using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Nexus.Hub.Core.Files;

public static class PdfTools
{
    /// <summary>One PDF with every page of <paramref name="pdfs"/>, in order (pages are copied, not re-rendered).</summary>
    public static void Combine(IEnumerable<string> pdfs, string target)
    {
        using var output = new PdfDocument();
        foreach (var path in pdfs)
        {
            using var input = PdfReader.Open(path, PdfDocumentOpenMode.Import);
            foreach (var page in input.Pages) output.AddPage(page);
        }
        if (output.PageCount == 0) throw new InvalidOperationException("There are no pages to combine.");
        string temp = target + ".tmp";
        output.Save(temp);
        File.Move(temp, target, overwrite: true);
    }
}
