using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nexus.Contracts;
using Nexus.Hub.Core;
using Nexus.Hub.Core.Files;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Nexus.Hub.Pdf;

/// <summary>
/// Reads the text of scanned pages (pages that are only a picture) with the text recognition built into Windows:
/// nothing leaves the PC and no other program is needed. Each page is drawn with Windows' PDF renderer and
/// recognised line by line. Results are kept on disk for the file as it is, so a page is recognised once.
/// </summary>
internal static class PdfOcr
{
    private const double Dpi = 200;

    /// <summary>Null when Windows has no recognition language installed for the user's languages.</summary>
    private static OcrEngine? Engine()
    {
        try { return OcrEngine.TryCreateFromUserProfileLanguages(); }
        catch { return null; }
    }

    public static bool IsAvailable => Engine() is not null;

    /// <summary>The recognised text of <paramref name="pages"/> (1-based), one piece per line.</summary>
    public static async Task<List<PdfTextPiece>> ReadAsync(string path, IReadOnlyList<int> pages, CancellationToken ct)
    {
        var engine = Engine() ?? throw new InvalidOperationException(
            "Windows has no text recognition language installed. Add one in Settings › Time & language › Language & region (a language's optional features).");
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
        var doc = await PdfDocument.LoadFromFileAsync(file);
        var pieces = new List<PdfTextPiece>();
        foreach (int number in pages)
        {
            ct.ThrowIfCancellationRequested();
            if (number < 1 || number > doc.PageCount) continue;
            using var page = doc.GetPage((uint)(number - 1));
            double widthIn = page.Size.Width / 96.0, heightIn = page.Size.Height / 96.0; // the size is in 1/96 inch
            if (widthIn <= 0 || heightIn <= 0) continue;
            // 200 dpi reads drawing text well; very large sheets are drawn smaller to fit what the engine accepts.
            double pixelsPerInch = Math.Min(Dpi, OcrEngine.MaxImageDimension / Math.Max(widthIn, heightIn));
            var options = new PdfPageRenderOptions
            {
                DestinationWidth = (uint)Math.Max(1, Math.Floor(widthIn * pixelsPerInch)),
                DestinationHeight = (uint)Math.Max(1, Math.Floor(heightIn * pixelsPerInch)),
            };
            using var stream = new InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(stream, options);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            var result = await engine.RecognizeAsync(bitmap);
            int before = pieces.Count;
            foreach (var line in result.Lines)
            {
                if (line.Text.Trim().Length == 0 || line.Words.Count == 0) continue;
                double left = line.Words.Min(w => w.BoundingRect.Left), top = line.Words.Min(w => w.BoundingRect.Top);
                double bottom = line.Words.Max(w => w.BoundingRect.Bottom);
                pieces.Add(new PdfTextPiece
                {
                    Page = number,
                    Text = line.Text.Trim(),
                    Source = PdfText.Recognised,
                    FromLeftIn = Math.Round(left / pixelsPerInch, 2),
                    FromBottomIn = Math.Round(heightIn - bottom / pixelsPerInch, 2),
                    HeightIn = Math.Round((bottom - top) / pixelsPerInch, 3),
                });
            }
            // Nothing readable (a photo, a blank scan): an empty piece marks the page as tried.
            if (pieces.Count == before) pieces.Add(new PdfTextPiece { Page = number, Text = "", Source = PdfText.Recognised });
        }
        return pieces;
    }

    // ------------------------------------------------------------------ kept results

    private static string CacheDir => Path.Combine(NexusPaths.Root, "pdf-text");

    /// <summary>One file per PDF as it is now (path, size and time saved): a changed PDF is recognised again.</summary>
    private static string CacheFile(string path)
    {
        var info = new FileInfo(path);
        string key = $"{Path.GetFullPath(path).ToUpperInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        return Path.Combine(CacheDir, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32] + ".json");
    }

    public static List<PdfTextPiece>? Kept(string path)
    {
        try
        {
            string file = CacheFile(path);
            return File.Exists(file) ? JsonSerializer.Deserialize<List<PdfTextPiece>>(File.ReadAllText(file)) : null;
        }
        catch
        {
            return null;
        }
    }

    public static void Keep(string path, List<PdfTextPiece> pieces)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            File.WriteAllText(CacheFile(path), JsonSerializer.Serialize(pieces));
        }
        catch (Exception ex)
        {
            HubLog.Warn("Could not keep the recognised text of a PDF.", ex);
        }
    }

    /// <summary>Puts recognised text into what was read from the file: its pages are then "Scan (text recognised)".</summary>
    public static void Merge(PdfDetails details, List<PdfTextPiece> pieces)
    {
        foreach (var group in pieces.GroupBy(p => p.Page))
        {
            var page = details.Pages.FirstOrDefault(p => p.Page == group.Key);
            if (page is null || page.Content != PdfInspector.Scan) continue;
            var read = group.Where(p => p.Text.Length > 0).ToList();
            foreach (var piece in read)
            {
                piece.File = page.File;
                piece.Sheet = page.Sheet;
                details.Text.Add(piece);
            }
            page.TextPieces = read.Count;
            page.Content = read.Count > 0 ? PdfInspector.ScanRecognised : PdfInspector.ScanUnreadable;
        }
    }
}
