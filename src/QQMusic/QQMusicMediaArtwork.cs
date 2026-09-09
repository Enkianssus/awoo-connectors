using Windows.Storage.Streams;

namespace UnifiedPlayerControlPoc;

/// <summary>Reads only the thumbnail captured with the selected session's metadata.</summary>
internal static class QQMusicMediaArtwork
{
    internal const int MaximumBytes = 256 * 1024;

    internal static async Task<string> ReadAsync(
        IRandomAccessStreamReference reference, CancellationToken cancellationToken)
    {
        using var randomAccess = await reference.OpenReadAsync()
            .AsTask(cancellationToken).ConfigureAwait(false);
        if (randomAccess.Size is 0 or > MaximumBytes) return string.Empty;
        using var stream = randomAccess.AsStreamForRead();
        return await ReadBoundedAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<string> ReadBoundedAsync(
        Stream stream, CancellationToken cancellationToken)
    {
        // One extra byte detects oversized data even when Size/Length is false.
        var bytes = new byte[MaximumBytes + 1];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(count), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0) break;
            count += read;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (count == 0 || count > MaximumBytes) return string.Empty;
        return Encode(bytes, count);
    }

    private static string Encode(byte[] bytes, int count)
    {
        var data = bytes.AsSpan(0, count);
        // Derive a fixed MIME from bytes, never from a path or supplier string.
        var mime = data.Length >= 8 && data[..8].SequenceEqual(
            new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ? "image/png" :
            data.Length >= 5 && data[0] == 0xff && data[1] == 0xd8 &&
            data[2] == 0xff && data[^2] == 0xff && data[^1] == 0xd9 ? "image/jpeg" : null;
        return mime is null ? string.Empty :
            $"data:{mime};base64,{Convert.ToBase64String(bytes, 0, count)}";
    }
}
