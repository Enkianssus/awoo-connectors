using KugouControlPoc;

static void Equal<T>(T expected, T actual, string label)
    where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException(
            $"{label}: expected {expected}, actual {actual}");
    }
}

static void True(bool value, string label)
{
    if (!value)
    {
        throw new InvalidOperationException($"{label}: expected true");
    }
}

static void False(bool value, string label)
{
    if (value)
    {
        throw new InvalidOperationException($"{label}: expected false");
    }
}

var spotlight = KugouTrackIdentityPolicy.ParseArtistAndTitle(
    "三角洲行动、SIENA- SPOTLIGHT HUNTER (焦点猎手)");
Equal("三角洲行动、SIENA", spotlight.Artist, "SPOTLIGHT artist parse");
Equal("SPOTLIGHT HUNTER", spotlight.Title, "SPOTLIGHT localized title parse");
True(spotlight.RemovedLocalizedAlias, "SPOTLIGHT alias removal");

var spotlightWithOtherSpacing = KugouTrackIdentityPolicy.ParseArtistAndTitle(
    "三角洲行动、SIENA -SPOTLIGHT HUNTER (焦点猎手)");
Equal(
    "SPOTLIGHT HUNTER",
    spotlightWithOtherSpacing.Title,
    "SPOTLIGHT alternate separator parse");

var dawn = KugouTrackIdentityPolicy.ParseArtistAndTitle(
    "三角洲行动、Lithium Done- Dawn (黎明将至) (...|)");
Equal("三角洲行动、Lithium Done", dawn.Artist, "Dawn artist parse");
Equal("Dawn", dawn.Title, "Dawn localized and metadata suffix parse");

True(
    KugouTrackIdentityPolicy.MetadataRepresentsSameSong(
        "SPOTLIGHT HUNTER",
        "三角洲行动、SIENA",
        "SPOTLIGHT HUNTER (焦点猎手)",
        "SIENA"),
    "SPOTLIGHT app-prefix/localized alias match");
True(
    KugouTrackIdentityPolicy.TracksRepresentSameSong(
        "",
        "Dawn (黎明将至) (...|)",
        "三角洲行动、Lithium Done",
        "390767001",
        "Dawn",
        "Lithium Done"),
    "Dawn fallback maps to confirmed numeric track");

True(
    KugouTrackIdentityPolicy.TracksRepresentSameSong(
        "493741552",
        "not-used",
        "not-used",
        "493741552",
        "SPOTLIGHT HUNTER",
        "三角洲行动、SIENA"),
    "same stable ID remains authoritative");
False(
    KugouTrackIdentityPolicy.TracksRepresentSameSong(
        "493741552",
        "SPOTLIGHT HUNTER",
        "三角洲行动、SIENA",
        "493741553",
        "SPOTLIGHT HUNTER (焦点猎手)",
        "SIENA"),
    "different stable IDs must not merge");

False(
    KugouTrackIdentityPolicy.MetadataRepresentsSameSong(
        "SPOTLIGHT HUNTER (Live)",
        "SIENA",
        "SPOTLIGHT HUNTER",
        "SIENA"),
    "Live version must remain distinct");
False(
    KugouTrackIdentityPolicy.MetadataRepresentsSameSong(
        "SPOTLIGHT HUNTER (Remix)",
        "SIENA",
        "SPOTLIGHT HUNTER",
        "SIENA"),
    "Remix version must remain distinct");
False(
    KugouTrackIdentityPolicy.MetadataRepresentsSameSong(
        "SPOTLIGHT HUNTER",
        "Other Artist",
        "SPOTLIGHT HUNTER (焦点猎手)",
        "SIENA"),
    "different artists must remain distinct");

True(
    KugouTrackIdentityPolicy.ShouldHoldTransientIni(
        "KuGou.ini",
        "POSTERGIRL-ID",
        "POSTERGIRL",
        "Artist",
        "OLD-ID",
        "Merry Christmas",
        "Old Habits",
        1),
    "first stale INI observation is held");
False(
    KugouTrackIdentityPolicy.ShouldHoldTransientIni(
        "KuGou.ini",
        "POSTERGIRL-ID",
        "POSTERGIRL",
        "Artist",
        "NEW-ID",
        "New Song",
        "New Artist",
        2),
    "repeated INI candidate is eventually accepted");
False(
    KugouTrackIdentityPolicy.ShouldHoldTransientIni(
        "WindowTitle",
        "POSTERGIRL-ID",
        "POSTERGIRL",
        "Artist",
        "OLD-ID",
        "Merry Christmas",
        "Old Habits",
        1),
    "authoritative WindowTitle change is not delayed");

var confirmedAt = new DateTimeOffset(
    2026,
    8,
    22,
    12,
    0,
    0,
    TimeSpan.Zero);
var staleIniWrite = confirmedAt.AddSeconds(-1);
True(
    KugouTrackIdentityPolicy.ShouldHoldTransientIni(
        "KuGou.ini",
        "POSTERGIRL-ID",
        "POSTERGIRL",
        "Artist",
        "OLD-ID",
        "Merry Christmas",
        "Old Habits",
        2,
        staleIniWrite,
        confirmedAt,
        confirmedAt.AddSeconds(1),
        confirmedAt.AddSeconds(2)),
    "old INI remains held after repeated callbacks");

var newIniWrite = confirmedAt.AddSeconds(1);
False(
    KugouTrackIdentityPolicy.ShouldHoldTransientIni(
        "KuGou.ini",
        "POSTERGIRL-ID",
        "POSTERGIRL",
        "Artist",
        "NEW-ID",
        "New Song",
        "New Artist",
        2,
        newIniWrite,
        confirmedAt,
        confirmedAt.AddMilliseconds(-500),
        confirmedAt),
    "new INI eventually transitions after debounce");

// FB-20261021-CDCE7CDE: the connector inserted 青藏高原 - 李娜 (303222240),
// and the title search resolved the same caption to another recording
// (317243695). The inserted song must win while the caption matches.
var inserted = new KugouInsertedTrackMemory<string>(capacity: 4);
inserted.Remember("李娜 - 青藏高原", "李娜", "青藏高原", "303222240");
var qinghai = KugouTrackIdentityPolicy.ParseArtistAndTitle("李娜 - 青藏高原");
Equal(
    "303222240",
    inserted.Find("李娜 - 青藏高原", qinghai.Artist, qinghai.Title) ?? "<none>",
    "inserted recording wins for its window caption");
var qinghaiSpacing = KugouTrackIdentityPolicy.ParseArtistAndTitle(
    "李娜 -青藏高原");
Equal(
    "303222240",
    inserted.Find(
        "李娜 -青藏高原",
        qinghaiSpacing.Artist,
        qinghaiSpacing.Title) ?? "<none>",
    "normalized artist/title key ignores caption spacing");
True(
    inserted.Find("青藏高原", string.Empty, "青藏高原") is null,
    "title-only caption still falls back to search");
True(
    inserted.Find("韩红 - 青藏高原", "韩红", "青藏高原") is null,
    "same title by another artist still falls back to search");
True(
    inserted.Find("李娜 - 青藏高原 (Live)", "李娜", "青藏高原 (Live)") is null,
    "Live caption is not the inserted studio recording");
True(
    inserted.Find("周杰伦 - 晴天", "周杰伦", "晴天") is null,
    "caption of a song the connector never inserted falls back to search");

var insertedAlias = new KugouInsertedTrackMemory<string>(capacity: 4);
insertedAlias.Remember(
    string.Empty,
    "SIENA",
    "SPOTLIGHT HUNTER (焦点猎手)",
    "493741552");
Equal(
    "493741552",
    insertedAlias.Find(
        "三角洲行动、SIENA- SPOTLIGHT HUNTER (焦点猎手)",
        spotlight.Artist,
        spotlight.Title) ?? "<none>",
    "inserted song matches the Delta Force caption alias");

// A mixed-search title can omit the version that the filename keeps; the
// caption KuGou shows for that file still maps to the inserted song.
var insertedByFilename = new KugouInsertedTrackMemory<string>(capacity: 4);
insertedByFilename.Remember(
    "李娜 - 青藏高原 (Live)",
    "李娜",
    "青藏高原",
    "live-317243695");
var liveCaption = KugouTrackIdentityPolicy.ParseArtistAndTitle(
    "李娜 - 青藏高原 (Live)");
False(
    KugouTrackIdentityPolicy.MetadataRepresentsSameSong(
        liveCaption.Title,
        liveCaption.Artist,
        "青藏高原",
        "李娜"),
    "Live caption differs from the song title alone");
Equal(
    "live-317243695",
    insertedByFilename.Find(
        "李娜 - 青藏高原 (Live)",
        liveCaption.Artist,
        liveCaption.Title) ?? "<none>",
    "filename sent to KuGou identifies its caption");

var reinserted = new KugouInsertedTrackMemory<string>(capacity: 4);
reinserted.Remember("李娜 - 青藏高原", "李娜", "青藏高原", "317243695");
reinserted.Remember("李娜 - 青藏高原", "李娜", "青藏高原", "303222240");
Equal(
    "303222240",
    reinserted.Find("李娜 - 青藏高原", qinghai.Artist, qinghai.Title) ?? "<none>",
    "newest insertion of the same caption wins");

var bounded = new KugouInsertedTrackMemory<string>(capacity: 2);
bounded.Remember("Artist One - Song One", "Artist One", "Song One", "1");
bounded.Remember("Artist Two - Song Two", "Artist Two", "Song Two", "2");
bounded.Remember("Artist Three - Song Three", "Artist Three", "Song Three", "3");
True(
    bounded.Find("Artist One - Song One", "Artist One", "Song One") is null,
    "oldest insertion is evicted at capacity");
Equal(
    "2",
    bounded.Find("Artist Two - Song Two", "Artist Two", "Song Two") ?? "<none>",
    "recent insertion within capacity remains");
Equal(
    "3",
    bounded.Find(
        "Artist Three - Song Three",
        "Artist Three",
        "Song Three") ?? "<none>",
    "newest insertion remains");

Console.WriteLine("KuGou track identity harness passed.");
