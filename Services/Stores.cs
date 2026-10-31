using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kleptos.Models;

namespace Kleptos.Services;

/// <summary>JSON persistence for settings and history. Never throws.</summary>
public static class Stores
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        // Older builds stored HistoryEntry as public fields; reading with IncludeFields keeps
        // anything that did get written.
        IncludeFields = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static KleptosSettings LoadSettings(string? path = null)
    {
        path ??= AppPaths.SettingsPath;
        try
        {
            if (File.Exists(path))
            {
                var s = JsonSerializer.Deserialize<KleptosSettings>(File.ReadAllText(path), Options);
                if (s != null) return Normalize(s);
            }
        }
        catch { /* ignore corrupt settings */ }
        return new KleptosSettings();
    }

    /// <summary>Clamps values a hand-edited or older settings file could get wrong.</summary>
    public static KleptosSettings Normalize(KleptosSettings s)
    {
        s.MaxParallelDownloads = Math.Clamp(s.MaxParallelDownloads, 1, 6);
        s.ConcurrentFragments = Math.Clamp(s.ConcurrentFragments, 1, 16);
        s.Retries = Math.Clamp(s.Retries, 0, 100);
        if (string.IsNullOrWhiteSpace(s.FilenameTemplate)) s.FilenameTemplate = "%(title)s";

        // Older builds only had a cookies.txt path; carry it over once.
        if (!s.CookieSourceMigrated)
        {
            if (s.CookieSource == CookieSource.None && !string.IsNullOrWhiteSpace(s.LastCookiesFile))
                s.CookieSource = CookieSource.File;
            s.CookieSourceMigrated = true;
        }
        return s;
    }

    public static void SaveSettings(KleptosSettings settings, string? path = null) =>
        Write(path ?? AppPaths.SettingsPath, settings);

    public static List<HistoryEntry> LoadHistory(string? path = null)
    {
        path ??= AppPaths.HistoryPath;
        try
        {
            if (File.Exists(path))
            {
                var h = JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(path), Options);
                // Entries written by the old field-based model came out as "{}"; drop those.
                if (h != null) return h.Where(e => !string.IsNullOrWhiteSpace(e.Url)).ToList();
            }
        }
        catch { /* ignore corrupt history */ }
        return new List<HistoryEntry>();
    }

    public static void SaveHistory(List<HistoryEntry> history, string? path = null) =>
        Write(path ?? AppPaths.HistoryPath, history);

    private static void Write<T>(string path, T value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
            File.Move(tmp, path, overwrite: true);
        }
        catch { /* never crash on save */ }
    }
}
