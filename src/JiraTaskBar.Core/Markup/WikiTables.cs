namespace JiraTaskBar.Core.Markup;

public static class WikiTables
{
    /// <summary>
    /// Converts tab-separated text (e.g. cells copied from Excel) to a Jira table, using the
    /// first row as the header. Returns null when the text doesn't look like a grid.
    /// </summary>
    public static string? FromTabSeparated(string text)
    {
        var lines = text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        if (lines.Length < 2 || lines.Any(l => !l.Contains('\t'))) return null;

        var rows = lines.Select(l => l.Split('\t')).ToList();
        var columns = rows[0].Length;
        if (rows.Any(r => r.Length != columns)) return null;

        static string Cell(string value)
        {
            var v = value.Trim().Replace("|", "\\|");
            return v.Length == 0 ? " " : v;
        }

        var output = new List<string> { "||" + string.Join("||", rows[0].Select(Cell)) + "||" };
        output.AddRange(rows.Skip(1).Select(r => "|" + string.Join("|", r.Select(Cell)) + "|"));
        return string.Join("\n", output) + "\n";
    }
}
