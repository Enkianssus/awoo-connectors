namespace QQMusicControlPoc;

/// <summary>
/// QQMusicApi classifies its host by a case-insensitive substring in the full
/// executable path. The ordinary connector name would select its internal
/// in-player route, although this connector is an external process.
/// </summary>
internal static class QQMusicWebHostPolicy
{
    internal const string HelperExecutableName = "Awoo.QqWebBridge.exe";

    internal static bool IsDotnetHost(string executable) =>
        string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase);

    internal static bool IsExternalApiHost(string executable) =>
        !string.IsNullOrWhiteSpace(executable) &&
        !executable.Contains("qqmusic.exe", StringComparison.OrdinalIgnoreCase);

    internal static string SelectExecutable(string currentExecutable, string applicationDirectory)
    {
        var executable = IsDotnetHost(currentExecutable)
            ? currentExecutable
            : Path.Combine(applicationDirectory, HelperExecutableName);
        if (!IsExternalApiHost(executable))
            throw new InvalidOperationException("helper-host-name-selects-internal-route");
        return executable;
    }
}
