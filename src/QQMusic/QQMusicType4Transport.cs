using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace QQMusicControlPoc;

// Single-use sender for the validated 22.71 native insertion scope.
// The return is a delivery receipt, never an insertion acknowledgement.
internal sealed class Type4Transport
{
    internal sealed record DeliveryReceipt(bool SendAttempted, bool TransportReturnedSuccess,
        int Win32Error, bool NativeOutcomeKnown = false);
    private const string ApiHash = "10ED93D19DDD4934111A7D789790DFA6953450F6F3DBD51E2CCF80BEB6B8F027";
    private const string WindowClass = "csQQMusicComApiWnd2017";
    private const string WindowTitle = "QQMusic_COM_WND_B2DA2B76_8235_4739_8298_89D994522476";
    private readonly string expectedExecutable;
    private readonly Action beforeAttempt;
    private int attempts;

    public Type4Transport(string executablePath, Action beforeAttempt)
    {
        if (!Path.IsPathFullyQualified(executablePath))
            throw new ArgumentException("Absolute verified QQ executable path required.");
        expectedExecutable = Path.GetFullPath(executablePath);
        this.beforeAttempt = beforeAttempt;
    }

    public static byte[] BuildPacket(long songId, int songType)
    {
        if (songId <= 0 || songId > uint.MaxValue || songType != 0)
            throw new ArgumentException("Only one positive UInt32 public song ID, type 0, is supported.");
        var text = FormattableString.Invariant($"/playbysongid cmd_count==1&&id_0=={songId}&&songtype_0==0");
        var body = Encoding.Unicode.GetBytes(text);
        var packet = new byte[18 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(0), (uint)packet.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), 0x514D4153);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(8), 100);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(10), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(14), (uint)body.Length);
        body.CopyTo(packet, 18);
        return packet;
    }

    // Deliberately not an Action: callers must explicitly handle an uncertain
    // post-send outcome before deciding when the native dispatch can be restored.
    // Ambiguous outcomes lock out this QQ process lifetime in the durable journal.
    public DeliveryReceipt SendOnce(int expectedProcessId, QQMusicSongReference song)
    {
        if (Interlocked.CompareExchange(ref attempts, 1, 0) != 0)
            throw new InvalidOperationException("This sender is consumed; retries are prohibited.");
        if (RuntimeInformation.ProcessArchitecture != Architecture.X86)
            throw new InvalidOperationException("Requires the reviewed x86 native insertion harness.");
        var packet = BuildPacket(song.SongId, song.SongType);
        using var process = Process.GetProcessById(expectedProcessId);
        if (process.HasExited || !string.Equals(process.MainModule?.FileName,
                expectedExecutable, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("QQ process identity changed.");
        var startedAt = process.StartTime;
        var apiPath = Path.Combine(Path.GetDirectoryName(expectedExecutable)!, "QQMusicApi.dll");
        // Hold the reviewed disk file against replacement for this bounded send.
        using var apiFile = new FileStream(apiPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (Convert.ToHexString(SHA256.HashData(apiFile)) != ApiHash)
            throw new InvalidOperationException("Unknown QQMusicApi wire implementation.");
        var modules = process.Modules.Cast<ProcessModule>()
            .Where(m => string.Equals(m.ModuleName, "QQMusicApi.dll", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (modules.Length != 1 || !string.Equals(modules[0].FileName, apiPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Expected QQ API module is not loaded from the reviewed installation.");

        var receivers = new List<nint>();
        EnumWindowsProc enumerate = (window, _) =>
        {
            GetWindowThreadProcessId(window, out var pid);
            if (pid != (uint)expectedProcessId) return true;
            var name = new StringBuilder(128);
            var title = new StringBuilder(128);
            GetClassNameW(window, name, name.Capacity);
            GetWindowTextW(window, title, title.Capacity);
            if (name.ToString() == WindowClass && title.ToString() == WindowTitle)
                receivers.Add(window);
            return true;
        };
        if (!EnumWindows(enumerate, 0) || receivers.Count != 1)
            throw new InvalidOperationException("A unique reviewed IPC receiver was not found; no launch fallback.");
        var receiver = receivers[0];
        GetWindowThreadProcessId(receiver, out var finalPid);
        if (finalPid != (uint)expectedProcessId || process.HasExited || process.StartTime != startedAt)
            throw new InvalidOperationException("Receiver/process identity changed before send.");

        var payload = Marshal.AllocHGlobal(packet.Length);
        try
        {
            Marshal.Copy(packet, 0, payload, packet.Length);
            var copyData = new CopyData { Data = 0, ByteCount = (uint)packet.Length, Pointer = payload };
            // Same envelope, flags and bounded timeout as the native sender.
            // Do not interpret receiver LRESULT or a successful return as queue acceptance.
            beforeAttempt(); // identity recheck + durable no-retry marker, before P/Invoke.
            try
            {
                var delivered = SendMessageTimeoutW(receiver, 0x4A, 0, ref copyData, 3, 5000, out _);
                var error = delivered == 0 ? Marshal.GetLastWin32Error() : 0;
                return new DeliveryReceipt(true, delivered != 0, error);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // Once P/Invoke was reached, do not unwind patch restoration
                // early. The native stage observation must still take place.
                return new DeliveryReceipt(true, false, exception.HResult);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(payload);
            GC.KeepAlive(enumerate);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CopyData { public nuint Data; public uint ByteCount; public nint Pointer; }
    private delegate bool EnumWindowsProc(nint window, nint parameter);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetClassNameW(nint window, StringBuilder result, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetWindowTextW(nint window, StringBuilder result, int size);
    [DllImport("user32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern nint SendMessageTimeoutW(nint window, uint message, nuint wParam,
        ref CopyData data, uint flags, uint timeout, out nuint result);
}
