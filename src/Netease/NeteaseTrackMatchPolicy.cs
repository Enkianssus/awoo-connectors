using System.Text;

namespace UnifiedPlayerControlPoc;

/// <summary>
/// Compares a requested NetEase track with metadata observed from the player.
/// Stable platform IDs remain authoritative; title/artist matching is only a
/// fallback for the short period in which the player exposes a title-derived
/// ID or no ID at all.
/// </summary>
internal static class NeteaseTrackMatchPolicy
{
    public static bool TracksMatch(
        PlayerTrack? actual,
        PlayerTrack expected)
    {
        if (actual is null)
        {
            return false;
        }

        var actualId = actual.Id.Trim();
        var expectedId = expected.Id.Trim();
        if (IsStableId(actualId) && IsStableId(expectedId))
        {
            return string.Equals(
                actualId,
                expectedId,
                StringComparison.Ordinal);
        }

        return NormalizeText(actual.Title) == NormalizeText(expected.Title)
            && (string.IsNullOrWhiteSpace(expected.Artist)
                || NormalizeText(actual.Artist)
                == NormalizeText(expected.Artist));
    }

    public static string NormalizeText(string value)
    {
        return string.Join(
            " ",
            value
                .Trim()
                // NFKC makes NFC/NFD and compatibility spellings compare
                // equally without stripping a meaningful accent.
                .Normalize(NormalizationForm.FormKC)
                .ToUpperInvariant()
                .Replace('‐', '-')
                .Replace('‑', '-')
                .Replace('‒', '-')
                .Replace('–', '-')
                .Replace('—', '-')
                .Replace('―', '-')
                .Replace('﹘', '-')
                .Replace('﹣', '-')
                .Replace('－', '-')
                .Split(
                    (char[]?)null,
                    StringSplitOptions.TrimEntries
                    | StringSplitOptions.RemoveEmptyEntries));
    }

    private static bool IsStableId(string value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.IndexOf('|') < 0;
    }
}
