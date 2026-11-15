using Nexus.Hub.Core.Files;
using PdfSharp.Pdf;

namespace Nexus.Tests;

public sealed class PdfDetailsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexus-pdf-" + Guid.NewGuid().ToString("N"));

    public PdfDetailsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* ignored */ }
    }

    /// <summary>A one-sheet PDF built the way Revu writes one: indirect lists, a scale, layers, XMP and its own values.</summary>
    private string ReviewedSheet()
    {
        string path = Path.Combine(_dir, "A301 - Sections.pdf");
        using var doc = new PdfDocument();
        doc.Info.Title = "Sections";
        doc.Info.Author = "Design Team";
        doc.Info.Elements["/ProjectNumber"] = new PdfString("2026-014");
        var catalog = doc.Internals.Catalog;

        var page = doc.AddPage();
        page.Elements["/MediaBox"] = new PdfArray(doc, new PdfInteger(0), new PdfInteger(0), new PdfInteger(2592), new PdfInteger(1728)); // 36 x 24 in
        page.Elements["/BSIBatesLabel"] = new PdfString("B-0007");
        var measureOfPage = new PdfDictionary(doc);
        measureOfPage.Elements["/R"] = new PdfString("1 in = 8 ft");
        var viewport = new PdfDictionary(doc);
        viewport.Elements["/Measure"] = measureOfPage;
        page.Elements["/VP"] = new PdfArray(doc, viewport);

        var layer = new PdfDictionary(doc);
        layer.Elements["/Type"] = new PdfName("/OCG");
        layer.Elements["/Name"] = new PdfString("Structural review");
        doc.Internals.AddObject(layer);
        var oc = new PdfDictionary(doc);
        oc.Elements["/OCGs"] = new PdfArray(doc, layer.Reference!);
        catalog.Elements["/OCProperties"] = oc;

        PdfDictionary Column(string name)
        {
            var c = new PdfDictionary(doc);
            c.Elements["/Name"] = new PdfString(name);
            return c;
        }
        catalog.Elements["/BSIColumns"] = new PdfArray(doc, Column("Discipline"), Column("Cost"));

        var annots = new PdfArray(doc);
        doc.Internals.AddObject(annots);
        page.Elements["/Annots"] = annots.Reference!; // an indirect list, as Revu writes it
        PdfDictionary Annot(string subtype, Action<PdfDictionary> fill)
        {
            var a = new PdfDictionary(doc);
            a.Elements["/Type"] = new PdfName("/Annot");
            a.Elements["/Subtype"] = new PdfName(subtype);
            fill(a);
            doc.Internals.AddObject(a);
            annots.Elements.Add(a.Reference!);
            return a;
        }
        var cloud = Annot("/Polygon", a =>
        {
            a.Elements["/IT"] = new PdfName("/PolygonCloud");
            a.Elements["/NM"] = new PdfString("ABCDEFGH");
            a.Elements["/Subj"] = new PdfString("Cloud+");
            a.Elements["/Contents"] = new PdfString("Beam depth?");
            a.Elements["/T"] = new PdfString("S. Engineer");
            a.Elements["/CreationDate"] = new PdfString("D:20261101080000-06'00'");
            a.Elements["/M"] = new PdfString("D:20261102090000-06'00'");
            a.Elements["/C"] = new PdfArray(doc, new PdfInteger(1), new PdfInteger(0), new PdfInteger(0));
            a.Elements["/F"] = new PdfInteger(4 | 128);
            a.Elements["/OC"] = layer.Reference!;
            a.Elements["/Rect"] = new PdfArray(doc, new PdfInteger(72), new PdfInteger(144), new PdfInteger(216), new PdfInteger(216));
            a.Elements["/BSIColumnData"] = new PdfArray(doc, new PdfString("Structural"), new PdfString("1200"));
            a.Elements["/BSISpace"] = new PdfString("Level 2 / Room 204");
        });
        Annot("/FreeText", a =>
        {
            a.Elements["/RC"] = new PdfString("<body><p>See <b>S-501</b></p></body>");
            a.Elements["/IRT"] = cloud.Reference!;
            a.Elements["/RT"] = new PdfName("/Group"); // grouped with the cloud: a markup of its own
        });
        var reply = Annot("/Text", a =>
        {
            a.Elements["/IRT"] = cloud.Reference!;
            a.Elements["/Contents"] = new PdfString("W18 per S-501");
            a.Elements["/T"] = new PdfString("Drafter");
            a.Elements["/M"] = new PdfString("D:20261103100000");
        });
        Annot("/Text", a =>
        {
            a.Elements["/IRT"] = reply.Reference!; // a reply to the reply
            a.Elements["/Contents"] = new PdfString("Agreed");
            a.Elements["/T"] = new PdfString("S. Engineer");
            a.Elements["/M"] = new PdfString("D:20261103110000");
        });
        foreach (var (state, at) in new[] { ("Accepted", "D:20261103120000"), ("Completed", "D:20261104120000") })
            Annot("/Text", a =>
            {
                a.Elements["/IRT"] = cloud.Reference!;
                a.Elements["/State"] = new PdfString(state);
                a.Elements["/StateModel"] = new PdfString("Review");
                a.Elements["/T"] = new PdfString("Drafter");
                a.Elements["/M"] = new PdfString(at);
            });
        Annot("/Line", a =>
        {
            a.Elements["/IT"] = new PdfName("/LineDimension");
            a.Elements["/Contents"] = new PdfString("12'-6\"");
            var measure = new PdfDictionary(doc);
            measure.Elements["/R"] = new PdfString("1 in = 8 ft");
            a.Elements["/Measure"] = measure;
        });
        Annot("/Square", a =>
        {
            a.Elements["/T"] = new PdfString("AutoCAD SHX Text"); // the drawing's own text, as AutoCAD writes it
            a.Elements["/Contents"] = new PdfString("%%USECTION A-A");
        });
        doc.Save(path);
        return path;
    }

    [Fact]
    public void Grouped_markups_nested_replies_and_status_history_are_kept()
    {
        var markups = PdfMarkups.Read(ReviewedSheet());
        var drawingText = Assert.Single(markups, m => m.IsDrawingText);
        Assert.Equal(("Drawing text", "SECTION A-A"), (drawingText.Type, drawingText.Comment));
        markups = markups.Where(m => !m.IsDrawingText).ToList();
        Assert.Equal(3, markups.Count); // the cloud, the text grouped with it, the length
        var cloud = markups.Single(m => m.Type == "Cloud");
        Assert.Equal(("ABCDEFGH", "Structural review", "#FF0000", true), (cloud.Id, cloud.Layer, cloud.Colour, cloud.Locked));
        Assert.Equal(new[] { "Drafter: W18 per S-501", "S. Engineer: Agreed" }, cloud.ReplyTexts);
        Assert.Equal("Completed", cloud.Status);
        Assert.Equal(2, cloud.StatusHistory.Count);
        Assert.StartsWith("Accepted · Drafter", cloud.StatusHistory[0]);
        Assert.Equal(new DateTime(2026, 11, 1, 8, 0, 0), cloud.Created);

        var text = markups.Single(m => m.Type == "Text Box");
        Assert.Equal("See S-501", text.Comment); // from the rich text
        Assert.Equal("ABCDEFGH", text.Group);
        Assert.Equal(cloud.Group, text.Group);
        Assert.Equal("Length", markups.Single(m => m.Comment.StartsWith("12'")).Type);
    }

    [Fact]
    public void Every_value_of_a_markup_is_listed_named_or_as_the_pdf_names_it()
    {
        var cloud = PdfMarkups.Read(ReviewedSheet()).Single(m => m.Type == "Cloud");
        string Value(string group, string name) => cloud.Properties.Single(p => p.Group == group && p.Name == name).Value;
        Assert.Equal("Structural", Value("Custom columns", "Discipline"));
        Assert.Equal("1200", Value("Custom columns", "Cost"));
        Assert.Equal("Level 2 / Room 204", Value("Bluebeam", "BSISpace"));
        Assert.Equal("1 in", Value("Position", "From left"));
        Assert.Equal("2 in", Value("Position", "Width"));
        Assert.Equal("Yes", Value("Markup", "Locked"));
        Assert.Equal("Polygon / PolygonCloud", Value("Markup", "PDF kind"));
        var length = PdfMarkups.Read(ReviewedSheet()).Single(m => m.Type == "Length");
        Assert.Contains(length.Properties, p => p is { Group: "Measurement", Name: "Scale", Value: "1 in = 8 ft" });
    }

    [Fact]
    public void Document_and_page_details_are_read()
    {
        var details = PdfInspector.Read(ReviewedSheet());
        string Value(string group, string name) => details.Document.Single(p => p.Group == group && p.Name == name).Value;
        Assert.Equal("Sections", Value("Document", "Title"));
        Assert.Equal("Design Team", Value("Document", "Author"));
        Assert.Equal("2026-014", Value("Document", "ProjectNumber")); // not a standard entry: kept under its own name
        Assert.Equal("1: Structural review", Value("Structure", "Layers"));
        Assert.Equal("1", Value("Structure", "Pages"));
        Assert.Equal("3", Value("Structure", "Markups"));
        Assert.Equal("SECTION A-A", Assert.Single(details.Text).Text);
        Assert.Equal("AutoCAD text", Assert.Single(details.Pages).Content);
        Assert.Contains(details.Document, p => p.Group == "XMP" && p.Name == "xmp:CreatorTool");
        Assert.Equal("Discipline", Value("Bluebeam", "BSIColumns 1 › Name"));

        var page = Assert.Single(details.Pages);
        Assert.Equal(("A301", 36d, 24d, "ARCH D", "1 in = 8 ft", 3), (page.Sheet, page.WidthIn, page.HeightIn, page.Paper, page.Scale, page.Markups));
        Assert.Contains(page.Properties, p => p is { Group: "Bluebeam", Name: "BSIBatesLabel", Value: "B-0007" });
    }

    /// <summary>A one-page PDF written by hand: text in a standard font, and optionally one hidden AutoCAD text comment.</summary>
    private string TextPdf(string name, string content, bool shx = false, bool picture = false)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 792 612] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >>"
                + (picture ? " /XObject << /Im1 6 0 R >>" : "") + " >>" + (shx ? " /Annots [6 0 R]" : "") + " >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        if (picture) objects.Add("<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8 /Length 1 >>\nstream\nA\nendstream");
        if (shx) objects.Add("<< /Type /Annot /Subtype /Square /Rect [72 72 144 90] /T (AutoCAD SHX Text) /Contents (GRID LINE A) /F 2 >>");
        var pdf = new System.Text.StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        int xref = pdf.Length;
        pdf.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append($"{offset:D10} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, pdf.ToString(), System.Text.Encoding.ASCII);
        return path;
    }

    [Fact]
    public void Text_on_the_page_is_read_in_pieces_with_its_position()
    {
        // A title near the top, a sheet number bottom right, and a line read upwards along the left edge.
        string path = TextPdf("S-201.pdf",
            "BT /F1 18 Tf 72 540 Td (FOUNDATION PLAN) Tj ET " +
            "BT /F1 12 Tf 648 36 Td (S-201) Tj ET " +
            "BT /F1 12 Tf 700 36 Td (REV 2) Tj ET " +
            "BT /F1 10 Tf 0 1 -1 0 30 100 Tm (PLOT DATE) Tj ET", shx: true);
        var details = PdfInspector.Read(path);
        var texts = details.Text.Select(t => t.Text).ToList();
        Assert.Contains("FOUNDATION PLAN", texts);
        Assert.Contains("S-201", texts); // far enough from REV 2 to stay a piece of its own
        Assert.Contains("REV 2", texts);
        Assert.Contains("PLOT DATE", texts); // turned text reads the right way round
        var title = details.Text.Single(t => t.Text == "FOUNDATION PLAN");
        Assert.Equal((1d, 7.5, PdfText.PageText, "S-201"), (title.FromLeftIn, Math.Round(title.FromBottomIn, 1), title.Source, title.Sheet));

        // AutoCAD's hidden text comments are text of the drawing, not markups.
        Assert.Empty(details.Markups);
        Assert.Equal(PdfText.AutoCadText, details.Text.Single(t => t.Text == "GRID LINE A").Source);
        var page = Assert.Single(details.Pages);
        Assert.Equal(("Text", 5), (page.Content, page.TextPieces));
        Assert.Empty(details.ScannedPages);
    }

    [Fact]
    public void A_page_without_text_is_told_apart_from_a_page_of_text()
    {
        var details = PdfInspector.Read(TextPdf("Blank.pdf", "0 0 m 100 100 l S"));
        Assert.Empty(details.Text);
        Assert.Equal("No text", Assert.Single(details.Pages).Content);
        Assert.Contains(details.Document, p => p is { Group: "Structure", Name: "Text", Value: "None" });
    }

    [Fact]
    public void A_page_that_is_only_a_picture_is_a_scan_to_be_recognised()
    {
        var details = PdfInspector.Read(TextPdf("Scan.pdf", "q 792 0 0 612 0 0 cm /Im1 Do Q", picture: true));
        Assert.Equal(PdfInspector.Scan, Assert.Single(details.Pages).Content);
        Assert.Equal(new[] { 1 }, details.ScannedPages);
        Assert.Contains(details.Document, p => p is { Group: "Structure", Name: "Text", Value: "None: the pages are scans" });
    }

    [Fact]
    public void Xmp_values_are_read_by_name_with_lists_joined()
    {
        var xmp = PdfInspector.ParseXmp(
            "<?xpacket begin='' id='W5M0MpCehiHzreSzNTczkc9d'?><x:xmpmeta xmlns:x='adobe:ns:meta/'>" +
            "<rdf:RDF xmlns:rdf='http://www.w3.org/1999/02/22-rdf-syntax-ns#'>" +
            "<rdf:Description rdf:about='' xmlns:xmp='http://ns.adobe.com/xap/1.0/' xmlns:dc='http://purl.org/dc/elements/1.1/' " +
            "xmlns:pdfx='http://ns.adobe.com/pdfx/1.3/' xmp:CreatorTool='Revit 2026'>" +
            "<dc:subject><rdf:Bag><rdf:li>IFC</rdf:li><rdf:li>Level 2</rdf:li></rdf:Bag></dc:subject>" +
            "<pdfx:ProjectNumber>2026-014</pdfx:ProjectNumber>" +
            "</rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end='w'?>   ").ToDictionary(x => x.Name, x => x.Value);
        Assert.Equal("Revit 2026", xmp["xmp:CreatorTool"]);
        Assert.Equal("IFC; Level 2", xmp["dc:subject"]);
        Assert.Equal("2026-014", xmp["pdfx:ProjectNumber"]);
        Assert.Empty(PdfInspector.ParseXmp("not xmp"));
    }

    [Fact]
    public void A_markup_list_exported_from_a_studio_session_is_read_with_every_column()
    {
        string path = Path.Combine(_dir, "Session markups.csv");
        File.WriteAllText(path,
            "Subject,Page Label,Page Index,Comments,Author,Date,Status,Layer,Discipline\r\n" +
            "Cloud+,A101 - Floor Plan,3,\"Move the door, then\r\nretag \"\"D12\"\"\",J. Reviewer,10/12/2026 9:30:00 AM,Accepted,Arch review,Architecture\r\n" +
            "Text Box,A102,4,Check grid,PM,,None,,\r\n");
        var markups = BluebeamCsv.Read(path);
        Assert.Equal(2, markups.Count);
        var first = markups[0];
        Assert.Equal(("A101", 3, "Cloud+", "J. Reviewer", "Accepted", "Arch review"), (first.Sheet, first.Page, first.Subject, first.Author, first.Status, first.Layer));
        Assert.Equal("Move the door, then\r\nretag \"D12\"", first.Comment);
        Assert.False(first.IsOpen);
        Assert.Contains(first.Properties, p => p is { Name: "Discipline", Value: "Architecture" });
        Assert.True(markups[1].IsOpen);
        Assert.Equal("Session markups.csv", markups[1].File);
    }
}
