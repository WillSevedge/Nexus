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
using Nexus.Hub.Pdf;

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
    public string Content => P.Content;
    public string Text => P.TextPieces == 0 ? "" : P.TextPieces.ToString();
    public string Title => $"{File} · page {Page}" + (Sheet.Length > 0 ? $" · {Sheet}" : "");
    public IEnumerable<PdfProperty> Properties =>
        new[]
        {
            new PdfProperty("Page", "File", File), new PdfProperty("Page", "Page", Page.ToString()), new PdfProperty("Page", "Sheet", Sheet),
            new PdfProperty("Page", "Label", Label), new PdfProperty("Page", "Bookmark", Bookmark), new PdfProperty("Page", "Size", Size),
            new PdfProperty("Page", "Paper", Paper), new PdfProperty("Page", "Rotation", Rotation), new PdfProperty("Page", "Scale", Scale),
            new PdfProperty("Page", "Markups", Markups), new PdfProperty("Page", "Content", Content),
            new PdfProperty("Page", "Pieces of text", Text),
        }.Where(p => p.Value.Length > 0).Concat(P.Properties);
}

/// <summary>One piece of text on a page in the Text list.</summary>
public sealed class TextRow
{
    public TextRow(PdfTextPiece t) => T = t;

    public PdfTextPiece T { get; }
    public string File => T.File;
    public int Page => T.Page;
    public string Sheet => T.Sheet;
    public string Text => T.Text;
    public string Source => T.Source;
    /// <summary>"41.75, 3.87 in": from the left and from the bottom of the page.</summary>
    public string Position => FormattableString.Invariant($"{T.FromLeftIn:0.##}, {T.FromBottomIn:0.##} in");
    public double FromLeft => T.FromLeftIn;
    public double FromBottom => T.FromBottomIn;
}

/// <summary>One value of a PDF in the Document list.</summary>
public sealed record DocumentRow(string File, string Group, string Name, string Value);

/// <summary>
/// PDFs next to the sheets: every markup from Bluebeam (or any PDF reviewer) with every value stored on it, each page
/// (sheet, size, scale), the text printed on the pages and what the file says about itself (document information,
/// XMP, layers, security…). Sources are files, or folders that are watched: whatever lands in them is read, and read
/// again when it changes. Markup lists exported from a Studio Session (CSV) are read the same way. Nothing is changed.
/// </summary>
public sealed class MarkupsViewModel : Observable, IDisposable
{
    public static readonly string[] Filters = { "Open", "Everything", "Closed", "On sheets not in the model" };

    /// <summary>Most PDFs read from the sources at once (a whole project's archive is not what this window is for).</summary>
    private const int MaxFiles = 2000;
    private const string NothingSelected = "Select a markup or a page to see everything stored on it.";

    private readonly MainViewModel _main;
    private readonly List<FileSystemWatcher> _watchers = new();
    /// <summary>What was read from each file, kept while the file stays as it was (size and time saved).</summary>
    private readonly Dictionary<string, (long Length, DateTime Saved, PdfDetails Details)> _read = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _saveDelay;
    private CancellationTokenSource? _scans;
    private string _filter = Filters[0];
    private string _search = "";
    private string _summary = "";
    private string _message = "";
    private bool _busy;
    private bool _again;

    public MarkupsViewModel(MainViewModel main)
    {
        _main = main;
        foreach (var f in main.MarkupPdfs.Where(p => File.Exists(p) || Directory.Exists(p))) Files.Add(f);
        View = CollectionViewSource.GetDefaultView(Rows);
        View.Filter = o => o is MarkupRow r && Shows(r);
        PagesView = CollectionViewSource.GetDefaultView(Pages);
        PagesView.Filter = o => o is PageRow r && Found(r.File, r.Sheet, r.Label, r.Bookmark, r.Paper, r.Scale, r.Content);
        TextView = CollectionViewSource.GetDefaultView(Text);
        TextView.Filter = o => o is TextRow r && Found(r.Text, r.Sheet, r.File);
        DocumentView = CollectionViewSource.GetDefaultView(Document);
        DocumentView.Filter = o => o is DocumentRow r && Found(r.File, r.Group, r.Name, r.Value);
        DetailsView = CollectionViewSource.GetDefaultView(Details);
        AddCommand = new RelayCommand(AddAsync);
        AddFolderCommand = new RelayCommand(AddFolderAsync);
        RemoveCommand = new RelayCommand(() => { Remove(); return Task.CompletedTask; }, () => SelectedFile is not null);
        RefreshCommand = new RelayCommand(() => RefreshAsync(again: true), () => Files.Count > 0);
        ExportCommand = new RelayCommand(() => { Export(); return Task.CompletedTask; }, () => Rows.Count + Pages.Count + Document.Count > 0);
        OpenPdfCommand = new RelayCommand(() => { OpenPdf(); return Task.CompletedTask; }, () => Files.Count > 0);
        ShowInModelCommand = new RelayCommand(ShowInModelAsync);
        ReadScansCommand = new RelayCommand(ReadScansAsync, () => !_busy && ScannedPages > 0);
    }

    /// <summary>The sources: PDF or CSV files, and folders (every PDF in them, subfolders included, watched).</summary>
    public ObservableCollection<string> Files { get; } = new();
    public ObservableCollection<MarkupRow> Rows { get; } = new();
    public ICollectionView View { get; }
    public ObservableCollection<PageRow> Pages { get; } = new();
    public ICollectionView PagesView { get; }
    public ObservableCollection<TextRow> Text { get; } = new();
    public ICollectionView TextView { get; }
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
    public RelayCommand ReadScansCommand { get; }

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
    /// <summary>0 Markups, 1 Pages, 2 Text, 3 Document (Export saves the list shown).</summary>
    public int Tab
    {
        get => _tab;
        set
        {
            if (Set(ref _tab, value)) Raise(nameof(IsMarkupsTab));
        }
    }

    public bool IsMarkupsTab => _tab == 0;

    private string _detailsTitle = NothingSelected;
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

    private int _scannedPages;
    /// <summary>Pages that are only a picture and have not been read yet.</summary>
    public int ScannedPages
    {
        get => _scannedPages;
        private set
        {
            if (!Set(ref _scannedPages, value)) return;
            Raise(nameof(HasScans));
            Raise(nameof(ScansText));
        }
    }

    public bool HasScans => _scannedPages > 0;
    public string ScansText => $"Read {_scannedPages} scanned page{(_scannedPages == 1 ? "" : "s")}";

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
            TextView.Refresh();
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
        private set
        {
            if (Set(ref _busy, value)) System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }

    private bool Shows(MarkupRow r)
    {
        bool kind = _filter switch
        {
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

    // ------------------------------------------------------------------ sources

    private async Task AddAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose PDFs (sets or single sheets) or markup lists exported from Bluebeam (CSV)",
            Filter = "PDFs and Bluebeam markup lists (*.pdf;*.csv)|*.pdf;*.csv|PDF files (*.pdf)|*.pdf|Bluebeam markup list (*.csv)|*.csv",
            Multiselect = true,
        };
        if (dialog.ShowDialog() == true) await AddPathsAsync(dialog.FileNames);
    }

    /// <summary>A folder to watch: every PDF in it and its subfolders, now and whenever one is added or saved.</summary>
    private async Task AddFolderAsync()
    {
        var dialog = new OpenFolderDialog { Title = "Choose a folder of PDFs to watch (subfolders included)" };
        if (dialog.ShowDialog() == true) await AddPathsAsync(new[] { dialog.FolderName });
    }

    /// <summary>Adds files (PDF, CSV) and folders, from the dialogs or dropped on the window.</summary>
    public async Task AddPathsAsync(IEnumerable<string> paths)
    {
        int added = 0, skipped = 0;
        foreach (var path in paths)
        {
            bool folder = Directory.Exists(path);
            string extension = System.IO.Path.GetExtension(path);
            if (!folder && !(File.Exists(path) && (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase) || extension.Equals(".csv", StringComparison.OrdinalIgnoreCase))))
            {
                skipped++;
                continue;
            }
            if (Files.Contains(path, StringComparer.OrdinalIgnoreCase)) continue;
            Files.Add(path);
            added++;
        }
        if (added > 0)
        {
            Remember();
            await RefreshAsync();
        }
        if (skipped > 0) Message = $"{skipped} item(s) were not added: only PDFs, Bluebeam CSV lists and folders are read.";
    }

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

    /// <summary>The files behind the sources: each file source, and every PDF under each folder source.</summary>
    private static (List<string> Paths, bool TooMany) Expand(IReadOnlyList<string> sources)
    {
        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 8 };
        foreach (var source in sources)
        {
            IEnumerable<string> found;
            try
            {
                found = Directory.Exists(source)
                    ? Directory.EnumerateFiles(source, "*.pdf", options).OrderBy(f => f, NaturalComparer.Instance).ToList()
                    : File.Exists(source) ? new[] { source } : Array.Empty<string>();
            }
            catch
            {
                continue; // a folder that went away (a drive not connected): its files come back with it
            }
            foreach (var f in found)
            {
                if (paths.Count >= MaxFiles) return (paths, true);
                if (seen.Add(f)) paths.Add(f);
            }
        }
        return (paths, false);
    }

    /// <summary>
    /// Reads the sources in the background and matches each page to a loaded sheet. Files that have not changed
    /// since they were read are not read again, unless <paramref name="again"/> (the Refresh button).
    /// </summary>
    public async Task RefreshAsync(bool again = false)
    {
        if (_busy)
        {
            _again = true; // a save while reading: read once more when this pass ends
            return;
        }
        IsBusy = true;
        try
        {
            if (again) _read.Clear();
            var sources = Files.ToList();
            var progress = new Progress<string>(text => Message = text);
            var known = new Dictionary<string, (long Length, DateTime Saved, PdfDetails Details)>(_read, StringComparer.OrdinalIgnoreCase);
            var (results, tooMany) = await Task.Run(() =>
            {
                var (paths, capped) = Expand(sources);
                var read = new (string Path, PdfDetails Details, string? Error, long Length, DateTime Saved)[paths.Count];
                int done = 0;
                Parallel.For(0, paths.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4) }, i =>
                {
                    string p = paths[i];
                    long length = 0;
                    DateTime saved = default;
                    try
                    {
                        var info = new FileInfo(p);
                        (length, saved) = (info.Length, info.LastWriteTimeUtc);
                        if (known.TryGetValue(p, out var kept) && kept.Length == length && kept.Saved == saved)
                            read[i] = (p, kept.Details, null, length, saved);
                        else if (System.IO.Path.GetExtension(p).Equals(".csv", StringComparison.OrdinalIgnoreCase))
                            read[i] = (p, new PdfDetails { Path = p, Markups = BluebeamCsv.Read(p) }, null, length, saved);
                        else
                        {
                            var details = PdfInspector.Read(p);
                            // Scanned pages recognised earlier, for the file as it is now.
                            if (details.ScannedPages.Count > 0 && PdfOcr.Kept(p) is { } recognised) PdfOcr.Merge(details, recognised);
                            read[i] = (p, details, null, length, saved);
                        }
                    }
                    catch (Exception ex)
                    {
                        read[i] = (p, new PdfDetails { Path = p }, ex.Message, length, saved);
                    }
                    int n = Interlocked.Increment(ref done);
                    if (paths.Count > 5 && (n % 5 == 0 || n == paths.Count)) ((IProgress<string>)progress).Report($"Reading {n} of {paths.Count} files…");
                });
                return (read, capped);
            });

            _read.Clear();
            foreach (var r in results.Where(r => r.Error is null)) _read[r.Path] = (r.Length, r.Saved, r.Details);
            Show(results.Select(r => (r.Path, r.Details)).ToList());

            var errors = results.Where(r => r.Error is not null).Select(r => $"{Path.GetFileName(r.Path)}: {r.Error}").ToList();
            Message = errors.Count > 0
                ? $"Could not read {errors.Count} file(s): " + string.Join("; ", errors.Take(3)) + (errors.Count > 3 ? "…" : "")
                : tooMany ? $"More than {MaxFiles} PDFs: the first {MaxFiles} are read. Watch a subfolder for the rest."
                : _main.SheetsByNumber().Count == 0 && Rows.Count > 0
                    ? "Show Sheets in the main window to see each sheet's name and to open sheets in Revit or AutoCAD."
                    : Files.Count > 0 ? $"Read {DateTime.Now:t}. New and saved PDFs show up here by themselves." : "";
        }
        finally
        {
            IsBusy = false;
        }
        if (_again)
        {
            _again = false;
            await RefreshAsync();
        }
    }

    /// <summary>Fills the lists from what was read.</summary>
    private void Show(IReadOnlyList<(string Path, PdfDetails Details)> results)
    {
        var sheets = _main.SheetsByNumber();
        Rows.Clear();
        Pages.Clear();
        Text.Clear();
        Document.Clear();
        foreach (var (path, details) in results)
        {
            foreach (var m in details.Markups.OrderBy(m => m.Sheet, NaturalComparer.Instance).ThenBy(m => m.Page).ThenBy(m => m.Date))
                Rows.Add(new MarkupRow(m, path, m.Sheet.Length > 0 && sheets.TryGetValue(ExcelCompare.NormalizeKey(m.Sheet), out var row) ? row : null));
            foreach (var page in details.Pages) Pages.Add(new PageRow(page));
            foreach (var piece in details.Text) Text.Add(new TextRow(piece));
            foreach (var p in details.Document) Document.Add(new DocumentRow(details.File, p.Group, p.Name, p.Value));
        }
        ShowDetails(NothingSelected, Array.Empty<PdfProperty>());
        ScannedPages = Pages.Count(p => p.Content == PdfInspector.Scan);

        int open = Rows.Count(r => r.IsOpen);
        int onSheets = Rows.Select(r => r.Sheet).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        int notInModel = Rows.Count(r => !r.InModel);
        int files = results.Count;
        Summary = files == 0
            ? (Files.Count == 0 ? "Add PDFs, or a folder to watch, to read them." : "No PDFs in these folders yet.")
            : $"{files} file(s) · {Pages.Count} page(s) · " +
              (Rows.Count == 0 ? "no markups" : $"{Rows.Count} markup(s) on {onSheets} sheet(s) · {open} open · {Rows.Count - open} closed") +
              (notInModel > 0 ? $" · {notInModel} on sheets not in the loaded models" : "") +
              (ScannedPages > 0 ? $" · {ScannedPages} scanned page(s) not read yet" : "");
    }

    // ------------------------------------------------------------------ scanned pages

    /// <summary>Reads the text of the pages that are only a picture, with Windows' text recognition (kept for next time).</summary>
    private async Task ReadScansAsync()
    {
        var files = _read.Where(r => r.Value.Details.ScannedPages.Count > 0).Select(r => (Path: r.Key, r.Value.Details)).ToList();
        if (files.Count == 0) return;
        IsBusy = true;
        _scans = new CancellationTokenSource();
        int pages = 0, total = files.Sum(f => f.Details.ScannedPages.Count);
        try
        {
            foreach (var (path, details) in files)
            {
                foreach (int page in details.ScannedPages)
                {
                    Message = $"Reading scanned page {pages + 1} of {total}: {Path.GetFileName(path)}, page {page}…";
                    var recognised = await PdfOcr.ReadAsync(path, new[] { page }, _scans.Token);
                    var all = (PdfOcr.Kept(path) ?? new List<PdfTextPiece>()).Concat(recognised).ToList();
                    PdfOcr.Keep(path, all);
                    PdfOcr.Merge(details, recognised);
                    pages++;
                }
            }
            Message = $"Read {pages} scanned page(s). Their text is in the Text tab and is kept for next time.";
        }
        catch (OperationCanceledException)
        {
            Message = $"Stopped after {pages} of {total} scanned page(s).";
        }
        catch (Exception ex)
        {
            HubLog.Warn("Reading scanned pages failed.", ex);
            Message = "Could not read the scanned pages: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
        Show(_read.OrderBy(r => r.Key, NaturalComparer.Instance).Select(r => (r.Key, r.Value.Details)).ToList());
    }

    // ------------------------------------------------------------------ watching

    /// <summary>
    /// Watches the folders of the sources: a folder source for any PDF added, saved or removed in it (subfolders
    /// included); a file source for that file (Bluebeam saves the markups into the PDF).
    /// </summary>
    private void Watch()
    {
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
        var folders = Files.Where(Directory.Exists).ToList();
        var fileFolders = Files.Where(f => !Directory.Exists(f)).Select(f => System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(f)))
            .Where(d => d is not null && Directory.Exists(d)).Select(d => d!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var (folder, whole) in folders.Select(f => (f, true)).Concat(fileFolders.Select(f => (f, false))))
        {
            try
            {
                var w = new FileSystemWatcher(folder)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                    IncludeSubdirectories = whole,
                    InternalBufferSize = 64 * 1024,
                };
                void OnEvent(string path)
                {
                    bool ours = whole
                        ? System.IO.Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase)
                        : Files.Contains(path, StringComparer.OrdinalIgnoreCase);
                    if (ours) Application.Current?.Dispatcher.BeginInvoke(OnSaved);
                }
                w.Changed += (_, e) => OnEvent(e.FullPath);
                w.Created += (_, e) => OnEvent(e.FullPath);
                w.Deleted += (_, e) => OnEvent(e.FullPath);
                w.Renamed += (_, e) =>
                {
                    OnEvent(e.FullPath);
                    OnEvent(e.OldFullPath);
                };
                // Too many changes at once to list (a big copy): read the folder again.
                w.Error += (_, _) => Application.Current?.Dispatcher.BeginInvoke(OnSaved);
                w.EnableRaisingEvents = true;
                _watchers.Add(w);
            }
            catch (Exception ex)
            {
                HubLog.Warn($"Could not watch {folder} for PDFs.", ex);
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
        _scans?.Cancel();
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
    }

    // ------------------------------------------------------------------ actions

    private void OpenPdf()
    {
        string? path = SelectedRows?.Invoke().FirstOrDefault()?.Path ?? SelectedFile ?? Files.FirstOrDefault();
        if (path is null) return;
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Message = "Could not open it: " + ex.Message; }
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

    /// <summary>The list shown (Markups, Pages, Text or Document) as a workbook, with every value as a column.</summary>
    private void Export()
    {
        string what = _tab switch { 1 => "Pages", 2 => "Text", 3 => "PDF details", _ => "Markups" };
        var dialog = new SaveFileDialog
        {
            Filter = "Excel workbook (*.xlsx)|*.xlsx",
            FileName = $"{what} {DateTime.Now:yyyy-MM-dd}.xlsx",
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            if (_tab == 3)
            {
                var rows = DocumentView.Cast<DocumentRow>().Select(r => (IReadOnlyList<string?>)new[] { r.File, r.Group, r.Name, r.Value });
                ExcelWorkbook.Export(dialog.FileName, new[] { "File", "Group", "Name", "Value" }, rows, "Document");
            }
            else if (_tab == 2)
            {
                var rows = TextView.Cast<TextRow>().Select(r => (IReadOnlyList<string?>)new[]
                {
                    r.File, r.Page.ToString(), r.Sheet, r.Text, r.Source,
                    r.FromLeft.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture), r.FromBottom.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
                });
                ExcelWorkbook.Export(dialog.FileName, new[] { "File", "Page", "Sheet", "Text", "Source", "From left (in)", "From bottom (in)" }, rows, "Text");
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
