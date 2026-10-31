using System;
using System.IO;

namespace Kleptos.Services;

/// <summary>Per-user locations Kleptos writes to.</summary>
public static class AppPaths
{
    // DoNotVerify: without it .NET returns "" when the folder doesn't exist yet (e.g. no
    // ~/.config on a fresh Mac), and every path below would silently become relative to
    // whatever directory Kleptos was started from.
    private static string Folder(Environment.SpecialFolder f) =>
        Environment.GetFolderPath(f, Environment.SpecialFolderOption.DoNotVerify);

    /// <summary>%APPDATA%\Kleptos on Windows, ~/.config/Kleptos elsewhere.</summary>
    public static string DataDir => Path.Combine(Folder(Environment.SpecialFolder.ApplicationData), "Kleptos");

    public static string SettingsPath => Path.Combine(DataDir, "settings.json");
    public static string HistoryPath => Path.Combine(DataDir, "history.json");
    public static string DefaultArchivePath => Path.Combine(DataDir, "archive.txt");

    /// <summary>Writable folder for downloaded helper binaries (ffmpeg on macOS/Linux).</summary>
    public static string ToolsDir => Path.Combine(DataDir, "tools");

    public static string LogPath => Path.Combine(Folder(Environment.SpecialFolder.LocalApplicationData), "Kleptos", "log.txt");

    public static string DefaultDownloadsFolder => Path.Combine(Folder(Environment.SpecialFolder.UserProfile), "Downloads");
}
