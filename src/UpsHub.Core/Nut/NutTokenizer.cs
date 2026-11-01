using System.Text;

namespace UpsHub.Core.Nut;

/// <summary>Splits upsd reply lines into words, honouring "quoted strings" with \" and \\ escapes.</summary>
public static class NutTokenizer
{
    public static List<string> Split(string line)
    {
        var parts = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false, any = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c == '\\' && i + 1 < line.Length) { sb.Append(line[++i]); }
                else if (c == '"') { inQuotes = false; }
                else sb.Append(c);
            }
            else if (c == '"') { inQuotes = true; any = true; }
            else if (c == ' ' || c == '\t')
            {
                if (sb.Length > 0 || any) { parts.Add(sb.ToString()); sb.Clear(); any = false; }
            }
            else { sb.Append(c); any = true; }
        }
        if (sb.Length > 0 || any) parts.Add(sb.ToString());
        return parts;
    }

    /// <summary>Quotes a value for sending (SET VAR etc.).</summary>
    public static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
