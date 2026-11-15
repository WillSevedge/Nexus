using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace Nexus.Hub.Core.Files;

/// <summary>One value read from a PDF: where it belongs ("Document", "XMP", "Bluebeam"…), its name and its text.</summary>
public sealed record PdfProperty(string Group, string Name, string Value);

/// <summary>One page: which sheet it is, its size, rotation and scale, and every other value stored on the page.</summary>
public sealed class PdfPageDetails
{
    public string File { get; init; } = "";
    /// <summary>1-based page number.</summary>
    public int Page { get; init; }
    public string Label { get; init; } = "";
    public string Bookmark { get; init; } = "";
    public string Sheet { get; init; } = "";
    /// <summary>Size as it is viewed (after rotation), in inches.</summary>
    public double WidthIn { get; init; }
    public double HeightIn { get; init; }
    /// <summary>"ARCH D", "ANSI B", "A1", "Letter"… or "" for a custom size.</summary>
    public string Paper { get; init; } = "";
    public int Rotation { get; init; }
    /// <summary>The drawing scale(s) set on the page ("1 in = 8 ft"), joined by "; " when viewports differ.</summary>
    public string Scale { get; init; } = "";
    public int Markups { get; set; }
    /// <summary>How many pieces of text the page has.</summary>
    public int TextPieces { get; set; }
    /// <summary>"Text", "AutoCAD text", "Scan (no text)", "Scan (text recognised)" or "No text".</summary>
    public string Content { get; set; } = "";
    public List<PdfProperty> Properties { get; init; } = new();

    public string Size => WidthIn <= 0 ? "" : string.Create(CultureInfo.InvariantCulture, $"{WidthIn:0.##} × {HeightIn:0.##} in");
}

/// <summary>Everything read from one PDF.</summary>
public sealed class PdfDetails
{
    public string Path { get; init; } = "";
    public string File => System.IO.Path.GetFileName(Path);
    /// <summary>File facts, the document information, XMP, structure, security and Bluebeam's own values.</summary>
    public List<PdfProperty> Document { get; init; } = new();
    public List<PdfPageDetails> Pages { get; init; } = new();
    /// <summary>Review markups (the drawing's own text is in <see cref="Text"/>).</summary>
    public List<PdfMarkup> Markups { get; init; } = new();
    /// <summary>The text on the pages, top to bottom.</summary>
    public List<PdfTextPiece> Text { get; init; } = new();

    /// <summary>Pages that are only a picture: their text can be recognised (1-based page numbers).</summary>
    public List<int> ScannedPages => Pages.Where(p => p.Content == PdfInspector.Scan).Select(p => p.Page).ToList();
}

/// <summary>
/// Reads everything a PDF says about itself, without changing it: the document information and XMP, how the file is
/// built (version, layers, bookmarks, attachments, forms, security), each page (sheet, size, rotation, scale) and
/// every markup with every value stored on it. Values this program has no name for (Bluebeam's own, or another
/// program's) are listed under the name the PDF gives them, so nothing is left out.
/// </summary>
public static class PdfInspector
{
    public const string Scan = "Scan (no text)";
    public const string ScanRecognised = "Scan (text recognised)";
    public const string ScanUnreadable = "Scan (nothing readable)";

    public static PdfDetails Read(string path)
    {
        List<PdfMarkup> all;
        List<PdfPageDetails> pages;
        List<PdfProperty> document;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var doc = PdfReader.Open(stream, PdfDocumentOpenMode.Import))
        {
            var labels = PdfMarkups.PageLabels(doc);
            var bookmarks = PdfMarkups.BookmarkTitles(doc);
            all = PdfMarkups.Read(doc, path, labels, bookmarks);
            pages = Pages(doc, path, labels, bookmarks);
            document = Document(doc, path, all);
        }
        var markups = all.Where(m => !m.IsDrawingText).ToList();
        foreach (var group in markups.GroupBy(m => m.Page))
            if (group.Key >= 1 && group.Key <= pages.Count) pages[group.Key - 1].Markups = group.Count();

        // The text on the pages: what the PDF stores, plus the text AutoCAD keeps as hidden comments (SHX fonts).
        List<PdfPageText> stored;
        try { stored = PdfText.Read(path); }
        catch { stored = new List<PdfPageText>(); }
        var text = new List<PdfTextPiece>();
        for (int i = 0; i < pages.Count; i++)
        {
            var page = pages[i];
            var onPage = i < stored.Count ? stored[i].Pieces.ToList() : new List<PdfTextPiece>();
            int printed = onPage.Count;
            foreach (var m in all.Where(m => m.IsDrawingText && m.Page == page.Page && m.Comment.Length > 0))
            {
                double Inches(string name) => m.Properties.FirstOrDefault(p => p.Group == "Position" && p.Name == name) is { } p
                    && double.TryParse(p.Value.Replace(" in", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;
                onPage.Add(new PdfTextPiece
                {
                    Page = page.Page, Text = m.Comment.Replace('\n', ' '), Source = PdfText.AutoCadText,
                    FromLeftIn = Inches("From left"), FromBottomIn = Inches("From bottom"), HeightIn = Inches("Height"),
                });
            }
            foreach (var piece in onPage)
            {
                piece.File = page.File;
                piece.Sheet = page.Sheet;
            }
            text.AddRange(onPage.OrderByDescending(p => p.FromBottomIn).ThenBy(p => p.FromLeftIn));
            page.TextPieces = onPage.Count;
            page.Content = printed > 0 ? "Text" : onPage.Count > 0 ? "AutoCAD text"
                : i < stored.Count && stored[i].HasImages ? Scan : "No text";
        }
        document.Insert(document.FindLastIndex(p => p.Group == "Structure") + 1,
            new PdfProperty("Structure", "Text", text.Count == 0
                ? (pages.Any(p => p.Content == Scan) ? "None: the pages are scans" : "None")
                : $"{text.Count} piece(s) of text on {pages.Count(p => p.TextPieces > 0)} page(s)"));
        return new PdfDetails { Path = path, Document = document, Pages = pages, Markups = markups, Text = text };
    }

    // ------------------------------------------------------------------ document

    private static List<PdfProperty> Document(PdfDocument doc, string path, List<PdfMarkup> markups)
    {
        var list = new List<PdfProperty>();
        void Add(string group, string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) list.Add(new PdfProperty(group, name, value.Trim()));
        }

        var info = new FileInfo(path);
        Add("File", "Name", info.Name);
        Add("File", "Folder", info.DirectoryName);
        Add("File", "Size", FileSize(info.Length));
        Add("File", "Modified", info.LastWriteTime.ToString("g", CultureInfo.CurrentCulture));
        Add("File", "Created", info.CreationTime.ToString("g", CultureInfo.CurrentCulture));

        // The document information: the standard entries first, then whatever else the file carries.
        var infoNames = new (string Key, string Name)[]
        {
            ("/Title", "Title"), ("/Author", "Author"), ("/Subject", "Subject"), ("/Keywords", "Keywords"),
            ("/Creator", "Created with"), ("/Producer", "PDF producer"), ("/CreationDate", "Created"), ("/ModDate", "Modified"),
        };
        var infoDict = doc.Info;
        foreach (var (key, name) in infoNames) Add("Document", name, Show(infoDict.Elements[key]));
        foreach (var key in infoDict.Elements.Keys.Where(k => infoNames.All(n => n.Key != k) && k != "/Type"))
            foreach (var (name, value) in Flatten(Label(key), infoDict.Elements[key], 0))
                Add(IsBluebeam(key) ? "Bluebeam" : "Document", name, value);

        var catalog = doc.Internals.Catalog;
        Add("Structure", "PDF version", string.Create(CultureInfo.InvariantCulture, $"{doc.Version / 10}.{doc.Version % 10}"));
        Add("Structure", "Pages", doc.PageCount.ToString(CultureInfo.InvariantCulture));
        Add("Structure", "Markups", markups.Count(m => !m.IsDrawingText).ToString(CultureInfo.InvariantCulture));
        Add("Structure", "Language", PdfMarkups.Text(catalog, "/Lang"));
        Add("Structure", "Page layout", PdfMarkups.Text(catalog, "/PageLayout"));
        Add("Structure", "Opens showing", PdfMarkups.Text(catalog, "/PageMode") switch
        {
            "UseOutlines" => "Bookmarks", "UseThumbs" => "Thumbnails", "UseOC" => "Layers", "UseAttachments" => "Attachments",
            "FullScreen" => "Full screen", _ => "",
        });
        var layers = Layers(catalog);
        Add("Structure", "Layers", layers.Count == 0 ? "" : $"{layers.Count}: {string.Join(", ", layers)}");
        int bookmarks = CountOutline(PdfMarkups.Deref(catalog.Elements["/Outlines"]) as PdfDictionary);
        Add("Structure", "Bookmarks", bookmarks == 0 ? "" : bookmarks.ToString(CultureInfo.InvariantCulture));
        var attachments = Attachments(catalog);
        Add("Structure", "Attachments", attachments.Count == 0 ? "" : $"{attachments.Count}: {string.Join(", ", attachments)}");
        if (PdfMarkups.Deref(catalog.Elements["/AcroForm"]) is PdfDictionary form)
        {
            var fields = new List<PdfDictionary>();
            CollectFields(PdfMarkups.Deref(form.Elements["/Fields"]) as PdfArray, fields, 0);
            Add("Structure", "Form fields", fields.Count == 0 ? "" : fields.Count.ToString(CultureInfo.InvariantCulture));
            var signed = fields.Where(f => f.Elements.GetName("/FT") == "/Sig").ToList();
            int done = signed.Count(f => PdfMarkups.Deref(f.Elements["/V"]) is PdfDictionary);
            if (signed.Count > 0) Add("Structure", "Signatures", $"{done} signed of {signed.Count} signature field(s)");
            foreach (var f in signed)
                if (PdfMarkups.Deref(f.Elements["/V"]) is PdfDictionary v)
                    Add("Structure", "Signed by", string.Join(" · ", new[] { PdfMarkups.Text(v, "/Name"), Show(v.Elements["/M"]), PdfMarkups.Text(v, "/Reason") }.Where(s => s.Length > 0)));
        }
        if (PdfMarkups.Deref(catalog.Elements["/MarkInfo"]) is PdfDictionary mark && Show(mark.Elements["/Marked"]) == "Yes")
            Add("Structure", "Tagged (accessible)", "Yes");

        bool encrypted = false;
        try { encrypted = doc.SecuritySettings.IsEncrypted; } catch { /* treated as not protected */ }
        Add("Security", "Password protected", encrypted ? "Yes" : "No");

        // Bluebeam (and other programs) keep their own values in the catalog under their own names.
        foreach (var key in catalog.Elements.Keys.Where(IsBluebeam))
            foreach (var (name, value) in Flatten(Label(key), catalog.Elements[key], 0))
                Add("Bluebeam", name, value);

        foreach (var (name, value) in Xmp(catalog)) Add("XMP", name, value);
        return list;
    }

    private static bool IsBluebeam(string key) => key.StartsWith("/BSI", StringComparison.OrdinalIgnoreCase) || key.StartsWith("/Bluebeam", StringComparison.OrdinalIgnoreCase);

    private static List<string> Layers(PdfDictionary catalog)
    {
        var names = new List<string>();
        if (PdfMarkups.Deref(catalog.Elements["/OCProperties"]) is PdfDictionary oc && PdfMarkups.Deref(oc.Elements["/OCGs"]) is PdfArray groups)
            foreach (var g in groups.Elements)
                if (PdfMarkups.Deref(g) is PdfDictionary layer && PdfMarkups.Text(layer, "/Name") is { Length: > 0 } name) names.Add(name);
        return names;
    }

    private static int CountOutline(PdfDictionary? outlines)
    {
        int n = 0;
        void Walk(PdfDictionary? item, int depth)
        {
            for (int guard = 0; item is not null && guard < 100000 && depth < 20; guard++)
            {
                n++;
                Walk(PdfMarkups.Deref(item.Elements["/First"]) as PdfDictionary, depth + 1);
                item = PdfMarkups.Deref(item.Elements["/Next"]) as PdfDictionary;
            }
        }
        if (outlines is not null) Walk(PdfMarkups.Deref(outlines.Elements["/First"]) as PdfDictionary, 0);
        return n;
    }

    private static List<string> Attachments(PdfDictionary catalog)
    {
        var names = new List<string>();
        void Walk(PdfDictionary node, int depth)
        {
            if (depth > 20) return;
            if (PdfMarkups.Deref(node.Elements["/Names"]) is PdfArray pairs)
                for (int i = 0; i + 1 < pairs.Elements.Count; i += 2)
                    names.Add(PdfMarkups.Deref(pairs.Elements[i + 1]) is PdfDictionary spec && FileName(spec) is { Length: > 0 } f
                        ? f : Show(pairs.Elements[i]));
            if (PdfMarkups.Deref(node.Elements["/Kids"]) is PdfArray kids)
                foreach (var kid in kids.Elements)
                    if (PdfMarkups.Deref(kid) is PdfDictionary child) Walk(child, depth + 1);
        }
        if (PdfMarkups.Deref(catalog.Elements["/Names"]) is PdfDictionary root && PdfMarkups.Deref(root.Elements["/EmbeddedFiles"]) is PdfDictionary files)
            Walk(files, 0);
        return names;
    }

    internal static string FileName(PdfDictionary fileSpec)
    {
        string name = PdfMarkups.Text(fileSpec, "/UF");
        return name.Length > 0 ? name : PdfMarkups.Text(fileSpec, "/F");
    }

    private static void CollectFields(PdfArray? fields, List<PdfDictionary> into, int depth)
    {
        if (fields is null || depth > 20) return;
        foreach (var item in fields.Elements)
        {
            if (PdfMarkups.Deref(item) is not PdfDictionary field) continue;
            if (PdfMarkups.Deref(field.Elements["/Kids"]) is PdfArray kids && kids.Elements.Count > 0
                && kids.Elements.Select(PdfMarkups.Deref).OfType<PdfDictionary>().Any(k => k.Elements.ContainsKey("/T")))
                CollectFields(kids, into, depth + 1);
            else into.Add(field);
        }
    }

    /// <summary>The XMP packet as name → value ("dc:title", "xmp:CreatorTool", "pdfx:Project Number"…).</summary>
    private static List<(string Name, string Value)> Xmp(PdfDictionary catalog)
    {
        try
        {
            if (PdfMarkups.Deref(catalog.Elements["/Metadata"]) is PdfDictionary { Stream: { } stream })
                return ParseXmp(Encoding.UTF8.GetString(stream.UnfilteredValue));
        }
        catch
        {
            // Metadata that cannot be unpacked: the rest of the file still reads.
        }
        return new List<(string, string)>();
    }

    /// <summary>An XMP packet as name → value; lists are joined with "; ". Empty when it is not XMP.</summary>
    public static List<(string Name, string Value)> ParseXmp(string xml)
    {
        var result = new List<(string, string)>();
        try
        {
            xml = xml.Trim((char)0xFEFF, (char)0, ' ', '\r', '\n');
            int start = xml.IndexOf("<x:xmpmeta", StringComparison.Ordinal);
            if (start < 0) start = xml.IndexOf("<rdf:RDF", StringComparison.Ordinal);
            int end = xml.LastIndexOf('>');
            if (start < 0 || end <= start) return result;
            // The packet ends with a processing instruction (and padding): keep the element only.
            string body = xml[start..(end + 1)];
            int close = Math.Max(body.LastIndexOf("</x:xmpmeta>", StringComparison.Ordinal), body.LastIndexOf("</rdf:RDF>", StringComparison.Ordinal));
            if (close > 0) body = body[..(body.IndexOf('>', close) + 1)];
            var root = XElement.Parse(body);
            XNamespace rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
            string Name(XName n, XElement scope)
            {
                string? prefix = scope.GetPrefixOfNamespace(n.Namespace);
                return string.IsNullOrEmpty(prefix) ? n.LocalName : prefix + ":" + n.LocalName;
            }
            string Value(XElement e)
            {
                var items = e.Descendants(rdf + "li").Where(li => !li.HasElements).Select(li => li.Value.Trim()).Where(v => v.Length > 0).ToList();
                if (items.Count > 0) return string.Join("; ", items);
                return e.HasElements ? "" : e.Value.Trim();
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var description in root.Descendants(rdf + "Description"))
            {
                foreach (var attribute in description.Attributes().Where(a => !a.IsNamespaceDeclaration && a.Name.Namespace != rdf && a.Value.Trim().Length > 0))
                    if (seen.Add(Name(attribute.Name, description))) result.Add((Name(attribute.Name, description), attribute.Value.Trim()));
                foreach (var element in description.Elements())
                {
                    string name = Name(element.Name, element);
                    string value = Value(element);
                    if (value.Length > 0 && seen.Add(name)) result.Add((name, value));
                    // Structured values (xmpMM:History, custom schemas): their own parts by name.
                    if (value.Length == 0)
                        foreach (var part in element.Descendants().Where(p => !p.HasElements && p.Value.Trim().Length > 0 && p.Name.Namespace != rdf))
                            if (seen.Add(name + " › " + part.Name.LocalName)) result.Add((name + " › " + part.Name.LocalName, part.Value.Trim()));
                }
            }
        }
        catch
        {
            // A damaged XMP packet: the rest of the file still reads.
        }
        return result;
    }

    // ------------------------------------------------------------------ pages

    private static readonly (string Name, double W, double H)[] Papers =
    {
        ("Letter", 8.5, 11), ("Legal", 8.5, 14), ("Tabloid / ANSI B", 11, 17), ("ANSI C", 17, 22), ("ANSI D", 22, 34), ("ANSI E", 34, 44),
        ("ARCH A", 9, 12), ("ARCH B", 12, 18), ("ARCH C", 18, 24), ("ARCH D", 24, 36), ("ARCH E1", 30, 42), ("ARCH E", 36, 48),
        ("A4", 8.27, 11.69), ("A3", 11.69, 16.54), ("A2", 16.54, 23.39), ("A1", 23.39, 33.11), ("A0", 33.11, 46.81),
    };

    private static readonly HashSet<string> PageKeysNotShown = new(StringComparer.Ordinal)
    {
        "/Type", "/Parent", "/Contents", "/Resources", "/Annots", "/Thumb", "/Group", "/B", "/StructParents", "/MediaBox", "/CropBox",
        "/Rotate", "/VP", "/Metadata", "/PieceInfo", "/Tabs",
    };

    private static List<PdfPageDetails> Pages(PdfDocument doc, string path, List<string> labels, Dictionary<int, string> bookmarks)
    {
        var pages = new List<PdfPageDetails>();
        string file = Path.GetFileName(path);
        string fromFileName = PdfMarkups.SheetFromFileName(path, doc.PageCount, labels, bookmarks);
        for (int i = 0; i < doc.PageCount; i++)
        {
            var page = doc.Pages[i];
            string label = labels.Count > i ? labels[i] : "";
            bookmarks.TryGetValue(i, out var bookmark);
            string sheet = PdfMarkups.SheetOf(label);
            if (sheet.Length == 0 && bookmark is not null) sheet = PdfMarkups.SheetOf(bookmark);
            if (sheet.Length == 0) sheet = fromFileName;

            var box = Box(Inherited(page, "/CropBox")) ?? Box(Inherited(page, "/MediaBox"));
            double unit = Number(page.Elements["/UserUnit"]) is { } u && u > 0 ? u : 1;
            double w = 0, h = 0;
            if (box is not null)
            {
                w = Math.Abs(box[2] - box[0]) * unit / 72;
                h = Math.Abs(box[3] - box[1]) * unit / 72;
            }
            int rotation = (int)(Number(Inherited(page, "/Rotate")) ?? 0);
            rotation = ((rotation % 360) + 360) % 360;
            if (rotation is 90 or 270) (w, h) = (h, w);

            var properties = new List<PdfProperty>();
            foreach (var key in page.Elements.Keys.Where(k => !PageKeysNotShown.Contains(k)))
                foreach (var (name, value) in Flatten(Label(key), page.Elements[key], 0))
                    if (value.Length > 0) properties.Add(new PdfProperty(IsBluebeam(key) ? "Bluebeam" : "Page", name, value));

            pages.Add(new PdfPageDetails
            {
                File = file,
                Page = i + 1,
                Label = label,
                Bookmark = bookmark ?? "",
                Sheet = sheet,
                WidthIn = Math.Round(w, 2),
                HeightIn = Math.Round(h, 2),
                Paper = Paper(w, h),
                Rotation = rotation,
                Scale = Scales(page),
                Properties = properties,
            });
        }
        return pages;
    }

    private static PdfItem? Inherited(PdfDictionary page, string key)
    {
        PdfDictionary? node = page;
        for (int depth = 0; node is not null && depth < 30; depth++)
        {
            if (PdfMarkups.Deref(node.Elements[key]) is { } value) return value;
            node = PdfMarkups.Deref(node.Elements["/Parent"]) as PdfDictionary;
        }
        return null;
    }

    /// <summary>A rectangle's four numbers (x1, y1, x2, y2), however the PDF library holds it.</summary>
    internal static double[]? Box(PdfItem? item) => PdfMarkups.Deref(item) switch
    {
        PdfRectangle r => new[] { r.X1, r.Y1, r.X2, r.Y2 },
        PdfArray { Elements.Count: 4 } a => a.Elements.Select(e => Number(e) ?? 0).ToArray(),
        _ => null,
    };

    private static string Paper(double w, double h)
    {
        if (w <= 0 || h <= 0) return "";
        double a = Math.Min(w, h), b = Math.Max(w, h);
        foreach (var (name, pw, ph) in Papers)
            if (Math.Abs(a - pw) <= 0.12 && Math.Abs(b - ph) <= 0.12) return name;
        return "";
    }

    /// <summary>The scales of the page's viewports (set with Calibrate or Set Scale in Revu): "1 in = 8 ft".</summary>
    private static string Scales(PdfDictionary page)
    {
        var scales = new List<string>();
        if (PdfMarkups.Deref(page.Elements["/VP"]) is PdfArray viewports)
            foreach (var item in viewports.Elements)
                if (PdfMarkups.Deref(item) is PdfDictionary vp && PdfMarkups.Deref(vp.Elements["/Measure"]) is PdfDictionary measure
                    && PdfMarkups.Text(measure, "/R") is { Length: > 0 } ratio)
                {
                    string name = PdfMarkups.Text(vp, "/Name");
                    string text = name.Length > 0 ? $"{name}: {ratio}" : ratio;
                    if (!scales.Contains(text)) scales.Add(text);
                }
        return string.Join("; ", scales);
    }

    // ------------------------------------------------------------------ values

    /// <summary>"/CreationDate" → "CreationDate" (the name as the PDF has it).</summary>
    internal static string Label(string key) => key.TrimStart('/');

    internal static double? Number(PdfItem? item) => PdfMarkups.Deref(item) switch
    {
        PdfInteger i => i.Value,
        PdfLongInteger l => l.Value,
        PdfReal r => r.Value,
        _ => null,
    };

    /// <summary>One value as text: strings, names, numbers, yes/no, dates and short lists. "" for what has no text.</summary>
    internal static string Show(PdfItem? item)
    {
        switch (PdfMarkups.Deref(item))
        {
            case PdfString s:
                string text = Clean(s.Value);
                return ParseDate(text) is { } date ? date.ToString("g", CultureInfo.CurrentCulture) : text;
            case PdfName n: return n.Value.TrimStart('/');
            case PdfBoolean b: return b.Value ? "Yes" : "No";
            case PdfInteger i: return i.Value.ToString(CultureInfo.InvariantCulture);
            case PdfLongInteger l: return l.Value.ToString(CultureInfo.InvariantCulture);
            case PdfReal r: return r.Value.ToString("0.####", CultureInfo.InvariantCulture);
            case PdfArray a:
                var parts = a.Elements.Select(PdfMarkups.Deref).ToList();
                if (parts.Count == 0) return "";
                if (parts.Any(p => p is PdfDictionary or PdfArray)) return $"{parts.Count} item(s)";
                if (parts.Count > 12) return $"{parts.Count} values";
                return string.Join(", ", parts.Select(Show).Where(v => v.Length > 0));
            default: return "";
        }
    }

    private static readonly HashSet<string> NeverFollowed = new(StringComparer.Ordinal)
    {
        "/Parent", "/P", "/Popup", "/IRT", "/AP", "/Type", "/Dest", "/StructParent", "/Resources", "/Contents",
    };

    /// <summary>
    /// A value and, for a dictionary or a list of dictionaries, its parts as "Name › Part" (three levels deep).
    /// Streams (appearances, images) and links to other objects of the page are not followed.
    /// </summary>
    internal static IEnumerable<(string Name, string Value)> Flatten(string name, PdfItem? item, int depth)
    {
        var value = PdfMarkups.Deref(item);
        if (value is PdfDictionary dict)
        {
            if (dict.Stream is not null)
            {
                yield return (name, $"(data, {dict.Stream.Length:N0} bytes)");
                yield break;
            }
            if (depth >= 3) yield break;
            foreach (var key in dict.Elements.Keys.Where(k => !NeverFollowed.Contains(k)))
                foreach (var part in Flatten(name + " › " + Label(key), dict.Elements[key], depth + 1))
                    yield return part;
            yield break;
        }
        if (value is PdfArray array && depth < 3 && array.Elements.Count is > 0 and <= 40
            && array.Elements.Select(PdfMarkups.Deref).All(e => e is PdfDictionary { Stream: null }))
        {
            int n = 1;
            foreach (var element in array.Elements)
            {
                foreach (var part in Flatten($"{name} {n}", element, depth + 1)) yield return part;
                n++;
            }
            yield break;
        }
        string text = Show(value);
        if (text.Length > 0) yield return (name, text);
    }

    internal static string Clean(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\0", "").Trim();

    /// <summary>"D:20261012093000-05'00'" → the local time written in the file (as Bluebeam shows it).</summary>
    internal static DateTime? ParseDate(string raw)
    {
        if (!raw.StartsWith("D:", StringComparison.Ordinal)) return null;
        raw = raw[2..];
        string digits = new string(raw.TakeWhile(char.IsDigit).ToArray());
        if (digits.Length < 4) return null;
        digits = digits.Length >= 14 ? digits[..14] : digits.Length switch
        {
            < 6 => digits[..4] + "0101000000",
            < 8 => digits[..6] + "01000000",
            _ => digits.PadRight(14, '0'),
        };
        return DateTime.TryParseExact(digits, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
    }

    /// <summary>Rich text (/RC, XHTML) as plain text.</summary>
    internal static string PlainText(string rich)
    {
        if (rich.IndexOf('<') < 0) return rich;
        string text = Regex.Replace(rich, @"<\s*(br|/p|/div)\s*/?>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<[^>]+>", "");
        return Clean(System.Net.WebUtility.HtmlDecode(text));
    }

    /// <summary>A PDF colour (grey, RGB or CMYK, 0–1) as "#RRGGBB"; "" for none.</summary>
    internal static string Colour(PdfItem? item)
    {
        if (PdfMarkups.Deref(item) is not PdfArray a) return "";
        var v = a.Elements.Select(e => Math.Clamp(Number(e) ?? 0, 0, 1)).ToList();
        (double r, double g, double b) = v.Count switch
        {
            1 => (v[0], v[0], v[0]),
            3 => (v[0], v[1], v[2]),
            4 => ((1 - v[0]) * (1 - v[3]), (1 - v[1]) * (1 - v[3]), (1 - v[2]) * (1 - v[3])),
            _ => (-1d, 0d, 0d),
        };
        if (r < 0) return "";
        return $"#{(int)Math.Round(r * 255):X2}{(int)Math.Round(g * 255):X2}{(int)Math.Round(b * 255):X2}";
    }

    private static string FileSize(long bytes) => bytes switch
    {
        >= 1 << 30 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1 << 30):0.##} GB"),
        >= 1 << 20 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1 << 20):0.#} MB"),
        >= 1 << 10 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024d:0} KB"),
        _ => $"{bytes} bytes",
    };
}
