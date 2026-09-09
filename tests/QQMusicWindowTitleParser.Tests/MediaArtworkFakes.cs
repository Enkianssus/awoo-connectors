// Pure memory substitutes: never open a file, player, network or Windows API.
namespace Windows.Storage.Streams;

internal interface IRandomAccessStreamReference
{
    Task<FakeRandomAccessStream> OpenReadAsync();
}

internal sealed class RandomAccessStreamReference(Func<Task<FakeRandomAccessStream>> open) : IRandomAccessStreamReference
{
    public Task<FakeRandomAccessStream> OpenReadAsync() => open();
}

internal sealed class FakeRandomAccessStream(Stream stream, ulong? reportedSize = null) : IDisposable
{
    public Stream Stream { get; } = stream;
    public ulong Size => reportedSize ?? (ulong)Stream.Length;
    public bool Disposed { get; private set; }
    public void Dispose() { Disposed = true; Stream.Dispose(); }
}

internal static class FakeArtworkStreamExtensions
{
    public static Stream AsStreamForRead(this FakeRandomAccessStream stream) => stream.Stream;
    public static Task<T> AsTask<T>(this Task<T> task, CancellationToken token) => task.WaitAsync(token);
}
