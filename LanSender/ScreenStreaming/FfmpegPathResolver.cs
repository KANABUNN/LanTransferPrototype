namespace LanSender.ScreenStreaming;

public static class FfmpegPathResolver
{
    public static string ResolveFfmpegPath(string? configuredPath = null)
    {
        return ResolveExecutable(
            configuredPath,
            Environment.GetEnvironmentVariable("LAN_FFMPEG_PATH"),
            "ffmpeg.exe");
    }

    private static string ResolveExecutable(string? configuredPath, string? envPath, string exeName)
    {
        foreach (string? candidate in EnumerateCandidates(configuredPath, envPath, exeName))
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            string expanded = Environment.ExpandEnvironmentVariables(candidate.Trim().Trim('"'));
            if (File.Exists(expanded))
            {
                return Path.GetFullPath(expanded);
            }
        }

        return Path.GetFileNameWithoutExtension(exeName);
    }

    private static IEnumerable<string?> EnumerateCandidates(string? configuredPath, string? envPath, string exeName)
    {
        yield return configuredPath;
        yield return envPath;

        string baseDir = AppContext.BaseDirectory;
        string current = Directory.GetCurrentDirectory();

        string[] roots =
        [
            current,
            baseDir,
            Path.Combine(current, "ffmpeg"),
            Path.Combine(current, "ffmpeg", "bin"),
            Path.Combine(current, "tools"),
            Path.Combine(current, "tools", "ffmpeg"),
            Path.Combine(current, "tools", "ffmpeg", "bin"),
            Path.Combine(current, "tools", "bin"),
            Path.Combine(baseDir, "ffmpeg"),
            Path.Combine(baseDir, "ffmpeg", "bin"),
            Path.Combine(baseDir, "tools"),
            Path.Combine(baseDir, "tools", "ffmpeg"),
            Path.Combine(baseDir, "tools", "ffmpeg", "bin"),
            Path.Combine(baseDir, "tools", "bin"),
        ];

        foreach (string root in roots)
        {
            yield return Path.Combine(root, exeName);
        }
    }
}