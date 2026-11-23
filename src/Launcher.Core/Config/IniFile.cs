using System.Text;
using System.Text.RegularExpressions;

namespace Launcher.Core.Config;

/// <summary>
/// Changes single keys in an emulator's ini file and leaves every other line, comment and
/// blank line exactly as it was.
/// </summary>
public static partial class IniFile
{
    [GeneratedRegex(@"^\s*\[\s*(?<name>[^\]]+?)\s*\]")]
    private static partial Regex SectionLine();

    [GeneratedRegex(@"^\s*(?<key>[^=;#\[\s][^=]*?)\s*=")]
    private static partial Regex KeyLine();

    /// <summary>Sets <paramref name="key"/> in <paramref name="section"/>, adding the key or section if missing. Returns true if the file changed.</summary>
    public static bool Set(string path, string section, string key, string value)
    {
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : [];
        string? current = null;
        int sectionStart = -1, sectionEnd = lines.Count;

        for (int i = 0; i < lines.Count; i++)
        {
            var s = SectionLine().Match(lines[i]);
            if (s.Success)
            {
                if (current != null && current.Equals(section, StringComparison.OrdinalIgnoreCase))
                {
                    sectionEnd = i;
                    break;
                }
                current = s.Groups["name"].Value;
                if (current.Equals(section, StringComparison.OrdinalIgnoreCase)) sectionStart = i;
                continue;
            }
            if (sectionStart < 0 || current == null || !current.Equals(section, StringComparison.OrdinalIgnoreCase)) continue;

            var k = KeyLine().Match(lines[i]);
            if (k.Success && k.Groups["key"].Value.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                var eq = lines[i].IndexOf('=');
                var existing = lines[i][(eq + 1)..].Split(';')[0].Trim();
                if (existing == value) return false;
                // Keep the original alignment before '='.
                lines[i] = lines[i][..(eq + 1)] + " " + value;
                File.WriteAllLines(path, lines, new UTF8Encoding(false));
                return true;
            }
        }

        if (sectionStart < 0)
        {
            if (lines.Count > 0 && lines[^1].Trim().Length > 0) lines.Add("");
            lines.Add($"[{section}]");
            lines.Add($"{key} = {value}");
        }
        else
        {
            // Insert after the section's last non-blank line.
            int insert = sectionEnd;
            while (insert - 1 > sectionStart && lines[insert - 1].Trim().Length == 0) insert--;
            lines.Insert(insert, $"{key} = {value}");
        }
        File.WriteAllLines(path, lines, new UTF8Encoding(false));
        return true;
    }
}
