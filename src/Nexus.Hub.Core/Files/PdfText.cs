using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace Nexus.Hub.Core.Files;

/// <summary>One piece of text on a page (a run of words on one line) and where it is.</summary>
public sealed class PdfTextPiece
{
    public string File { get; set; } = "";
    /// <summary>1-based page number.</summary>
    public int Page { get; init; }
    public string Sheet { get; set; } = "";
    public string Text { get; init; } = "";
    /// <summary>"Page text", "AutoCAD text" (SHX fonts, kept by AutoCAD as hidden comments) or "Recognised" (read from a scan).</summary>
    public string Source { get; init; } = PdfText.PageText;
    /// <summary>Inches from the left and bottom edges of the page, and the text height.</summary>
    public double FromLeftIn { get; init; }
    public double FromBottomIn { get; init; }
    public double HeightIn { get; init; }
}

/// <summary>What one page holds: its text, and whether it is only a picture (a scan).</summary>
public sealed class PdfPageText
{
    public List<PdfTextPiece> Pieces { get; } = new();
    public bool HasImages { get; set; }
}

/// <summary>
/// Reads the text printed on each page with its position, as the PDF stores it (no recognition, no guessing): what
/// Revit and most CAD exports write. Words on one line that sit together become one piece, so a title block cell
/// or a note reads as one text. A page with pictures and no text is a scan.
/// </summary>
public static class PdfText
{
    public const string PageText = "Page text";
    public const string AutoCadText = "AutoCAD text";
    public const string Recognised = "Recognised";

    private const int MaxPiecesPerPage = 20000;

    /// <summary>The text of every page (index 0 = page 1). Throws when the file cannot be opened.</summary>
    public static List<PdfPageText> Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var doc = PdfDocument.Open(stream);
        var pages = new List<PdfPageText>();
        for (int n = 1; n <= doc.NumberOfPages; n++)
        {
            var result = new PdfPageText();
            pages.Add(result);
            try
            {
                var page = doc.GetPage(n);
                result.Pieces.AddRange(Pieces(page.GetWords(NearestNeighbourWordExtractor.Instance).ToList(), n));
                if (result.Pieces.Count == 0) result.HasImages = page.GetImages().Any();
            }
            catch
            {
                // A page this reader cannot follow: its text is left out, the other pages still read.
            }
        }
        return pages;
    }

    /// <summary>Words joined into pieces: same line, same direction, no gap wider than a few characters.</summary>
    internal static IEnumerable<PdfTextPiece> Pieces(IReadOnlyList<Word> words, int page)
    {
        var pieces = new List<PdfTextPiece>();
        foreach (var group in words.Where(w => w.Text.Trim().Length > 0).GroupBy(w => w.TextOrientation))
        {
            // Along the line (start → end, in reading direction) and across it (line), whichever way the text is
            // turned. A turned word's box is turned with it, so its edges are taken by smallest and largest.
            static double MinX(Word w) => Math.Min(w.BoundingBox.Left, w.BoundingBox.Right);
            static double MaxX(Word w) => Math.Max(w.BoundingBox.Left, w.BoundingBox.Right);
            static double MinY(Word w) => Math.Min(w.BoundingBox.Bottom, w.BoundingBox.Top);
            static double MaxY(Word w) => Math.Max(w.BoundingBox.Bottom, w.BoundingBox.Top);
            var turned = group.Key;
            bool upright = turned is not (TextOrientation.Rotate90 or TextOrientation.Rotate270);
            double Line(Word w) => upright ? MinY(w) : MinX(w);
            double Start(Word w) => turned switch
            {
                TextOrientation.Rotate180 => -MaxX(w), // upside down: reads right to left
                TextOrientation.Rotate270 => MinY(w),  // reads upwards
                TextOrientation.Rotate90 => -MaxY(w),  // reads downwards
                _ => MinX(w),
            };
            double End(Word w) => turned switch
            {
                TextOrientation.Rotate180 => -MinX(w),
                TextOrientation.Rotate270 => MaxY(w),
                TextOrientation.Rotate90 => -MinY(w),
                _ => MaxX(w),
            };
            double Size(Word w) => Math.Max(1, upright ? MaxY(w) - MinY(w) : MaxX(w) - MinX(w));

            var current = new List<Word>();
            void Flush()
            {
                if (current.Count == 0) return;
                pieces.Add(new PdfTextPiece
                {
                    Page = page,
                    Text = string.Join(" ", current.Select(w => w.Text.Trim())),
                    FromLeftIn = Math.Round(current.Min(MinX) / 72, 2),
                    FromBottomIn = Math.Round(current.Min(MinY) / 72, 2),
                    HeightIn = Math.Round(current.Max(Size) / 72, 3),
                });
                current.Clear();
            }
            // Words into lines (top line first), then along each line in reading direction.
            var lines = new List<List<Word>>();
            foreach (var word in group.OrderByDescending(Line))
            {
                var line = lines.Count > 0 ? lines[^1] : null;
                if (line is null || Math.Abs(Line(line[0]) - Line(word)) > Math.Max(Size(line[0]), Size(word)) * 0.5) lines.Add(line = new List<Word>());
                line.Add(word);
            }
            foreach (var line in lines)
            {
                foreach (var word in line.OrderBy(Start))
                {
                    if (current.Count > 0)
                    {
                        var last = current[^1];
                        double size = Math.Max(Size(last), Size(word));
                        double gap = Start(word) - End(last);
                        if (gap > size * 1.6 || gap < -size) Flush();
                    }
                    current.Add(word);
                }
                Flush();
            }
        }
        return pieces.OrderByDescending(p => p.FromBottomIn).ThenBy(p => p.FromLeftIn).Take(MaxPiecesPerPage);
    }
}
