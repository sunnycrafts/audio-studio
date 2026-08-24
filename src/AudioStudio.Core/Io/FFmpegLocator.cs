using System.Diagnostics;

namespace AudioStudio.Core.Io;

/// <summary>
/// Finds ffmpeg/ffprobe. Checks the app folder first so a portable copy can be
/// shipped alongside the exe, then PATH, then the usual install locations.
/// </summary>
public static class FFmpegLocator
{
    private static string? _ffmpeg;
    private static string? _ffprobe;
    private static bool _searched;
    private static readonly object Gate = new();

    public static string? FFmpegPath { get { EnsureSearched(); return _ffmpeg; } }
    public static string? FFprobePath { get { EnsureSearched(); return _ffprobe; } }
    public static bool IsAvailable => FFmpegPath is not null;

    private static readonly string[] CandidateDirectories =
    {
        @"C:\ffmpeg\bin",
        @"C:\Program Files\ffmpeg\bin",
        @"C:\Program Files (x86)\ffmpeg\bin",
    };

    private static void EnsureSearched()
    {
        if (_searched) return;
        lock (Gate)
        {
            if (_searched) return;
            _ffmpeg = Find("ffmpeg.exe");
            _ffprobe = Find("ffprobe.exe");
            _searched = true;
        }
    }

    private static string? Find(string exeName)
    {
        // 1. Next to the application (portable deployment). This must be the
        // real executable's folder — for a self-extracting single-file build
        // AppContext.BaseDirectory is a temp extraction folder, so an ffmpeg.exe
        // the user dropped beside the app would never be found.
        string appDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        foreach (var sub in new[] { "", "ffmpeg", Path.Combine("ffmpeg", "bin") })
        {
            string candidate = Path.Combine(appDir, sub, exeName);
            if (File.Exists(candidate)) return candidate;
        }

        // 2. PATH.
        string? pathVar = Environment.GetEnvironmentVariable("PATH");
        if (pathVar is not null)
        {
            foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    string candidate = Path.Combine(dir.Trim(), exeName);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException) { /* malformed PATH entry */ }
            }
        }

        // 3. Common install locations.
        foreach (var dir in CandidateDirectories)
        {
            string candidate = Path.Combine(dir, exeName);
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    public static string GetVersionString()
    {
        if (FFmpegPath is null) return "not found";
        try
        {
            var psi = new ProcessStartInfo(FFmpegPath, "-version")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc is null) return "unknown";
            string line = proc.StandardOutput.ReadLine() ?? "unknown";
            proc.WaitForExit(3000);
            return line;
        }
        catch (Exception ex)
        {
            return $"error: {ex.Message}";
        }
    }
}
