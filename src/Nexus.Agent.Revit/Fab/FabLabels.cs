using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Nexus.Fab;
using QRCoder;

namespace Nexus.Agent.Revit.Fab;

/// <summary>What one spool label says.</summary>
internal sealed class SpoolLabel
{
    public string Spool { get; init; } = "";
    public string Package { get; init; } = "";
    public string Line1 { get; init; } = "";
    public string Line2 { get; init; } = "";
    public string Line3 { get; init; } = "";
    public string Project { get; init; } = "";
}

/// <summary>
/// Spool labels with a QR code, printed through Windows (a label printer, a sheet of labels, or Microsoft Print to
/// PDF). The code is plain text ("NX|spool|package"), so any scanner reads it: scanned into the Tracking tab it finds
/// the spool and moves it to the next step.
/// </summary>
internal static class FabLabels
{
    private const double Dip = 96; // WPF units per inch

    /// <summary>"Label printer": one 4 × 2 in label a page. "Sheet": 4 × 2 in labels, ten to a Letter sheet (Avery 5163).</summary>
    public static FixedDocument Build(IReadOnlyList<SpoolLabel> labels, bool sheet)
    {
        var document = new FixedDocument();
        double w = 4 * Dip, h = 2 * Dip;
        if (!sheet)
        {
            foreach (var label in labels) AddPage(document, w, h, new[] { (Label(label, w, h), 0d, 0d) });
            return document;
        }
        double pageW = 8.5 * Dip, pageH = 11 * Dip, left = 0.156 * Dip, top = 0.5 * Dip, gap = 0.188 * Dip;
        for (int start = 0; start < labels.Count; start += 10)
        {
            var placed = new List<(UIElement, double, double)>();
            for (int i = start; i < Math.Min(labels.Count, start + 10); i++)
            {
                int cell = i - start;
                placed.Add((Label(labels[i], w, h), left + (cell % 2) * (w + gap), top + (cell / 2) * h));
            }
            AddPage(document, pageW, pageH, placed);
        }
        return document;
    }

    /// <summary>Asks for a printer and prints. False when the user cancelled.</summary>
    public static bool Print(FixedDocument document, string title)
    {
        var dialog = new PrintDialog();
        if (dialog.ShowDialog() != true) return false;
        dialog.PrintDocument(document.DocumentPaginator, title);
        return true;
    }

    private static void AddPage(FixedDocument document, double width, double height, IEnumerable<(UIElement Element, double X, double Y)> content)
    {
        var page = new FixedPage { Width = width, Height = height, Background = Brushes.White };
        foreach (var (element, x, y) in content)
        {
            FixedPage.SetLeft(element, x);
            FixedPage.SetTop(element, y);
            page.Children.Add(element);
        }
        var size = new Size(width, height);
        page.Measure(size);
        page.Arrange(new Rect(size));
        page.UpdateLayout();
        var holder = new PageContent();
        ((System.Windows.Markup.IAddChild)holder).AddChild(page);
        document.Pages.Add(holder);
        document.DocumentPaginator.PageSize = size;
    }

    private static UIElement Label(SpoolLabel label, double width, double height)
    {
        double pad = 0.12 * Dip, code = height - 2 * pad;
        var grid = new Grid { Width = width, Height = height };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(code + pad) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());

        var qr = new System.Windows.Shapes.Path
        {
            Data = Code(FabCode.For(label.Spool, label.Package)), Fill = Brushes.Black, Stretch = Stretch.Uniform,
            Width = code, Height = code, Margin = new Thickness(pad, pad, 0, pad), SnapsToDevicePixels = true,
        };
        RenderOptions.SetEdgeMode(qr, EdgeMode.Aliased);
        grid.Children.Add(qr);

        var text = new StackPanel { Margin = new Thickness(pad, pad, pad, pad), VerticalAlignment = VerticalAlignment.Center };
        TextBlock Line(string value, double size, FontWeight weight) => new()
        {
            Text = value, FontFamily = new FontFamily("Segoe UI"), FontSize = size, FontWeight = weight, Foreground = Brushes.Black,
            TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap,
        };
        // The spool name is what people read across the shop: as large as fits the label.
        double nameSize = label.Spool.Length <= 10 ? 26 : label.Spool.Length <= 16 ? 20 : 15;
        text.Children.Add(Line(label.Spool, nameSize, FontWeights.Bold));
        if (label.Package.Length > 0) text.Children.Add(Line("Package " + label.Package, 12, FontWeights.SemiBold));
        foreach (string line in new[] { label.Line1, label.Line2, label.Line3 })
            if (line.Length > 0) text.Children.Add(Line(line, 10.5, FontWeights.Normal));
        if (label.Project.Length > 0) text.Children.Add(Line(label.Project, 8.5, FontWeights.Normal));
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return grid;
    }

    /// <summary>The code's dark squares as one shape (a quiet border of four squares is part of it).</summary>
    private static Geometry Code(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var geometry = new StreamGeometry { FillRule = FillRule.Nonzero };
        using (var context = geometry.Open())
        {
            int n = data.ModuleMatrix.Count;
            // An unseen frame the size of the whole code, so the quiet border is kept when the shape is scaled.
            context.BeginFigure(new Point(0, 0), false, false);
            context.LineTo(new Point(n, n), false, false);
            for (int y = 0; y < n; y++)
            {
                var row = data.ModuleMatrix[y];
                for (int x = 0; x < n; x++)
                {
                    if (!row[x]) continue;
                    int end = x;
                    while (end + 1 < n && row[end + 1]) end++; // a run of dark squares as one rectangle
                    context.BeginFigure(new Point(x, y), true, true);
                    context.LineTo(new Point(end + 1, y), false, false);
                    context.LineTo(new Point(end + 1, y + 1), false, false);
                    context.LineTo(new Point(x, y + 1), false, false);
                    x = end;
                }
            }
        }
        geometry.Freeze();
        return geometry;
    }
}
