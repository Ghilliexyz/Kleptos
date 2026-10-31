using System;
using System.Collections.Generic;
using System.Linq;
using Kleptos.Models;

namespace Kleptos.Services;

/// <summary>All-time numbers derived from the download history.</summary>
public sealed class LifetimeStats
{
    public int Total { get; init; }
    public int Succeeded { get; init; }
    public double TotalSizeMB { get; init; }
    public int Last7Days { get; init; }
    /// <summary>Downloads per day for the last 14 days, oldest first.</summary>
    public int[] Daily { get; init; } = Array.Empty<int>();
    public string TopSite { get; init; } = string.Empty;

    public double SuccessRate => Total > 0 ? Succeeded * 100.0 / Total : 0;

    public static LifetimeStats From(IReadOnlyCollection<HistoryEntry> history, DateTime? today = null)
    {
        var day = (today ?? DateTime.Now).Date;
        var daily = new int[14];
        foreach (var h in history)
        {
            int ago = (int)(day - h.When.Date).TotalDays;
            if (ago >= 0 && ago < 14) daily[13 - ago]++;
        }

        string topSite = history
            .Select(h => Host(h.Url))
            .Where(h => h.Length > 0)
            .GroupBy(h => h)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault() ?? string.Empty;

        return new LifetimeStats
        {
            Total = history.Count,
            Succeeded = history.Count(h => h.Succeeded),
            TotalSizeMB = history.Where(h => h.Succeeded).Sum(h => h.FileSizeMB),
            Last7Days = history.Count(h => (day - h.When.Date).TotalDays < 7),
            Daily = daily,
            TopSite = topSite,
        };
    }

    public static string Host(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return string.Empty;
        var host = uri.Host.ToLowerInvariant();
        foreach (var prefix in new[] { "www.", "m.", "music." })
            if (host.StartsWith(prefix)) host = host[prefix.Length..];
        if (host == "youtu.be") host = "youtube.com";
        return host;
    }
}
