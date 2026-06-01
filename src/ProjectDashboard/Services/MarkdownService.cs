using System.IO;
using System.Text.RegularExpressions;

namespace ProjectDashboard.Services;

public static class MarkdownService
{
    public static string ExtractTitle(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return "";

        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("# ") && !trimmed.StartsWith("##"))
                return trimmed[2..].Trim();
        }

        return "";
    }

    public static string ExtractDescription(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return "";

        bool pastTitle = false;
        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.Trim();

            if (!pastTitle)
            {
                if (trimmed.StartsWith("# ") && !trimmed.StartsWith("##"))
                    pastTitle = true;
                continue;
            }

            // Skip blank lines and headings after the title
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#'))
                continue;

            return trimmed;
        }

        return "";
    }

    public static string ExtractLatestVersion(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return "";

        var regex = new Regex(@"\[(\d+\.\d+[\.\d]*[^\]]*)\]");

        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("## ["))
            {
                var match = regex.Match(trimmed);
                if (match.Success)
                    return match.Groups[1].Value;
            }
        }

        return "";
    }

    public static string ReadFileHead(string filePath, int lines = 500)
    {
        if (!File.Exists(filePath)) return "";

        try
        {
            var allLines = File.ReadLines(filePath).Take(lines);
            return string.Join("\n", allLines);
        }
        catch
        {
            return "";
        }
    }
}
