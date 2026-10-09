using System.Text;
using System.Text.RegularExpressions;

namespace InvestigationNightmares.Util;

/// <summary>Case-insensitive globs over forward-slash paths: * stays within a folder, ** crosses folders, ? is one character.</summary>
public static class Glob
{
    public static Regex ToRegex(string glob)
    {
        var sb = new StringBuilder("^");
        for (int i = 0; i < glob.Length; i++)
        {
            char c = glob[i];
            if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*') { sb.Append(".*"); i++; }
            else if (c == '*') sb.Append("[^/]*");
            else if (c == '?') sb.Append("[^/]");
            else sb.Append(Regex.Escape(c.ToString()));
        }
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public static bool IsMatch(string glob, string path) => ToRegex(glob).IsMatch(path.Replace('\\', '/'));
}
