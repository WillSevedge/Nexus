using System.Globalization;
using System.Text;

namespace Nexus.Hub.Core.Files;

/// <summary>
/// Reads a markup list exported from Bluebeam Revu as CSV (Markups list › Summary or Export › CSV). This is how the
/// markups of a Studio Session get here while the session is still running: its PDFs live on Bluebeam's servers,
/// not in a file. Every column of the export is kept, whatever it is called (custom columns included).
/// </summary>
public static class BluebeamCsv
{
    private static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Subject"] = "Subject", ["Comments"] = "Comment", ["Comment"] = "Comment", ["Author"] = "Author", ["Date"] = "Date",
        ["Creation Date"] = "Created", ["Status"] = "Status", ["Layer"] = "Layer", ["Color"] = "Colour", ["Colour"] = "Colour",
        ["Page Label"] = "PageLabel", ["Page Index"] = "Page", ["Page"] = "Page", ["Lock"] = "Locked", ["Locked"] = "Locked",
        ["File Name"] = "File", ["Document"] = "File", ["File"] = "File", ["ID"] = "Id", ["Type"] = "Type",
    };

    public static List<PdfMarkup> Read(string path)
    {
        string text;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            text = reader.ReadToEnd();
        var rows = Parse(text);
        var markups = new List<PdfMarkup>();
        // The header is the first row that names a column of the Markups list (exports may start with a title line).
        int head = rows.FindIndex(r => r.Any(c => Known.ContainsKey(c.Trim())));
        if (head < 0) return markups;
        var headers = rows[head].Select(h => h.Trim()).ToList();
        string list = Path.GetFileName(path);
        foreach (var row in rows.Skip(head + 1))
        {
            if (row.All(c => c.Trim().Length == 0)) continue;
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            var properties = new List<PdfProperty>();
            for (int i = 0; i < headers.Count && i < row.Count; i++)
            {
                string value = row[i].Trim();
                if (headers[i].Length == 0 || value.Length == 0) continue;
                properties.Add(new PdfProperty("Markups list", headers[i], value));
                if (Known.TryGetValue(headers[i], out var field)) values.TryAdd(field, value);
            }
            if (properties.Count == 0) continue;
            string Get(string field) => values.GetValueOrDefault(field, "");
            string label = Get("PageLabel");
            int.TryParse(Get("Page"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int page);
            string status = Get("Status");
            markups.Add(new PdfMarkup
            {
                File = Get("File") is { Length: > 0 } f ? f : list,
                Page = page,
                PageLabel = label,
                Sheet = PdfMarkups.SheetOf(label),
                Type = Get("Type") is { Length: > 0 } t ? t : Get("Subject"),
                Subject = Get("Subject"),
                Comment = Get("Comment"),
                Author = Get("Author"),
                Date = When(Get("Date")),
                Created = When(Get("Created")),
                Status = status.Equals("None", StringComparison.OrdinalIgnoreCase) ? "" : status,
                Id = Get("Id"),
                Layer = Get("Layer"),
                Colour = Get("Colour"),
                Locked = Get("Locked") is "Locked" or "Yes" or "True" or "true",
                Properties = properties,
            });
        }
        return markups;
    }

    private static DateTime? When(string text) =>
        text.Length == 0 ? null
        : DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out var local) ? local
        : DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var invariant) ? invariant : null;

    /// <summary>CSV with quoted values ("" for a quote, line breaks inside quotes); comma, semicolon or tab separated.</summary>
    internal static List<List<string>> Parse(string text)
    {
        string firstLine = text.Split('\n', 2)[0];
        char separator = new[] { ',', ';', '\t' }.OrderByDescending(c => firstLine.Count(x => x == c)).First();
        var rows = new List<List<string>>();
        var row = new List<string>();
        var value = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { value.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else value.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == separator) { row.Add(value.ToString()); value.Clear(); }
            else if (c == '\n' || c == '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(value.ToString());
                value.Clear();
                rows.Add(row);
                row = new List<string>();
            }
            else value.Append(c);
        }
        if (value.Length > 0 || row.Count > 0)
        {
            row.Add(value.ToString());
            rows.Add(row);
        }
        return rows;
    }
}
