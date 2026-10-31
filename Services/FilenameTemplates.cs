using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Kleptos.Services;

public sealed record TemplatePreset(string Name, string Template);

/// <summary>Filename template presets and a local preview renderer (no yt-dlp call needed).</summary>
public static class FilenameTemplates
{
    public static readonly IReadOnlyList<TemplatePreset> Presets = new[]
    {
        new TemplatePreset("Title", "%(title)s"),
        new TemplatePreset("Title [id]", "%(title)s [%(id)s]"),
        new TemplatePreset("Channel - Title", "%(uploader)s - %(title)s"),
        new TemplatePreset("Date - Title", "%(upload_date>%Y-%m-%d)s - %(title)s"),
        new TemplatePreset("Folder per channel", "%(uploader)s/%(title)s"),
        new TemplatePreset("Folder per channel, dated", "%(uploader)s/%(upload_date>%Y-%m-%d)s - %(title)s"),
        new TemplatePreset("Folder per playlist, numbered", "%(playlist_title,uploader)s/%(playlist_index)03d - %(title)s"),
    };

    private static readonly Dictionary<string, string> SampleValues = new(StringComparer.OrdinalIgnoreCase)
    {
        ["title"] = "Never Gonna Build It Myself",
        ["id"] = "dQw4w9WgXcQ",
        ["uploader"] = "Some Channel",
        ["channel"] = "Some Channel",
        ["playlist_title"] = "Favourites",
        ["playlist"] = "Favourites",
        ["playlist_index"] = "7",
        ["ext"] = "mp4",
        ["upload_date"] = "20261030",
        ["duration_string"] = "3:33",
        ["height"] = "1080",
        ["resolution"] = "1920x1080",
        ["extractor"] = "youtube",
    };

    // %(field[,fallback...][>strftime])[flags][width][.prec]type
    private static readonly Regex FieldRegex =
        new(@"%\((?<expr>[^)]*)\)(?<flags>[-#0+ ]*)(?<width>\d*)(?:\.\d+)?(?<type>[a-zA-Z])", RegexOptions.Compiled);

    /// <summary>Renders a template with sample values so the Settings page can show a preview.</summary>
    public static string Preview(string template, string ext = "mp4")
    {
        var safe = FileNames.SanitizeTemplatePath(template);
        var rendered = FieldRegex.Replace(safe, m =>
        {
            var expr = m.Groups["expr"].Value;
            string? fmt = null;
            int gt = expr.IndexOf('>');
            if (gt >= 0) { fmt = expr[(gt + 1)..]; expr = expr[..gt]; }

            string value = "NA";
            foreach (var name in expr.Split(','))
            {
                if (SampleValues.TryGetValue(name.Trim(), out var v)) { value = v; break; }
            }

            if (fmt != null && DateTime.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                value = Strftime(date, fmt);

            if (m.Groups["type"].Value == "d" && int.TryParse(value, out var n))
            {
                var width = m.Groups["width"].Value;
                value = width.Length > 0 && m.Groups["flags"].Value.Contains('0')
                    ? n.ToString("D" + width, CultureInfo.InvariantCulture)
                    : n.ToString(CultureInfo.InvariantCulture);
            }
            return value;
        });
        return rendered + "." + ext;
    }

    private static string Strftime(DateTime d, string fmt) => fmt
        .Replace("%Y", d.ToString("yyyy", CultureInfo.InvariantCulture))
        .Replace("%m", d.ToString("MM", CultureInfo.InvariantCulture))
        .Replace("%d", d.ToString("dd", CultureInfo.InvariantCulture))
        .Replace("%y", d.ToString("yy", CultureInfo.InvariantCulture));
}
