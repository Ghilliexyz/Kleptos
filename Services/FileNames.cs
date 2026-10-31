using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Kleptos.Services;

/// <summary>URL validation and filename sanitising shared by single and multi downloads.</summary>
public static class FileNames
{
    public static readonly Regex UrlRegex =
        new(@"https?://\S+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Windows-superset of invalid filename chars, applied on every OS so filenames stay
    // portable even when writing to e.g. an NTFS drive from Linux.
    private static readonly char[] InvalidFileNameChars = BuildInvalidFileNameChars();

    private static char[] BuildInvalidFileNameChars()
    {
        var chars = new List<char> { '<', '>', ':', '"', '/', '\\', '|', '?', '*' };
        for (int i = 0; i < 32; i++) chars.Add((char)i);
        return chars.ToArray();
    }

    public static string Sanitize(string name)
    {
        foreach (var c in InvalidFileNameChars)
            name = name.Replace(c, '_');

        // Strip any directory traversal attempts
        name = name.Replace("..", "_");

        name = Regex.Replace(name, @"\s+", " ").Trim();
        name = name.TrimEnd('.', ' ');

        if (string.IsNullOrWhiteSpace(name))
            name = "download";

        return name;
    }

    private static readonly Regex TemplateFieldRegex =
        new(@"%\([^)]*\)[-#0+ ]*\d*(?:\.\d+)?[a-zA-Z]", RegexOptions.Compiled);

    /// <summary>
    /// Sanitises a user-entered name while keeping yt-dlp template fields such as
    /// %(title)s or %(upload_date>%Y-%m-%d)s intact.
    /// </summary>
    public static string SanitizeTemplateSafe(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "%(title)s";

        var fields = new List<string>();
        name = TemplateFieldRegex.Replace(name, m =>
        {
            fields.Add(m.Value);
            return $"___KLEPTOS_FIELD_{fields.Count - 1}___";
        });

        name = Sanitize(name);

        for (int i = 0; i < fields.Count; i++)
            name = name.Replace($"___KLEPTOS_FIELD_{i}___", fields[i]);

        return string.IsNullOrWhiteSpace(name) ? "%(title)s" : name;
    }

    /// <summary>
    /// Like <see cref="SanitizeTemplateSafe"/> but allows '/' as a folder separator so a
    /// template can sort files into sub-folders (e.g. "%(uploader)s/%(title)s").
    /// Each segment is sanitised; empty segments and traversal are removed.
    /// </summary>
    public static string SanitizeTemplatePath(string template)
    {
        if (string.IsNullOrWhiteSpace(template)) return "%(title)s";

        // Split on separators that are NOT inside a %(...) field (strftime formats contain '/').
        var segments = new List<string>();
        int depth = 0;
        var current = new System.Text.StringBuilder();
        for (int i = 0; i < template.Length; i++)
        {
            char c = template[i];
            if (c == '%' && i + 1 < template.Length && template[i + 1] == '(') depth++;
            else if (c == ')' && depth > 0) depth--;

            if ((c == '/' || c == '\\') && depth == 0)
            {
                segments.Add(current.ToString());
                current.Clear();
            }
            else current.Append(c);
        }
        segments.Add(current.ToString());

        var cleaned = new List<string>();
        foreach (var seg in segments)
        {
            if (string.IsNullOrWhiteSpace(seg) || seg.Trim() == "." || seg.Trim() == "..") continue;
            cleaned.Add(SanitizeTemplateSafe(seg));
        }

        return cleaned.Count == 0 ? "%(title)s" : string.Join("/", cleaned);
    }

    public static bool TryGetValidUrl(string? input, out string url, out string error)
    {
        url = string.Empty;

        if (string.IsNullOrWhiteSpace(input) || input.Trim().Length < 8)
        {
            error = "No link found.";
            return false;
        }

        input = input.Trim();

        if (!(input.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
              input.StartsWith("http://", StringComparison.OrdinalIgnoreCase)))
        {
            error = "Invalid link (must start with http:// or https://).";
            return false;
        }

        error = string.Empty;
        url = input;
        return true;
    }

    /// <summary>
    /// Parses one line of the multi-download box. Accepted shapes:
    ///   URL
    ///   name URL
    ///   3 name URL      (numbered: "3 - name")
    ///   URL name        (a name after the URL works too)
    /// </summary>
    public static bool TryParseMultiLine(string line, out string url, out string baseName)
    {
        url = string.Empty;
        baseName = string.Empty;

        var m = UrlRegex.Match(line);
        if (!m.Success) return false;

        url = m.Value.Trim();

        var left = line.Substring(0, m.Index).Trim();
        var right = line.Substring(m.Index + m.Length).Trim();
        if (string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right))
            left = right;

        int? indexNum = null;
        string titlePart = left;

        if (!string.IsNullOrWhiteSpace(left))
        {
            var parts = left.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 1 && int.TryParse(parts[0], out var n))
            {
                indexNum = n;
                titlePart = parts.Length == 2 ? parts[1].Trim() : string.Empty;
            }
        }

        titlePart = string.IsNullOrWhiteSpace(titlePart) ? "%(title)s" : Sanitize(titlePart);

        baseName = indexNum.HasValue ? $"{indexNum.Value} - {titlePart}" : titlePart;
        return true;
    }

    public static bool LooksLikePlaylist(string? url) =>
        !string.IsNullOrEmpty(url) &&
        (url.Contains("list=", StringComparison.OrdinalIgnoreCase) ||
         url.Contains("/playlist", StringComparison.OrdinalIgnoreCase) ||
         url.Contains("/@", StringComparison.Ordinal) && url.Contains("/videos", StringComparison.OrdinalIgnoreCase));

    private static readonly Regex PlaylistItemsRegex =
        new(@"^\s*(-?\d+(\s*[-:]\s*-?\d*)?(\s*:\s*-?\d+)?)(\s*,\s*-?\d+(\s*[-:]\s*-?\d*)?(\s*:\s*-?\d+)?)*\s*$", RegexOptions.Compiled);

    /// <summary>Validates a yt-dlp --playlist-items spec such as "1-10,15,20:30".</summary>
    public static bool IsValidPlaylistItems(string? spec) =>
        string.IsNullOrWhiteSpace(spec) || PlaylistItemsRegex.IsMatch(spec);

    private static readonly Regex RateLimitRegex =
        new(@"^\s*\d+(\.\d+)?\s*[KkMmGg]?\s*$", RegexOptions.Compiled);

    /// <summary>Validates a rate like 500K, 2M, 1.5M. Empty means unlimited.</summary>
    public static bool IsValidRateLimit(string? rate) =>
        string.IsNullOrWhiteSpace(rate) || RateLimitRegex.IsMatch(rate);
}
