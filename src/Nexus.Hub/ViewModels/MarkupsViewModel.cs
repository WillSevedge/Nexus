using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Data;
using Microsoft.Win32;
using Nexus.Hub.Core;
using Nexus.Hub.Core.Excel;
using Nexus.Hub.Core.Files;

namespace Nexus.Hub.ViewModels;

/// <summary>One markup in the list, with the sheet it is on as the model has it.</summary>
public sealed class MarkupRow
{
    public MarkupRow(PdfMarkup m, string path, TableRow? model)
    {
        M = m;
        Path = path;
        Model = model;
    }

    public PdfMarkup M { get; }
    public string Path { get; }
    public TableRow? Model { get; }

    public string Sheet => M.Sheet.Length > 0 ? M.Sheet : $"Page {M.Page}";
    public string SheetName => Model is not null && Model.Values.TryGetValue(SheetFieldMap.ColumnId("Title"), out var t) ? t.Value ?? "" : "";
    /// <summary>"A101 · Floor Plan" for the group headers.</summary>
    public string Group => SheetName.Length > 0 ? $"{Sheet} · {SheetName}" : M.Sheet.Length > 0 ? $"{Sheet} · not in the loaded models" : Sheet;
    public bool InModel => Model is not null;
    public int Page => M.Page;
    public string Type => M.Type;
    public string Subject => M.Subject;
    public string Comment => M.Comment;
    public string Author => M.Author;
    public string Date => M.Date?.ToString("g") ?? "";
    public DateTime? When => M.Date;
    public string Status => M.Status.Length > 0 ? M.Status : "Open";
    public bool IsOpen => M.IsOpen;
    public string Replies => M.Replies == 0 ? "" : M.Replies.ToString();
    public string RepliesText => string.Join(Environment.NewLine, M.ReplyTexts);
    public string File => M.File;
    public string Layer => M.Layer;
    public string Colour => M.Colour;
    /// <summary>"Cloud · A101 · J. Reviewer" for the details pane.</summary>
    public string Title => string.Join(" · ", new[] { Type, Sheet, Author }.Where(v => v.Length > 0));
    /// <summary>Every value of the markup, the sheet it is on first.</summary>
    public IEnumerable<PdfProperty> Properties =>
        new[]
        {
            new PdfProperty("Where", "File", File), new PdfProperty("Where", "Sheet", Sheet), new PdfProperty("Where", "Sheet name", SheetName),
            new PdfProperty("Where", "Page", M.Page > 0 ? M.Page.ToString() : ""), new PdfProperty("Where", "Page label", M.PageLabel),
        }.Where(p => p.Value.Length > 0).Concat(M.Properties);
}

/// <summary>One page of a PDF in the Pages list.</summary>
public sealed class PageRow
{
    public PageRow(PdfPageDetails p) => P = p;

    public PdfPageDetails P { get; }
    public string File => P.File;
    public int Page => P.Page;
    public string Sheet => P.Sheet;
    public string Label => P.Label;
    public string Bookmark => P.Bookmark;
    public string Size => P.Size;
    public string Paper => P.Paper;
    public string Rotation => P.Rotation == 0 ? "" : P.Rotation + "°";
    public string Scale => P.Scale;
    public string Markups => P.Markups == 0 ? "" : P.Markups.ToString();
    public string Title => $"{File} · page {Page}" + (Sheet.Length > 0 ? $" · {Sheet}" : "");
    public IEnumerable<PdfProperty> Properties =>
        new[]
        {
            new PdfProperty("Page", "File", File), new PdfProperty("Page", "Page", Page.ToString()), new PdfProperty("Page", "Sheet", Sheet),
            new PdfProperty("Page", "Label", Label), new PdfProperty("Page", "Bookmark", Bookmark), new PdfProperty("Page", "Size", Size),
            new PdfProperty("Page", "Paper", Paper), new PdfProperty("Page", "Rotation", Rotation), new PdfProperty("Page", "Scale", Scale),
            new PdfProperty("Page", "Markups", Markups),
        }.Where(p => p.Value.Length > 0).Concat(P.Properties);
}

/// <summary>One value of a PDF in the Document list.</summary>
public sealed record DocumentRow(string File, string Group, string Name, string Value);

/// <summary>
/// PDFs next to the sheets: every markup from Bluebeam (or any PDF reviewer) with every value stored on it, each page
/// (sheet, size, scale) and what the file says about itself (document information, XMP, layers, security…). Markup
/// lists exported from a Studio Session (CSV) are read the same way. Files are only read, and read again when saved.
/// </summary>
public sealed class MarkupsViewModel : Observable, IDisposable
{
    public static readonly string[] Filters = { "Open", "Everything", "Closed", "On sheets not in the model", DrawingTextFilter };
    private const string DrawingTextFilter = "Drawing text (AutoCAD)";

    private readonly MainViewModel _main;
    private readonly List<FileSystemWatcher> _watchers = new();
    private CancellationTokenSource? _saveDelay;
    private string _filter = Filters[0];
    private string _search = "";
    private string _summary = "";
    private string _message = "";
    private bool _busy;

    public MarkupsViewModel(MainViewModel main)
    {
        _main = main;
        foreach (var f in main.MarkupPdfs.Where(File.Exists)) Files.Add(f);
        View = CollectionViewSource.GetDefaultView(Rows);
        View.Filter = o => o is MarkupRow r && Shows(r);
        View.GroupDescriptions.Add(new PropertyGroupDescription(nameof(MarkupRow.Group)));
        PagesView = CollectionViewSource.GetDefaultView(Pages);
        PagesView.Filter = o => o is PageRow r && Found(r.File, r.Sheet, r.Label, r.Bookmark, r.Paper, r.Scale);
        DocumentView = CollectionViewSource.GetDefaultView(Document);
        DocumentView.Filter = o => o is DocumentRow r && Found(r.File, r.Group, r.Name, r.Value);
        DocumentView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(DocumentRow.File)));
        DetailsView = CollectionViewSource.GetDefaultView(Details);
        DetailsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(PdfProperty.Group)));
        AddCommand = new RelayCommand(AddAsync);
        AddFolderCommand = new RelayCommand(AddFolderAsync);
        RemoveCommand = new RelayCommand(() => { Remove(); return Task.CompletedTask; }, () => SelectedFile is not null);
        RefreshCommand = new RelayCommand(RefreshAsync, () => !_busy && Files.Count > 0);
        ExportCommand = new RelayCommand(() => { Export(); return Task.CompletedTask; }, () => Rows.Count + Pages.Count + Document.Count > 0);
        OpenPdfCommand = new RelayCommand(() => { OpenPdf(); return Task.CompletedTask; }, () => Files.Count > 0);
        ShowInModelCommand = new RelayCommand(ShowInModelAsync);
    }

    public ObservableCollection<string> Files { get; } = new();
    public ObservableCollection<MarkupRow> Rows { get; } = new();
    public ICollectionView View { get; }
    public ObservableCollection<PageRow> Pages { get; } = new();
    public ICollectionView PagesView { get; }
    public ObservableCollection<DocumentRow> Document { get; } = new();
    public ICollectionView DocumentView { get; }
    /// <summary>Every value of the markup or page selected last.</summary>
    public ObservableCollection<PdfProperty> Details { get; } = new();
    public ICollectionView DetailsView { get; }
    public RelayCommand AddCommand { get; }
    public RelayCommand AddFolderCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand ExportCommand { get; }
    public RelayCommand OpenPdfCommand { get; }
    public RelayCommand ShowInModelCommand { get; }

    /// <summary>The window supplies the selected markups.</summary>
    public Func<IReadOnlyList<MarkupRow>>? SelectedRows { get; set; }

    private string? _selectedFile;
    public string? SelectedFile
    {
        get => _selectedFile;
        set => Set(ref _selectedFile, value);
    }

    public bool HasFiles => Files.Count > 0;

    private int _tab;
    /// <summary>0 Markups, 1 Pages, 2 Document (Export saves the list shown).</summary>
    public int Tab
    {
        get => _tab;
        set
        {
            if (Set(ref _tab, value)) Raise(nameof(IsMarkupsTab));
        }
    }

    public bool IsMarkupsTab => _tab == 0;

    private string _detailsTitle = "Select a markup or a page to see everything stored on it.";
    public string DetailsTitle
    {
        get => _detailsTitle;
        private set => Set(ref _detailsTitle, value);
    }

    public void ShowDetails(string title, IEnumerable<PdfProperty> properties)
    {
        DetailsTitle = title;
        Details.Clear();
        foreach (var p in properties) Details.Add(p);
    }

    public string Filter
    {
        get => _filter;
        set
        {
            if (Set(ref _filter, value)) View.Refresh();
        }
    }

    public string Search
    {
        get => _search;
        set
        {
            if (!Set(ref _search, value ?? "")) return;
            View.Refresh();
            PagesView.Refresh();
            DocumentView.Refresh();
        }
    }

    public string Summary
    {
        get => _summary;
        private set => Set(ref _summary, value);
    }

    public string Message
    {
        get => _message;
        private set => Set(ref _message, value);
    }

    public bool IsBusy
    {
        get => _busy;
        private set => Set(ref _busy, value);
    }

    private bool Shows(MarkupRow r)
    {
        // The drawing's own text (AutoCAD SHX) is not a review comment: listed only when asked for.
        if (r.M.IsDrawingText != (_filter == DrawingTextFilter)) return false;
        bool kind = _filter switch
        {
            DrawingTextFilter => true,
            "Everything" => true,
            "Closed" => !r.IsOpen,
            "On sheets not in the model" => !r.InModel,
            _ => r.IsOpen,
        };
        if (!kind) return false;
        string s = _search.Trim();
        return s.Length == 0
               || Found(r.Sheet, r.SheetName, r.Comment, r.Subject, r.Author, r.Status, r.RepliesText, r.Layer, r.Type)
               || r.M.Properties.Any(p => p.Value.Contains(s, StringComparison.OrdinalIgnoreCase));
    }

    private bool Found(params string[] values)
    {
        string s = _search.Trim();
        return s.Length == 0 || values.Any(v => v.Contains(s, StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------------ files

    private async Task AddAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose PDFs (sets or single sheets) or markup lists exported from Bluebeam (CSV)",
            Filter = "PDFs and Bluebeam markup lists (*.pdf;*.csv)|*.pdf;*.csv|PDF files (*.pdf)|*.pdf|Bluebeam markup list (*.csv)|*.csv",
            Multiselect = true,
        };
        if (dialog.ShowDialog() != true) return;
        foreach (var f in dialog.FileNames)
            if (!Files.Contains(f, StringComparer.OrdinalIgnoreCase)) Files.Add(f);
        Remember();
        await RefreshAsync();
    }

    /// <summary>Every PDF in a folder and its subfolders (an issued set, or the folder a Studio Session was saved to).</summary>
    private async Task AddFolderAsync()
    {
        var dialog = new OpenFolderDialog { Title = "Choose a folder of PDFs (subfolders included)" };
        if (dialog.ShowDialog() != true) return;
        List<string> found;
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 8 };
            found = await Task.Run(() => Directory.EnumerateFiles(dialog.FolderName, "*.pdf", options).Take(MaxFolderFiles + 1).ToList());
        }
        catch (Exception ex)
        {
            Message = "Could not read the folder: " + ex.Message;
            return;
        }
        bool tooMany = found.Count > MaxFolderFiles;
        foreach (var f in found.Take(MaxFolderFiles).OrderBy(f => f, NaturalComparer.Instance))
            if (!Files.Contains(f, StringComparer.OrdinalIgnoreCase)) Files.Add(f);
        Remember();
        await RefreshAsync();
        if (found.Count == 0) Message = "No PDFs in that folder.";
        else if (tooMany) Message = $"That folder has more than {MaxFolderFiles} PDFs: the first {MaxFolderFiles} were added. Add a subfolder for the rest.";
    }

    private const int MaxFolderFiles = 2000;

    private void Remove()
    {
        if (SelectedFile is null) return;
        Files.Remove(SelectedFile);
        Remember();
        _ = RefreshAsync();
    }

    private void Remember()
    {
        _main.MarkupPdfs = Files.ToList();
        Raise(nameof(HasFiles));
        Watch();
    }

    /// <summary>Reads every PDF (in the background) and matches each page to a loaded sheet.</summary>
    public async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            var paths = Files.ToList();
            var results = await Task.Run(() => paths.Select(p =>
            {
                try
                {
                    if (System.IO.Path.GetExtension(p).Equals(".csv", StringComparison.OrdinalIgnoreCase))
                        return (Path: p, Details: new PdfDetails { Path = p, Markups = BluebeamCsv.Read(p) }, Error: (string?)null);
                    return (Path: p, Details: PdfInspector.Read(p), Error: (string?)null);
                }
                catch (Exception ex)
                {
                    return (Path: p, Details: new PdfDetails { Path = p }, Error: ex.Message);
                }
            }).ToList());

            var sheets = _main.SheetsByNumber();
            Rows.Clear();
            Pages.Clear();
            Document.Clear();
            foreach (var (path, details, _) in results)
            {
                foreach (var m in details.Markups.OrderBy(m => m.Sheet, NaturalComparer.Instance).ThenBy(m => m.Page).ThenBy(m => m.Date))
                    Rows.Add(new MarkupRow(m, path, m.Sheet.Length > 0 && sheets.TryGetValue(ExcelCompare.NormalizeKey(m.Sheet), out var row) ? row : null));
                foreach (var page in details.Pages) Pages.Add(new PageRow(page));
                foreach (var p in details.Document) Document.Add(new DocumentRow(details.File, p.Group, p.Name, p.Value));
            }
            ShowDetails("Select a markup or a page to see everything stored on it.", Array.Empty<PdfProperty>());

            var review = Rows.Where(r => !r.M.IsDrawingText).ToList();
            int drawingText = Rows.Count - review.Count;
            int open = review.Count(r => r.IsOpen);
            int onSheets = review.Select(r => r.Sheet).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            int notInModel = review.Count(r => !r.InModel);
            Summary = (review.Count == 0
                ? (Files.Count == 0 ? "Add PDFs to read them." : $"{Files.Count} file(s) · {Pages.Count} page(s) · no markups")
                : $"{Files.Count} file(s) · {Pages.Count} page(s) · {review.Count} markup(s) on {onSheets} sheet(s) · {open} open · {review.Count - open} closed" +
                  (notInModel > 0 ? $" · {notInModel} on sheets not in the loaded models" : ""))
                + (drawingText > 0 ? $" · {drawingText} piece(s) of drawing text" : "");
            var errors = results.Where(r => r.Error is not null).Select(r => $"{Path.GetFileName(r.Path)}: {r.Error}").ToList();
            Message = errors.Count > 0
                ? "Could not read " + string.Join("; ", errors)
                : sheets.Count == 0 && Rows.Count > 0
                    ? "Show Sheets in the main window to see each sheet's name and to open sheets in Revit or AutoCAD."
                    : Files.Count > 0 ? $"Read {DateTime.Now:t}. Saves in Bluebeam show up here by themselves." : "";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Bluebeam saves the markups into the PDF: read again shortly after a save.</summary>
    private void Watch()
    {
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
        foreach (var folder in Files.Select(f => System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(f))).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (folder is null || !Directory.Exists(folder)) continue;
            try
            {
                var w = new FileSystemWatcher(folder) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
                void OnEvent(string path)
                {
                    if (Files.Contains(path, StringComparer.OrdinalIgnoreCase))
                        Application.Current?.Dispatcher.BeginInvoke(OnSaved);
                }
                w.Changed += (_, e) => OnEvent(e.FullPath);
                w.Created += (_, e) => OnEvent(e.FullPath);
                w.Renamed += (_, e) => OnEvent(e.FullPath);
                w.EnableRaisingEvents = true;
                _watchers.Add(w);
            }
            catch (Exception ex)
            {
                HubLog.Warn("Could not watch the markup PDFs.", ex);
            }
        }
    }

    private async void OnSaved()
    {
        _saveDelay?.Cancel();
        var delay = _saveDelay = new CancellationTokenSource();
        try { await Task.Delay(1500, delay.Token); }
        catch (OperationCanceledException) { return; }
        await RefreshAsync();
    }

    public void Start()
    {
        Watch();
        _ = RefreshAsync();
    }

    public void Dispose()
    {
        _saveDelay?.Cancel();
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
    }

    // ------------------------------------------------------------------ actions

    private void OpenPdf()
    {
        string? path = SelectedRows?.Invoke().FirstOrDefault()?.Path ?? SelectedFile ?? Files.FirstOrDefault();
        if (path is null) return;
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Message = "Could not open the PDF: " + ex.Message; }
    }

    private Task ShowInModelAsync()
    {
        var rows = (SelectedRows?.Invoke() ?? Array.Empty<MarkupRow>()).Where(r => r.Model is not null).Select(r => r.Model!).Distinct().ToList();
        if (rows.Count == 0)
        {
            Message = "Select markups on sheets that are in the loaded models (Sheets view) to open those sheets.";
            return Task.CompletedTask;
        }
        return _main.ShowInModelAsync(rows);
    }

    /// <summary>The list shown (Markups, Pages or Document) as a workbook, with every value as a column.</summary>
    private void Export()
    {
        string what = _tab switch { 1 => "Pages", 2 => "PDF details", _ => "Markups" };
        var dialog = new SaveFileDialog
        {
            Filter = "Excel workbook (*.xlsx)|*.xlsx",
            FileName = $"{what} {DateTime.Now:yyyy-MM-dd}.xlsx",
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            if (_tab == 2)
            {
                var rows = DocumentView.Cast<DocumentRow>().Select(r => (IReadOnlyList<string?>)new[] { r.File, r.Group, r.Name, r.Value });
                ExcelWorkbook.Export(dialog.FileName, new[] { "File", "Group", "Name", "Value" }, rows, "Document");
            }
            else
            {
                // One column for each value found on any row, in the order first seen.
                var shown = _tab == 1
                    ? PagesView.Cast<PageRow>().Select(r => r.Properties.ToList()).ToList()
                    : View.Cast<MarkupRow>().Select(r => r.Properties.ToList()).ToList();
                var first = _tab == 1 ? "Page" : "Where";
                static string Header(PdfProperty p, string plain) => p.Group == plain || p.Group == "Markup" ? p.Name : $"{p.Group}: {p.Name}";
                var headers = shown.SelectMany(l => l.Select(p => Header(p, first))).Distinct(StringComparer.Ordinal).ToList();
                var index = headers.Select((h, i) => (h, i)).ToDictionary(x => x.h, x => x.i, StringComparer.Ordinal);
                var rows = shown.Select(l =>
                {
                    var cells = new string?[headers.Count];
                    foreach (var p in l) cells[index[Header(p, first)]] ??= p.Value;
                    return (IReadOnlyList<string?>)cells;
                });
                ExcelWorkbook.Export(dialog.FileName, headers, rows, what);
            }
            Message = $"Saved {Path.GetFileName(dialog.FileName)}.";
        }
        catch (Exception ex)
        {
            Message = "Could not save: " + ex.Message;
        }
    }
}
