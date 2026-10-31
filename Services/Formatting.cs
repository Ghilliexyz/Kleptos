using System;
using System.Globalization;

namespace Kleptos.Services;

public static class Formatting
{
    public static string Size(double mb)
    {
        if (mb <= 0) return "0 MB";
        if (mb >= 1024 * 1024) return $"{mb / (1024.0 * 1024.0):F2} TB";
        if (mb >= 1024) return $"{mb / 1024.0:F2} GB";
        if (mb >= 1) return $"{mb:F1} MB";
        return $"{mb * 1024.0:F0} KB";
    }

    public static string SizeFromBytes(long? bytes) =>
        bytes is long b && b > 0 ? Size(b / (1024.0 * 1024.0)) : "?";

    public static string Speed(double mbps)
    {
        if (mbps <= 0) return "-";
        if (mbps < 1) return $"{mbps * 1024:F0} KB/s";
        return $"{mbps:F1} MB/s";
    }

    public static string Duration(TimeSpan ts)
    {
        if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}h {ts.Minutes}m";
        if (ts.TotalMinutes >= 1) return $"{ts.Minutes}m {ts.Seconds}s";
        return $"{ts.TotalSeconds:F1}s";
    }

    /// <summary>Clock style, e.g. 4:05 or 1:02:03.</summary>
    public static string Clock(double totalSeconds)
    {
        if (totalSeconds < 0 || double.IsNaN(totalSeconds)) totalSeconds = 0;
        var ts = TimeSpan.FromSeconds((int)totalSeconds);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes}:{ts.Seconds:D2}";
    }

    /// <summary>Parses h:mm:ss, m:ss or plain seconds. Returns -1 when invalid.</summary>
    public static double ParseClock(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return -1;
        input = input.Trim();

        var parts = input.Split(':');
        if (parts.Length == 3 &&
            int.TryParse(parts[0], out int h) && int.TryParse(parts[1], out int m) && int.TryParse(parts[2], out int s) &&
            h >= 0 && m >= 0 && s >= 0)
            return h * 3600 + m * 60 + s;

        if (parts.Length == 2 &&
            int.TryParse(parts[0], out int m2) && int.TryParse(parts[1], out int s2) && m2 >= 0 && s2 >= 0)
            return m2 * 60 + s2;

        if (parts.Length == 1 &&
            double.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out double sec) && sec >= 0)
            return sec;

        return -1;
    }

    public static string Count(int n) => n.ToString("N0", CultureInfo.InvariantCulture);
}
