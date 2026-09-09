using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;

namespace QQMusicControlPoc;

// These are current-state observations, not a queue identity, revision, occurrence,
// audio-output proof, or acknowledgement of an earlier write.
internal sealed record QQMusicWebStatus(uint? SongId, int? SongType, int? SongPosition,
    int? PlayStatus, int? QueueSetting, string Code, bool Succeeded = false)
{
    internal static QQMusicWebStatus Failed(string code) => new(null, null, null, null, null,
        QQMusicWebStatusProtocol.IsSafeCode(code) ? code : "status-operation-failed");
}

internal sealed record QQMusicWebStatusRequest(Guid OperationId, int ProcessId,
    long ProcessStartTimeUtcTicks, string ExecutablePath, string ApiDllPath, string ApiSha256);
internal sealed record QQMusicWebStatusResponse(Guid OperationId, QQMusicWebStatus Status);

// Reserving before native entry is irreversible even when native entry throws.
// One write OR each of the two fixed reads once. A caller cannot mix modes.
internal sealed class QQMusicWebApiBudget
{
    private int reserved;
    internal void ReserveWrite() => Reserve(4);
    internal void ReserveCurrent() => Reserve(1);
    internal void ReserveQueueSetting() => Reserve(2);
    private void Reserve(int bit)
    {
        for (;;)
        {
            var before = Volatile.Read(ref reserved);
            if ((before & bit) != 0 || (bit == 4 ? before != 0 : (before & 4) != 0))
                throw new InvalidOperationException("dispatch-budget-exhausted");
            if (Interlocked.CompareExchange(ref reserved, before | bit, before) == before) return;
        }
    }
}

internal static class QQMusicWebStatusProtocol
{
    internal const int MaximumXmlBytes = 65_536;
    internal const int MaximumWireCharacters = 4096;
    internal const string QueryCurrentXml = "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
        + "<command-lable-xwl78-qq-music><cmd value=\"1074\"><subcmd value=\"QueryPlayStatus\"/>"
        + "</cmd></command-lable-xwl78-qq-music>";
    internal const string QueryQueueSettingXml = "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
        + "<command-lable-xwl78-qq-music><cmd value=\"1074\"><subcmd value=\"QueryPlayQueueSetting\"/>"
        + "</cmd></command-lable-xwl78-qq-music>";
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8
    };

    internal static QQMusicWebStatus Parse(string? currentXml, string? settingXml)
    {
        if (!TryAttributes(currentXml, current: true, out var current) ||
            !TryAttributes(settingXml, current: false, out var setting))
            return QQMusicWebStatus.Failed("status-response-invalid");
        if (!current.TryGetValue("songID", out var idText) || !Unsigned(idText) ||
            !uint.TryParse(idText, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ||
            !Signed(current.GetValueOrDefault("songtype"), out var type) || type < -1 ||
            !Signed(current.GetValueOrDefault("songpos"), out var position) || position < -1 ||
            !Signed(current.GetValueOrDefault("playstatus"), out var playStatus) || playStatus < -1 ||
            !current.TryGetValue("listkey", out var listKey) ||
            (listKey.Length != 0 && (!Unsigned(listKey) || !ulong.TryParse(listKey,
                NumberStyles.None, CultureInfo.InvariantCulture, out _))) ||
            !Signed(setting.GetValueOrDefault("Setting"), out var queueSetting) || queueSetting < 0)
            return QQMusicWebStatus.Failed("status-response-invalid");
        return new(id == 0 ? null : id, type == -1 ? null : type,
            position == -1 ? null : position, playStatus == -1 ? null : playStatus,
            queueSetting, "status-read", true);
    }

    private static bool TryAttributes(string? xml, bool current, out Dictionary<string, string> values)
    {
        values = new(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(xml) || xml.Length > MaximumXmlBytes ||
            Encoding.UTF8.GetByteCount(xml) > MaximumXmlBytes) return false;
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                MaxCharactersInDocument = MaximumXmlBytes, MaxCharactersFromEntities = MaximumXmlBytes,
                IgnoreComments = true, IgnoreProcessingInstructions = true,
                ConformanceLevel = ConformanceLevel.Document
            });
            var songElements = 0;
            var songDepth = -1;
            while (reader.Read())
            {
                if (reader.Depth > 32) return false;
                if (reader.NodeType == XmlNodeType.Element)
                {
                    if (current && songDepth >= 0 && reader.Depth > songDepth) return false;
                    var isSong = reader.LocalName == "songinfo";
                    if (current && isSong)
                    {
                        if (++songElements != 1 || reader.NamespaceURI.Length != 0) return false;
                        songDepth = reader.IsEmptyElement ? -1 : reader.Depth;
                    }
                    if ((current && isSong) || !current)
                    {
                        if (reader.MoveToFirstAttribute())
                        {
                            do
                            {
                                var wanted = current ? reader.LocalName is "songID" or "songtype" or
                                    "listkey" or "songpos" or "playstatus" : reader.LocalName == "Setting";
                                if (wanted && (reader.NamespaceURI.Length != 0 ||
                                    !values.TryAdd(reader.Name, reader.Value))) return false;
                            } while (reader.MoveToNextAttribute());
                            reader.MoveToElement();
                        }
                        if (!current && values.Count != 0 && reader.NamespaceURI.Length != 0) return false;
                    }
                }
                else if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == songDepth) songDepth = -1;
                else if (current && songDepth >= 0 && reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA &&
                    !string.IsNullOrWhiteSpace(reader.Value)) return false;
            }
            return current ? songElements == 1 : values.Count == 1;
        }
        catch (XmlException) { return false; }
    }

    private static bool Unsigned(string value) => value.Length > 0 && value.All(c => c is >= '0' and <= '9');
    private static bool Signed(string? value, out int parsed)
    {
        parsed = 0;
        return value is not null && (Unsigned(value) || value.StartsWith('-') && Unsigned(value[1..])) &&
            int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out parsed);
    }

    internal static void ValidateRequest(QQMusicWebStatusRequest request)
    {
        if (request.OperationId == Guid.Empty || request.ProcessId <= 0 || request.ProcessStartTimeUtcTicks <= 0)
            throw new InvalidOperationException("status-request-invalid");
        if (string.IsNullOrWhiteSpace(request.ExecutablePath) || string.IsNullOrWhiteSpace(request.ApiDllPath) ||
            !Path.IsPathFullyQualified(request.ExecutablePath) || !Path.IsPathFullyQualified(request.ApiDllPath) ||
            request.ExecutablePath.StartsWith(@"\\", StringComparison.Ordinal) ||
            request.ApiDllPath.StartsWith(@"\\", StringComparison.Ordinal))
            throw new InvalidOperationException("status-target-path-invalid");
        if (!string.Equals(Path.GetFileName(request.ExecutablePath), "QQMusic.exe", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(request.ApiDllPath), "QQMusicApi.dll", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetDirectoryName(Path.GetFullPath(request.ExecutablePath)),
                Path.GetDirectoryName(Path.GetFullPath(request.ApiDllPath)), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("status-target-path-invalid");
        if (request.ApiSha256 is null || request.ApiSha256.Length != 64 || !request.ApiSha256.All(Uri.IsHexDigit))
            throw new InvalidOperationException("status-api-hash-invalid");
    }

    internal static void ValidateApi(Stream stream, string expectedHash)
    {
        stream.Position = 0;
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(stream), Convert.FromHexString(expectedHash)))
            throw new InvalidOperationException("status-api-hash-mismatch");
        stream.Position = 0;
        Span<byte> dos = stackalloc byte[64];
        stream.ReadExactly(dos);
        if (dos[0] != 'M' || dos[1] != 'Z') throw new InvalidOperationException("status-api-invalid-pe");
        var offset = BinaryPrimitives.ReadInt32LittleEndian(dos[0x3c..]);
        if (offset < 64 || offset > stream.Length - 26) throw new InvalidOperationException("status-api-invalid-pe");
        stream.Position = offset;
        Span<byte> pe = stackalloc byte[26];
        stream.ReadExactly(pe);
        var optionalBytes = BinaryPrimitives.ReadUInt16LittleEndian(pe[20..]);
        if (!pe[..4].SequenceEqual(new byte[] { (byte)'P', (byte)'E', 0, 0 }) ||
            BinaryPrimitives.ReadUInt16LittleEndian(pe[4..]) != 0x014c ||
            BinaryPrimitives.ReadUInt16LittleEndian(pe[24..]) != 0x010b ||
            BinaryPrimitives.ReadUInt16LittleEndian(pe[6..]) == 0 || optionalBytes < 96 ||
            (BinaryPrimitives.ReadUInt16LittleEndian(pe[22..]) & 0x2000) == 0 ||
            (long)offset + 24 + optionalBytes > stream.Length)
            throw new InvalidOperationException("status-api-invalid-pe");
    }

    internal static QQMusicWebStatus ParseResponse(string json, Guid operationId)
    {
        if (json.Length > MaximumWireCharacters) return QQMusicWebStatus.Failed("status-helper-output-invalid");
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            if (!UniqueProperties(document.RootElement)) return QQMusicWebStatus.Failed("status-helper-output-invalid");
            var response = JsonSerializer.Deserialize<QQMusicWebStatusResponse>(json, JsonOptions);
            if (response?.OperationId != operationId || response.Status is null || !IsSafeCode(response.Status.Code))
                return QQMusicWebStatus.Failed("status-helper-output-invalid");
            var status = response.Status;
            if (!status.Succeeded) return QQMusicWebStatus.Failed(status.Code);
            if (status.Code != "status-read" || status.SongId == 0 || status.SongType < 0 ||
                status.SongPosition < 0 || status.PlayStatus < 0 || status.QueueSetting is null or < 0)
                return QQMusicWebStatus.Failed("status-helper-output-invalid");
            return status;
        }
        catch (JsonException) { return QQMusicWebStatus.Failed("status-helper-output-invalid"); }
    }

    private static bool UniqueProperties(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return false;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!names.Add(property.Name) || property.Value.ValueKind == JsonValueKind.Object &&
                !UniqueProperties(property.Value)) return false;
        return true;
    }

    internal static bool IsSafeCode(string? code) => code is "status-read" or "status-response-invalid" or
        "status-request-invalid" or "status-target-path-invalid" or "status-api-hash-invalid" or
        "status-api-hash-mismatch" or "status-api-invalid-pe" or "status-target-reparse-point" or
        "status-target-unavailable" or "status-target-epoch-changed" or "status-target-path-changed" or
        "status-window-unavailable" or "status-helper-host-invalid" or "status-requires-x86-sta" or
        "status-helper-deadline" or "status-cancelled" or "status-helper-output-invalid" or
        "status-helper-exit-failed" or "status-operation-failed";
}
