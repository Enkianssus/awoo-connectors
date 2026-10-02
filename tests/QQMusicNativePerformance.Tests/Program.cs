using QQMusicControlPoc;

var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    checks++;
}
static List<int> OriginalPattern(byte[] bytes, byte[] pattern, int rva)
{
    var result = new List<int>();
    for (var index = 0; index <= bytes.Length - pattern.Length; index++)
        if (bytes.AsSpan(index, pattern.Length).SequenceEqual(pattern)) result.Add(rva + index);
    return result;
}
void Match(byte[] bytes, byte[] pattern, int rva, string name)
{
    var actual = new List<int>();
    QQMusicNativeNextAnalyzer.ScanPattern(bytes, pattern, rva, actual);
    Check(actual.SequenceEqual(OriginalPattern(bytes, pattern, rva)), name);
}
Match([], [], 4096, "empty scan matches original");
Match([1, 1, 1, 1], [1, 1], 4096, "overlapping matches retained");
Match([1, 2, 3], [2, 3], 4096, "final complete match retained");
Match([1, 2, 3], [3, 4], 4096, "truncated pattern is absent");
Match([1], [1, 2], 4096, "pattern exceeds section");
var boundary = new List<int>();
QQMusicNativeNextAnalyzer.ScanPattern(new byte[] { 9, 1 }, new byte[] { 1, 2 }, 4096, boundary);
QQMusicNativeNextAnalyzer.ScanPattern(new byte[] { 2, 9 }, new byte[] { 1, 2 }, 8192, boundary);
Check(boundary.Count == 0, "matches never cross PE sections");
var random = new Random(47271);
for (var sample = 0; sample < 1200; sample++)
{
    var bytes = new byte[random.Next(0, 1024)]; random.NextBytes(bytes);
    var pattern = new byte[random.Next(0, 25)]; random.NextBytes(pattern);
    if (sample % 4 == 0) { Array.Fill(bytes, (byte)1); Array.Fill(pattern, (byte)1); }
    if (pattern.Length <= bytes.Length && sample % 4 == 1) pattern.CopyTo(bytes, bytes.Length - pattern.Length);
    Match(bytes, pattern, 0x2000, "random pattern " + sample);
    var target = random.Next();
    if (bytes.Length >= 5 && sample % 2 == 0)
    {
        var at = sample % 4 == 0 ? bytes.Length - 5 : random.Next(bytes.Length - 4);
        bytes[at] = 0xE8;
        BitConverter.GetBytes(unchecked(target - (0x2000 + at + 5))).CopyTo(bytes, at + 1);
    }
    var expected = new List<int>();
    for (var index = 0; index <= bytes.Length - 5; index++)
        if (bytes[index] == 0xE8 && unchecked(0x2000 + index + 5 + BitConverter.ToInt32(bytes, index + 1)) == target)
            expected.Add(0x2000 + index);
    var actual = new List<int>();
    QQMusicNativeNextAnalyzer.ScanRelativeCalls(bytes, 0x2000, target, actual);
    Check(actual.SequenceEqual(expected), "random relative call " + sample);
}
// Invalid identities must be rejected before any Windows process enumeration.
foreach (var invalidPid in new[] { 0, -1, int.MinValue })
{
    var rejected = false;
    try { _ = QQMusicNativeController.ReadPlaybackState(invalidPid); }
    catch (ArgumentOutOfRangeException) { rejected = true; }
    Check(rejected, "invalid expected PID rejected");
}
Console.WriteLine($"PASS {checks} native performance equivalence checks; no QQ process reads, writes, transport or UI calls.");
