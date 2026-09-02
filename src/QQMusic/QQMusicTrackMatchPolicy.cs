using System.Text;
using System.Text.RegularExpressions;

namespace UnifiedPlayerControlPoc;

/// <summary>
/// Keeps QQ catalog metadata and the desktop/GSMTC metadata on one narrow
/// equivalence rule. QQ has used several explicit labels for instrumental
/// recordings, but other version labels remain authoritative.
/// </summary>
internal static class QQMusicTrackMatchPolicy
{
    private static readonly char[] ArtistSeparators =
        ['、', '，', ',', ';', '；', '/', '&'];

    private static readonly Regex InstrumentalSuffix = new(
        @"\((?:纯音乐|inst\.?|instrumental)\)$",
        RegexOptions.IgnoreCase
            | RegexOptions.CultureInvariant
            | RegexOptions.Compiled);

    internal static bool MetadataRepresentsSameSong(
        string? firstTitle,
        string? firstArtist,
        string? secondTitle,
        string? secondArtist)
    {
        if (!TitlesRepresentSameSong(firstTitle, secondTitle))
        {
            return false;
        }

        var normalizedFirstArtist = NormalizeArtist(firstArtist);
        var normalizedSecondArtist = NormalizeArtist(secondArtist);
        return !string.IsNullOrWhiteSpace(normalizedFirstArtist)
            && !string.IsNullOrWhiteSpace(normalizedSecondArtist)
            && normalizedFirstArtist == normalizedSecondArtist;
    }

    /// <summary>
    /// Matches only metadata updates for one already-observed QQ track. The
    /// title remains exact after normalization; artist metadata may gain or
    /// lose collaborators while QQ fills its session fields, but it must not
    /// introduce a disjoint artist token. This deliberately does not share
    /// the instrumental-title aliases or relaxed artist rule used here with
    /// strict target matching.
    /// </summary>
    internal static bool ObservationMetadataRepresentsSameSong(
        string? firstTitle,
        string? firstArtist,
        string? secondTitle,
        string? secondArtist)
    {
        var normalizedFirstTitle = NormalizeText(firstTitle);
        var normalizedSecondTitle = NormalizeText(secondTitle);
        if (string.IsNullOrWhiteSpace(normalizedFirstTitle)
            || normalizedFirstTitle != normalizedSecondTitle)
        {
            return false;
        }

        var firstArtists = TokenizeArtist(firstArtist);
        var secondArtists = TokenizeArtist(secondArtist);
        if (firstArtists.Count == 0 || secondArtists.Count == 0)
        {
            return false;
        }

        return firstArtists.SetEquals(secondArtists)
            || firstArtists.IsSubsetOf(secondArtists)
            || secondArtists.IsSubsetOf(firstArtists);
    }

    /// <summary>
    /// Applies the narrow observation rule to complete player tracks. A
    /// stable native ID is authoritative when both observations have one:
    /// equal IDs are the same track even while metadata is incomplete, while
    /// different IDs cannot be collapsed into an artist/title update.
    /// </summary>
    internal static bool TracksRepresentSameObservation(
        string? firstId,
        string? firstTitle,
        string? firstArtist,
        string? secondId,
        string? secondTitle,
        string? secondArtist)
    {
        var normalizedFirstId = firstId?.Trim() ?? string.Empty;
        var normalizedSecondId = secondId?.Trim() ?? string.Empty;
        var firstHasStableId = IsStableTrackId(normalizedFirstId);
        var secondHasStableId = IsStableTrackId(normalizedSecondId);
        if (firstHasStableId && secondHasStableId)
        {
            return normalizedFirstId == normalizedSecondId;
        }

        return ObservationMetadataRepresentsSameSong(
            firstTitle,
            firstArtist,
            secondTitle,
            secondArtist);
    }

    internal static bool TracksRepresentSameSong(
        string? actualId,
        string? actualTitle,
        string? actualArtist,
        string? expectedId,
        string? expectedTitle,
        string? expectedArtist)
    {
        var actualIdentity = actualId?.Trim() ?? string.Empty;
        var expectedIdentity = expectedId?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(actualIdentity)
            && actualIdentity == expectedIdentity)
        {
            return true;
        }

        if (IsStableTrackId(actualIdentity)
            && IsStableTrackId(expectedIdentity)
            && actualIdentity != expectedIdentity)
        {
            return false;
        }

        if (!TitlesRepresentSameSong(actualTitle, expectedTitle))
        {
            return false;
        }

        var normalizedExpectedArtist = NormalizeArtist(expectedArtist);
        if (string.IsNullOrWhiteSpace(normalizedExpectedArtist))
        {
            return true;
        }

        var normalizedActualArtist = NormalizeArtist(actualArtist);
        return !string.IsNullOrWhiteSpace(normalizedActualArtist)
            && normalizedActualArtist == normalizedExpectedArtist;
    }

    private static bool TitlesRepresentSameSong(
        string? firstTitle,
        string? secondTitle)
    {
        var normalizedFirst = NormalizeText(firstTitle);
        var normalizedSecond = NormalizeText(secondTitle);
        if (string.IsNullOrWhiteSpace(normalizedFirst)
            || string.IsNullOrWhiteSpace(normalizedSecond))
        {
            return false;
        }
        if (normalizedFirst == normalizedSecond)
        {
            return true;
        }

        var firstIsInstrumental = InstrumentalSuffix.IsMatch(normalizedFirst);
        var secondIsInstrumental = InstrumentalSuffix.IsMatch(normalizedSecond);
        return firstIsInstrumental
            && secondIsInstrumental
            && InstrumentalSuffix.Replace(normalizedFirst, string.Empty)
                == InstrumentalSuffix.Replace(normalizedSecond, string.Empty);
    }

    private static string NormalizeArtist(string? value)
    {
        return NormalizeText(value)
            .Replace('、', '/')
            .Replace('，', '/')
            .Replace(',', '/')
            .Replace(';', '/')
            .Replace('；', '/')
            .Replace('&', '/');
    }

    private static HashSet<string> TokenizeArtist(string? value)
    {
        return NormalizeText(value)
            .Split(ArtistSeparators, StringSplitOptions.RemoveEmptyEntries)
            .Where(token => !string.IsNullOrWhiteSpace(token))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string NormalizeText(string? value)
    {
        var normalized = (value ?? string.Empty)
            .Normalize(System.Text.NormalizationForm.FormKC)
            .ToUpperInvariant();
        var builder = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            if (!char.IsWhiteSpace(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    private static bool IsStableTrackId(string value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Length <= 128
            && !value.Contains('|', StringComparison.Ordinal);
    }
}
