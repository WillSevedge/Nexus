using System.Globalization;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace Nexus.Hub.Core.Files;

/// <summary>One markup (PDF annotation) as Bluebeam Revu's Markups list shows it, with every value stored on it.</summary>
public sealed class PdfMarkup
{
    public string File { get; init; } = "";
    /// <summary>1-based page number.</summary>
    public int Page { get; init; }
    /// <summary>The page's label (the sheet number in sets made by Nexus or most publishers), else "".</summary>
    public string PageLabel { get; init; } = "";
    /// <summary>
    /// The sheet the page is: from the page label, else the page's bookmark, else the file name of a one-sheet PDF
    /// ("A101 - Floor Plan" → A101).
    /// </summary>
    public string Sheet { get; init; } = "";
    /// <summary>"Cloud", "Callout", "Rectangle"… (the annotation kind).</summary>
    public string Type { get; init; } = "";
    /// <summary>Bluebeam's Subject column (often the tool, e.g. "Cloud+", or a custom subject).</summary>
    public string Subject { get; init; } = "";
    public string Comment { get; init; } = "";
    public string Author { get; init; } = "";
    /// <summary>When it was last changed.</summary>
    public DateTime? Date { get; init; }
    public DateTime? Created { get; init; }
    /// <summary>The latest review status set on the markup ("Accepted", "Completed"… or a custom status); "" when none.</summary>
    public string Status { get; init; } = "";
    /// <summary>Every status set on it, oldest first ("Completed · Drafter · 10/13/2026 10:05 AM").</summary>
    public List<string> StatusHistory { get; init; } = new();
    public int Replies { get; init; }
    /// <summary>The replies, oldest first ("Author: text").</summary>
    public List<string> ReplyTexts { get; init; } = new();
    /// <summary>The markup's own id in the PDF (stays the same when the file is saved again).</summary>
    public string Id { get; init; } = "";
    public string Layer { get; init; } = "";
    /// <summary>"#RRGGBB", or "".</summary>
    public string Colour { get; init; } = "";
    public bool Locked { get; init; }
    /// <summary>Markups grouped together in Revu share this; "" when not grouped.</summary>
    public string Group { get; init; } = "";
    /// <summary>Every value stored on the markup, named; values without a known name keep the PDF's name.</summary>
    public List<PdfProperty> Properties { get; init; } = new();

    /// <summary>Still to do: no status, or a status that does not close it.</summary>
    public bool IsOpen => !PdfMarkups.ClosedStatuses.Contains(Status);
}

/// <summary>
/// Reads markups from PDFs (Bluebeam Revu keeps its markups in the PDF as standard annotations): author, subject,
/// comment, dates, replies, review status and history, layer, colour, measurements, custom columns and whatever else
/// is stored on them, with each page matched to its sheet by page label or bookmark.
/// Links, form fields and pop-up notes are not markups and are left out. The file is only read.
/// </summary>
public static class PdfMarkups
{
    /// <summary>Statuses that close a markup (Bluebeam's and the PDF standard's).</summary>
    public static readonly HashSet<string> ClosedStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Accepted", "Completed", "Rejected", "Cancelled", "Canceled", "Closed", "Resolved", "Done", "Void",
    };

    private static readonly HashSet<string> NotMarkups = new(StringComparer.Ordinal) { "/Popup", "/Link", "/Widget" };

    public static List<PdfMarkup> Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var doc = PdfReader.Open(stream, PdfDocumentOpenMode.Import);
        return Read(doc, path, PageLabels(doc), BookmarkTitles(doc));
    }

    internal static List<PdfMarkup> Read(PdfDocument doc, string path, List<string> labels, Dictionary<int, string> bookmarks)
    {
        // Every annotation once, so replies (IRT) find their markup on any page.
        var all = new List<(int Page, PdfDictionary Annot)>();
        for (int i = 0; i < doc.PageCount; i++)
        {
            if (Deref(doc.Pages[i].Elements["/Annots"]) is not PdfArray annots) continue;
            foreach (var item in annots.Elements)
                if (Deref(item) is PdfDictionary a && !NotMarkups.Contains(a.Elements.GetName("/Subtype"))) all.Add((i, a));
        }

        // A reply (or a status change) points at what it answers with /IRT. Markups grouped in Revu also point at
        // the group's first markup with /IRT, marked /RT /Group: those are markups of their own, not replies.
        static bool IsGrouped(PdfDictionary a) => a.Elements.ContainsKey("/IRT") && a.Elements.GetName("/RT") == "/Group";
        static bool IsReply(PdfDictionary a) => a.Elements.ContainsKey("/IRT") && !IsGrouped(a);
        PdfDictionary Root(PdfDictionary reply)
        {
            var a = reply;
            for (int guard = 0; guard < 100 && IsReply(a) && Deref(a.Elements["/IRT"]) is PdfDictionary parent; guard++) a = parent;
            return a;
        }
        var replies = new Dictionary<PdfDictionary, List<PdfDictionary>>(ReferenceEqualityComparer.Instance);
        foreach (var (_, a) in all)
            if (IsReply(a))
            {
                var root = Root(a);
                if (!replies.TryGetValue(root, out var list)) replies[root] = list = new List<PdfDictionary>();
                list.Add(a);
            }

        // Groups: the first markup's id (or a number) names the group for every member.
        var groups = new Dictionary<PdfDictionary, string>(ReferenceEqualityComparer.Instance);
        foreach (var (_, a) in all)
            if (IsGrouped(a) && Deref(a.Elements["/IRT"]) is PdfDictionary first && !groups.ContainsKey(first))
                groups[first] = Text(first, "/NM") is { Length: > 0 } id ? id : $"Group {groups.Count + 1}";

        var columns = CustomColumnNames(doc.Internals.Catalog);
        var markups = new List<PdfMarkup>();
        string file = Path.GetFileName(path);
        string fromFileName = SheetFromFileName(path, doc.PageCount, labels, bookmarks);
        foreach (var (page, a) in all)
        {
            if (IsReply(a)) continue; // a reply or a status change, listed with its markup
            var own = replies.TryGetValue(a, out var r) ? r.OrderBy(x => DateOf(x) ?? DateTime.MinValue).ToList() : new List<PdfDictionary>();
            // Review status: the latest state set by a reply (or on the markup itself). "Marked" states are ticks, not review.
            var states = own.Where(x => x.Elements.ContainsKey("/State") && Text(x, "/StateModel") != "Marked").ToList();
            string status = states.Select(x => Text(x, "/State")).LastOrDefault() ?? Text(a, "/State");
            var history = states.Select(x => string.Join(" · ", new[] { Text(x, "/State"), Text(x, "/T"), DateOf(x)?.ToString("g", CultureInfo.CurrentCulture) ?? "" }
                .Where(v => v.Length > 0))).ToList();
            var texts = own.Where(x => !x.Elements.ContainsKey("/State") && CommentOf(x).Length > 0)
                .Select(x => $"{Text(x, "/T")}: {CommentOf(x)}").ToList();

            string label = labels.Count > page ? labels[page] : "";
            string sheet = SheetOf(label);
            if (sheet.Length == 0 && bookmarks.TryGetValue(page, out var bookmark)) sheet = SheetOf(bookmark);
            if (sheet.Length == 0) sheet = fromFileName;
            string group = groups.TryGetValue(a, out var g) ? g
                : IsGrouped(a) && Deref(a.Elements["/IRT"]) is PdfDictionary first && groups.TryGetValue(first, out var fg) ? fg : "";
            status = status == "None" ? "" : status;
            var markup = new PdfMarkup
            {
                File = file,
                Page = page + 1,
                PageLabel = label,
                Sheet = sheet,
                Type = TypeOf(a),
                Subject = Text(a, "/Subj"),
                Comment = CommentOf(a),
                Author = Text(a, "/T"),
                Date = DateOf(a),
                Created = PdfInspector.ParseDate(Raw(a, "/CreationDate")),
                Status = status,
                StatusHistory = history,
                Replies = texts.Count,
                ReplyTexts = texts,
                Id = Text(a, "/NM"),
                Layer = Deref(a.Elements["/OC"]) is PdfDictionary layer ? Text(layer, "/Name") : "",
                Colour = PdfInspector.Colour(a.Elements["/C"]),
                Locked = ((int)(PdfInspector.Number(a.Elements["/F"]) ?? 0) & 128) != 0,
                Group = group,
            };
            markup.Properties.AddRange(Properties(a, markup, columns));
            markups.Add(markup);
        }
        return markups;
    }

    /// <summary>The comment: /Contents, else the rich text as plain text.</summary>
    private static string CommentOf(PdfDictionary a)
    {
        string text = Text(a, "/Contents");
        return text.Length > 0 ? text : PdfInspector.PlainText(Text(a, "/RC"));
    }

    /// <summary>
    /// One sheet per file ("A101 - Floor Plan.pdf"): the file name says which sheet it is. Used when the page has
    /// no label or bookmark naming it, for a single page, or a file without labels and bookmarks named by number.
    /// </summary>
    internal static string SheetFromFileName(string path, int pageCount, List<string> labels, Dictionary<int, string> bookmarks)
    {
        string named = SheetOf(Path.GetFileNameWithoutExtension(path));
        return pageCount == 1 || (labels.All(l => SheetOf(l).Length == 0) && bookmarks.Count == 0 && named.Any(char.IsDigit)) ? named : "";
    }

    // ------------------------------------------------------------------ every value of a markup

    /// <summary>Values already shown under a name of their own, or that are links to other objects or drawing data.</summary>
    private static readonly HashSet<string> Named = new(StringComparer.Ordinal)
    {
        "/Type", "/Subtype", "/IT", "/Subj", "/Contents", "/T", "/M", "/CreationDate", "/NM", "/State", "/StateModel", "/IRT", "/RT",
        "/Popup", "/P", "/AP", "/AS", "/Rect", "/C", "/IC", "/CA", "/F", "/BS", "/Border", "/OC", "/RC", "/LE", "/Vertices", "/InkList",
        "/QuadPoints", "/L", "/Measure", "/Name", "/FS", "/A", "/Rotate", "/StructParent", "/BSIColumnData",
    };

    private static List<PdfProperty> Properties(PdfDictionary a, PdfMarkup m, IReadOnlyList<string> columns)
    {
        var list = new List<PdfProperty>();
        void Add(string group, string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) list.Add(new PdfProperty(group, name, value.Trim()));
        }
        string When(DateTime? d) => d?.ToString("g", CultureInfo.CurrentCulture) ?? "";

        Add("Markup", "Type", m.Type);
        Add("Markup", "Subject", m.Subject);
        Add("Markup", "Comment", m.Comment);
        Add("Markup", "Author", m.Author);
        Add("Markup", "Created", When(m.Created));
        Add("Markup", "Modified", When(m.Date));
        Add("Markup", "Status", m.Status.Length > 0 ? m.Status : "Open");
        for (int i = 0; i < m.StatusHistory.Count; i++) Add("Markup", $"Status change {i + 1}", m.StatusHistory[i]);
        for (int i = 0; i < m.ReplyTexts.Count; i++) Add("Markup", $"Reply {i + 1}", m.ReplyTexts[i]);
        Add("Markup", "Layer", m.Layer);
        Add("Markup", "Grouped with", m.Group);
        Add("Markup", "ID", m.Id);
        int flags = (int)(PdfInspector.Number(a.Elements["/F"]) ?? 0);
        Add("Markup", "Locked", (flags & 128) != 0 ? "Yes" : "");
        Add("Markup", "Hidden", (flags & 2) != 0 ? "Yes" : "");
        Add("Markup", "Prints", a.Elements.ContainsKey("/F") ? ((flags & 4) != 0 ? "Yes" : "No") : "");
        Add("Markup", "Stamp or icon", Text(a, "/Name"));
        if (Deref(a.Elements["/FS"]) is PdfDictionary spec) Add("Markup", "Attached file", PdfInspector.FileName(spec));
        if (Deref(a.Elements["/A"]) is PdfDictionary action)
            Add("Markup", "Link", Text(action, "/URI") is { Length: > 0 } uri ? uri : Text(action, "/S"));
        Add("Markup", "PDF kind", string.Join(" / ", new[] { a.Elements.GetName("/Subtype").TrimStart('/'), a.Elements.GetName("/IT").TrimStart('/') }.Where(v => v.Length > 0)));

        // Custom columns (Revu's Markups list): the values, named from the file's column list when it has one.
        if (Deref(a.Elements["/BSIColumnData"]) is PdfArray custom)
            for (int i = 0; i < custom.Elements.Count; i++)
                Add("Custom columns", i < columns.Count && columns[i].Length > 0 ? columns[i] : $"Column {i + 1}", PdfInspector.Show(custom.Elements[i]));

        Add("Appearance", "Colour", m.Colour);
        Add("Appearance", "Fill colour", PdfInspector.Colour(a.Elements["/IC"]));
        if (PdfInspector.Number(a.Elements["/CA"]) is { } opacity) Add("Appearance", "Opacity", $"{Math.Round(opacity * 100)}%");
        double? width = Deref(a.Elements["/BS"]) is PdfDictionary bs ? PdfInspector.Number(bs.Elements["/W"])
            : Deref(a.Elements["/Border"]) is PdfArray { Elements.Count: >= 3 } border ? PdfInspector.Number(border.Elements[2]) : null;
        if (width is not null) Add("Appearance", "Line width", string.Create(CultureInfo.InvariantCulture, $"{width:0.##} pt"));
        if (Deref(a.Elements["/BS"]) is PdfDictionary style)
            Add("Appearance", "Line style", style.Elements.GetName("/S") switch { "/D" => "Dashed", "/B" => "Bevelled", "/I" => "Inset", "/U" => "Underline", "/S" => "Solid", _ => "" });
        Add("Appearance", "Line ends", PdfInspector.Show(a.Elements["/LE"]));
        if (PdfInspector.Number(a.Elements["/Rotate"]) is { } rotate && rotate != 0) Add("Appearance", "Rotation", string.Create(CultureInfo.InvariantCulture, $"{rotate:0.#}°"));

        if (PdfInspector.Box(a.Elements["/Rect"]) is { } v)
        {
            string In(double points) => string.Create(CultureInfo.InvariantCulture, $"{points / 72:0.##} in");
            Add("Position", "From left", In(Math.Min(v[0], v[2])));
            Add("Position", "From bottom", In(Math.Min(v[1], v[3])));
            Add("Position", "Width", In(Math.Abs(v[2] - v[0])));
            Add("Position", "Height", In(Math.Abs(v[3] - v[1])));
        }
        if (Deref(a.Elements["/Vertices"]) is PdfArray vertices) Add("Position", "Points", (vertices.Elements.Count / 2).ToString(CultureInfo.InvariantCulture));
        if (Deref(a.Elements["/InkList"]) is PdfArray ink) Add("Position", "Pen strokes", ink.Elements.Count.ToString(CultureInfo.InvariantCulture));

        if (Deref(a.Elements["/Measure"]) is PdfDictionary measure)
        {
            Add("Measurement", "Scale", Text(measure, "/R"));
            foreach (var (name, value) in PdfInspector.Flatten("Measure", measure, 0))
                if (name != "Measure › R") Add("Measurement", name, value);
        }

        // Everything else, under the name the PDF gives it (Bluebeam's own values start with BSI).
        foreach (var key in a.Elements.Keys.Where(k => !Named.Contains(k)))
            foreach (var (name, value) in PdfInspector.Flatten(PdfInspector.Label(key), a.Elements[key], 0))
                Add(key.StartsWith("/BSI", StringComparison.OrdinalIgnoreCase) ? "Bluebeam" : "Other", name, value);
        return list;
    }

    /// <summary>
    /// The names of the custom columns, when the file lists them: a list of named entries kept in the catalog under
    /// one of Bluebeam's own names. Empty when the file has none (the values are then "Column 1", "Column 2"…).
    /// </summary>
    private static List<string> CustomColumnNames(PdfDictionary catalog)
    {
        List<string>? Names(PdfItem? item)
        {
            if (Deref(item) is not PdfArray array || array.Elements.Count == 0) return null;
            var names = array.Elements.Select(e => Deref(e) is PdfDictionary d
                ? new[] { "/Name", "/DisplayName", "/N", "/Title" }.Select(k => Text(d, k)).FirstOrDefault(v => v.Length > 0) ?? "" : "").ToList();
            return names.Any(n => n.Length > 0) ? names : null;
        }
        foreach (var key in catalog.Elements.Keys.Where(k => k.StartsWith("/BSI", StringComparison.OrdinalIgnoreCase) && k.Contains("Column", StringComparison.OrdinalIgnoreCase)))
        {
            if (Names(catalog.Elements[key]) is { } direct) return direct;
            if (Deref(catalog.Elements[key]) is PdfDictionary holder)
                foreach (var inner in holder.Elements.Keys)
                    if (Names(holder.Elements[inner]) is { } nested) return nested;
        }
        return new List<string>();
    }

    /// <summary>"A101 - Floor Plan" or "A101 Floor Plan" → "A101"; a label that is only a page number gives "".</summary>
    public static string SheetOf(string labelOrTitle)
    {
        string t = labelOrTitle.Trim();
        if (t.Length == 0 || t.All(char.IsDigit)) return "";
        foreach (var sep in new[] { " - ", " – ", " — ", ": " })
        {
            int i = t.IndexOf(sep, StringComparison.Ordinal);
            if (i > 0) return t[..i].Trim();
        }
        return t;
    }

    // ------------------------------------------------------------------ page labels and bookmarks

    /// <summary>Each page's label from the catalog's /PageLabels number tree ("" when the file has none).</summary>
    public static List<string> PageLabels(PdfDocument doc)
    {
        var result = new List<string>();
        if (Deref(doc.Internals.Catalog.Elements["/PageLabels"]) is not PdfDictionary tree) return result;
        var ranges = new SortedDictionary<int, PdfDictionary>();
        CollectNums(tree, ranges, 0);
        if (ranges.Count == 0) return result;
        for (int i = 0; i < doc.PageCount; i++)
        {
            var start = ranges.Keys.LastOrDefault(k => k <= i, -1);
            if (start < 0)
            {
                result.Add("");
                continue;
            }
            var range = ranges[start];
            string prefix = Text(range, "/P");
            int first = range.Elements.ContainsKey("/St") ? range.Elements.GetInteger("/St") : 1;
            int n = first + (i - start);
            string style = range.Elements.GetName("/S");
            string number = style switch
            {
                "/D" => n.ToString(CultureInfo.InvariantCulture),
                "/R" => Roman(n).ToUpperInvariant(),
                "/r" => Roman(n),
                "/A" => Letters(n),
                "/a" => Letters(n).ToLowerInvariant(),
                _ => "",
            };
            result.Add(prefix + number);
        }
        return result;
    }

    private static void CollectNums(PdfDictionary node, SortedDictionary<int, PdfDictionary> ranges, int depth)
    {
        if (depth > 20) return;
        if (Deref(node.Elements["/Nums"]) is PdfArray nums)
            for (int k = 0; k + 1 < nums.Elements.Count; k += 2)
                if (Deref(nums.Elements[k]) is PdfInteger index && Deref(nums.Elements[k + 1]) is PdfDictionary range)
                    ranges[index.Value] = range;
        if (Deref(node.Elements["/Kids"]) is PdfArray kids)
            foreach (var kid in kids.Elements)
                if (Deref(kid) is PdfDictionary child) CollectNums(child, ranges, depth + 1);
    }

    /// <summary>Page index → the first bookmark that opens it (top-level and nested).</summary>
    public static Dictionary<int, string> BookmarkTitles(PdfDocument doc)
    {
        var pages = new Dictionary<PdfDictionary, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < doc.PageCount; i++) pages[doc.Pages[i]] = i;
        var result = new Dictionary<int, string>();
        void Walk(PdfDictionary? item, int depth)
        {
            for (int guard = 0; item is not null && guard < 100000 && depth < 20; guard++)
            {
                string title = Text(item, "/Title");
                if (title.Length > 0 && DestinationPage(item) is { } page && pages.TryGetValue(page, out int index))
                    result.TryAdd(index, title);
                Walk(Deref(item.Elements["/First"]) as PdfDictionary, depth + 1);
                item = Deref(item.Elements["/Next"]) as PdfDictionary;
            }
        }
        if (Deref(doc.Internals.Catalog.Elements["/Outlines"]) is PdfDictionary outlines)
            Walk(Deref(outlines.Elements["/First"]) as PdfDictionary, 0);
        return result;
    }

    private static PdfDictionary? DestinationPage(PdfDictionary item)
    {
        var dest = Deref(item.Elements["/Dest"]);
        if (dest is null && Deref(item.Elements["/A"]) is PdfDictionary action && action.Elements.GetName("/S") == "/GoTo")
            dest = Deref(action.Elements["/D"]);
        return dest is PdfArray array && array.Elements.Count > 0 ? Deref(array.Elements[0]) as PdfDictionary : null;
    }

    // ------------------------------------------------------------------ values

    internal static PdfItem? Deref(PdfItem? item) => item is PdfReference r ? r.Value : item;

    /// <summary>A text or name value, trimmed; "" when the key is missing or is something else.</summary>
    internal static string Text(PdfDictionary d, string key) => Deref(d.Elements[key]) switch
    {
        PdfString s => PdfInspector.Clean(s.Value),
        PdfName n => n.Value.TrimStart('/'),
        _ => "",
    };

    private static string Raw(PdfDictionary d, string key) => Deref(d.Elements[key]) is PdfString s ? s.Value.Trim() : "";

    private static DateTime? DateOf(PdfDictionary d) =>
        PdfInspector.ParseDate(Raw(d, "/M")) ?? PdfInspector.ParseDate(Raw(d, "/CreationDate"));

    /// <summary>Bluebeam's names for the annotation kinds (Revu sets /IT for clouds, callouts and measurements).</summary>
    private static string TypeOf(PdfDictionary a)
    {
        string subtype = a.Elements.GetName("/Subtype").TrimStart('/');
        string intent = a.Elements.GetName("/IT").TrimStart('/');
        bool Has(string word) => intent.Contains(word, StringComparison.OrdinalIgnoreCase);
        if (Has("Cloud")) return "Cloud";
        if (intent == "FreeTextCallout") return "Callout";
        if (intent == "FreeTextTypeWriter") return "Typewriter";
        if (Has("Count")) return "Count";
        if (Has("Volume")) return "Volume";
        if (Has("Radius")) return "Radius";
        if (Has("Diameter")) return "Diameter";
        if (Has("Angle")) return "Angle";
        if (Has("Dimension"))
            return intent.StartsWith("PolyLine", StringComparison.Ordinal) ? "Perimeter"
                : intent.StartsWith("Polygon", StringComparison.Ordinal) ? "Area" : "Length";
        if (intent == "LineArrow") return "Arrow";
        return subtype switch
        {
            "FreeText" => "Text Box",
            "Square" => "Rectangle",
            "Circle" => "Ellipse",
            "PolyLine" => "Polyline",
            "Ink" => "Pen",
            "Text" => "Note",
            "StrikeOut" => "Strikethrough",
            "FileAttachment" => "Attachment",
            "" => "Markup",
            _ => subtype,
        };
    }

    private static string Roman(int n)
    {
        if (n <= 0 || n > 3999) return n.ToString(CultureInfo.InvariantCulture);
        var map = new[] { (1000, "m"), (900, "cm"), (500, "d"), (400, "cd"), (100, "c"), (90, "xc"), (50, "l"), (40, "xl"), (10, "x"), (9, "ix"), (5, "v"), (4, "iv"), (1, "i") };
        var sb = new System.Text.StringBuilder();
        foreach (var (value, text) in map)
            while (n >= value) { sb.Append(text); n -= value; }
        return sb.ToString();
    }

    /// <summary>PDF letter numbering: A…Z, then AA…ZZ, AAA…</summary>
    private static string Letters(int n)
    {
        if (n <= 0) return "";
        char c = (char)('A' + (n - 1) % 26);
        return new string(c, (n - 1) / 26 + 1);
    }
}
