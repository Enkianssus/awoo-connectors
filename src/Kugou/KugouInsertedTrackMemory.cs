namespace KugouControlPoc;

/// <summary>
/// The songs this connector itself handed to KuGou through IPC, newest
/// first.  KuGou exposes only the playing caption, and a title search can
/// resolve that caption to another recording with the same title and
/// artist.  A caption matches a remembered song by its normalized
/// artist/title keys, by the filename sent to KuGou, or by the title+artist
/// rule the search itself accepts; the newest match wins and every other
/// caption still falls back to the search.
/// </summary>
internal sealed class KugouInsertedTrackMemory<T>
    where T : class
{
    private readonly object _sync = new();
    private readonly List<Entry> _entries = [];
    private readonly int _capacity;

    internal KugouInsertedTrackMemory(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
    }

    internal void Remember(
        string? filename,
        string? artist,
        string? title,
        T value)
    {
        var keys = KugouTrackIdentityPolicy.BuildIdentityKeys(
            filename,
            artist,
            title);
        if (keys.Count == 0)
        {
            return;
        }

        var entry = new Entry(
            new HashSet<string>(keys, StringComparer.Ordinal),
            artist?.Trim() ?? string.Empty,
            title?.Trim() ?? string.Empty,
            value);
        lock (_sync)
        {
            // A newer insertion of the same caption replaces the older
            // recording instead of competing with it.
            _entries.RemoveAll(existing => existing.Keys.Overlaps(entry.Keys));
            _entries.Insert(0, entry);
            if (_entries.Count > _capacity)
            {
                _entries.RemoveRange(_capacity, _entries.Count - _capacity);
            }
        }
    }

    internal T? Find(
        string? rawTitle,
        string? artist,
        string? title)
    {
        var keys = KugouTrackIdentityPolicy.BuildIdentityKeys(
            rawTitle,
            artist,
            title);
        if (keys.Count == 0)
        {
            return null;
        }

        lock (_sync)
        {
            foreach (var entry in _entries)
            {
                if (entry.Keys.Overlaps(keys)
                    || KugouTrackIdentityPolicy.MetadataRepresentsSameSong(
                        title,
                        artist,
                        entry.Title,
                        entry.Artist))
                {
                    return entry.Value;
                }
            }
        }

        return null;
    }

    private sealed record Entry(
        HashSet<string> Keys,
        string Artist,
        string Title,
        T Value);
}
