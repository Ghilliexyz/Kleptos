using System;
using System.IO;

namespace Kleptos.Services;

public readonly struct CookiesExpiryReport
{
    public int Total { get; init; }
    public int Expired { get; init; }
    public int ExpiringSoon { get; init; }
    public int Session { get; init; }
    public DateTime? EarliestUpcomingExpiry { get; init; }
    public bool ParseFailed { get; init; }
}

public static class CookiesInspector
{
    /// <summary>
    /// Netscape cookies.txt: tab-separated, 7 fields. Field 5 (index 4) is a Unix expiry
    /// timestamp in seconds; 0 means a session cookie. Lines starting with '#' are comments
    /// (except the "#HttpOnly_" prefix, which precedes a real entry).
    /// </summary>
    public static CookiesExpiryReport Inspect(string path, DateTimeOffset? nowOverride = null)
    {
        const int soonWindowDays = 7;
        var now = (nowOverride ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        var soonCutoff = now + soonWindowDays * 24L * 60 * 60;

        int total = 0, expired = 0, soon = 0, session = 0;
        long? earliestUpcoming = null;

        try
        {
            foreach (var raw in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var line = raw.StartsWith("#HttpOnly_", StringComparison.Ordinal) ? raw.Substring("#HttpOnly_".Length) : raw;
                if (line.StartsWith("#", StringComparison.Ordinal)) continue;

                var fields = line.Split('\t');
                if (fields.Length < 7) continue;
                if (!long.TryParse(fields[4], out var expiry)) continue;

                total++;
                if (expiry == 0) { session++; continue; }
                if (expiry <= now) { expired++; continue; }
                if (expiry <= soonCutoff) soon++;
                if (earliestUpcoming is null || expiry < earliestUpcoming) earliestUpcoming = expiry;
            }
        }
        catch
        {
            return new CookiesExpiryReport { ParseFailed = true };
        }

        return new CookiesExpiryReport
        {
            Total = total,
            Expired = expired,
            ExpiringSoon = soon,
            Session = session,
            EarliestUpcomingExpiry = earliestUpcoming is null
                ? null
                : DateTimeOffset.FromUnixTimeSeconds(earliestUpcoming.Value).LocalDateTime,
        };
    }
}
