namespace UnifiedPlayerControlPoc;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 1 && args[0] == QQMusicControlPoc.QQMusicWebStatusTransport.HelperArgument)
            return QQMusicControlPoc.QQMusicWebStatusHost.RunFromStandardInput();

        if (args.Length == 1 && args[0] == QQMusicControlPoc.QQMusicWebPlayTransport.HelperArgument)
            return QQMusicControlPoc.QQMusicWebBridgeHost.RunFromStandardInput();

        if (args.Contains(
                "--diagnose-next-guard",
                StringComparer.OrdinalIgnoreCase))
        {
            using var audio = QQMusicAudioMuteScope.Capture();
            var timeline = await QQMusicTimelineProbe.TryCreateAsync();
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(
                new
                {
                    audio.CapturedSessionCount,
                    audio.CaptureError,
                    Timeline = timeline?.ReadSnapshot()
                },
                new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true
                }));
            return 0;
        }

        var backend = Environment.GetEnvironmentVariable("AWOO_QQMUSIC_BACKEND");
        if (string.IsNullOrWhiteSpace(backend) ||
            string.Equals(backend, "web", StringComparison.OrdinalIgnoreCase))
            return await ConnectorRuntime.RunAsync("qqmusic", new QQMusicWebPlayerAdapter());
        if (!string.IsNullOrWhiteSpace(backend) &&
            !string.Equals(backend, "native", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("Unknown QQ backend; use web or native. No fallback was attempted.");
            return 2;
        }

        return await ConnectorRuntime.RunAsync(
            "qqmusic",
            new QQMusicPlayerAdapter
            {
                // The transport still refuses every unknown build. This flag
                // merely enables the hash-locked compatibility profiles.
                AllowUnsafeNativeNext = true
            });
    }
}
