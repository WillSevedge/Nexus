using System.Collections.ObjectModel;
using Nexus.Hub.Core;
using Nexus.Hub.Core.Bulk;

namespace Nexus.Hub.ViewModels;

/// <summary>A column that can be renamed in bulk.</summary>
public sealed record RenameField(string ColumnId, string Label, bool Unique)
{
    public override string ToString() => Label;
}

/// <summary>
/// Rename &amp; renumber: pick a column (sheet number, title...), a way to change it (renumber from a pattern,
/// find &amp; replace, capitals, add or remove text), see every result before anything changes, then stage the
/// changes as ordinary edits (applied with Review &amp; apply).
/// </summary>
public sealed class RenameViewModel : Observable
{
    private readonly MainViewModel _main;
    private readonly IReadOnlyList<TableRow> _rows;
    private RenameField? _field;
    private int _modeIndex;
    private string _summary = "";

    public RenameViewModel(MainViewModel main, IReadOnlyList<TableRow> rows, string scope, IReadOnlyList<RenameField> fields)
    {
        _main = main;
        _rows = rows;
        Scope = scope;
        Fields = fields;
        _field = fields.FirstOrDefault();
        Options.Pattern = SuggestPattern();
        StageCommand = new RelayCommand(() => { Stage(); return Task.CompletedTask; }, () => IncludedCount > 0);
        SelectAllCommand = new RelayCommand(() => { SetIncluded(true); return Task.CompletedTask; });
        SelectNoneCommand = new RelayCommand(() => { SetIncluded(false); return Task.CompletedTask; });
        Refresh();
    }

    public string Scope { get; }
    public IReadOnlyList<RenameField> Fields { get; }
    public RenameOptions Options { get; } = new();
    public ObservableCollection<RenameRow> Preview { get; } = new();
    public RelayCommand StageCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand SelectNoneCommand { get; }

    /// <summary>Raised after the changes were staged (the window closes).</summary>
    public event Action? Staged;

    public RenameField? Field
    {
        get => _field;
        set
        {
            if (!Set(ref _field, value)) return;
            if (value?.Unique == true && ModeIndex == 0) Pattern = SuggestPattern();
            Refresh();
        }
    }

    /// <summary>0 Renumber, 1 Find and replace, 2 Capitals, 3 Add text, 4 Remove text (tab order).</summary>
    public int ModeIndex
    {
        get => _modeIndex;
        set
        {
            if (!Set(ref _modeIndex, value)) return;
            Options.Mode = (RenameMode)value;
            Refresh();
        }
    }

    // Option properties (each change refreshes the preview).
    public string Pattern { get => Options.Pattern; set { Options.Pattern = value ?? ""; Changed(); } }
    public int Start { get => Options.Start; set { Options.Start = value; Changed(); } }
    public int Step { get => Options.Step; set { Options.Step = value; Changed(); } }
    public string Find { get => Options.Find; set { Options.Find = value ?? ""; Changed(); } }
    public string ReplaceWith { get => Options.ReplaceWith; set { Options.ReplaceWith = value ?? ""; Changed(); } }
    public bool MatchCase { get => Options.MatchCase; set { Options.MatchCase = value; Changed(); } }
    public bool WholeValue { get => Options.WholeValue; set { Options.WholeValue = value; Changed(); } }
    public string Prefix { get => Options.Prefix; set { Options.Prefix = value ?? ""; Changed(); } }
    public string Suffix { get => Options.Suffix; set { Options.Suffix = value ?? ""; Changed(); } }
    public bool Upper { get => Options.Case == TextCase.Upper; set { if (value) { Options.Case = TextCase.Upper; Changed(); } } }
    public bool Lower { get => Options.Case == TextCase.Lower; set { if (value) { Options.Case = TextCase.Lower; Changed(); } } }
    public bool Title { get => Options.Case == TextCase.Title; set { if (value) { Options.Case = TextCase.Title; Changed(); } } }

    public string Summary
    {
        get => _summary;
        private set => Set(ref _summary, value);
    }

    public int IncludedCount => Preview.Count(r => r.Include && r.CanApply);
    public string StageText => IncludedCount == 0 ? "Nothing to change" : $"Stage {IncludedCount} change{(IncludedCount == 1 ? "" : "s")}";

    private void Changed([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        Raise(name);
        if (name is nameof(Upper) or nameof(Lower) or nameof(Title))
        {
            Raise(nameof(Upper));
            Raise(nameof(Lower));
            Raise(nameof(Title));
        }
        Refresh();
    }

    private void Refresh()
    {
        foreach (var r in Preview) r.IncludeChanged -= OnIncludeChanged;
        Preview.Clear();
        if (_field is null)
        {
            Summary = "Nothing here can be renamed.";
            return;
        }
        var results = BulkRename.Preview(_rows, _field.ColumnId, Options, _main.CurrentValue, Editing.Blocker, _field.Unique, _main.LoadedRows);
        foreach (var p in results)
        {
            var row = new RenameRow(p);
            row.IncludeChanged += OnIncludeChanged;
            Preview.Add(row);
        }
        int change = results.Count(r => r.Status == RenameStatus.Change);
        int clash = results.Count(r => r.Status == RenameStatus.Duplicate);
        int locked = results.Count(r => r.Status == RenameStatus.Locked);
        int same = results.Count(r => r.Status == RenameStatus.NoChange);
        Summary = string.Join("  ·  ", new[]
        {
            $"{change} will change",
            clash > 0 ? $"{clash} would clash" : "",
            locked > 0 ? $"{locked} locked" : "",
            same > 0 ? $"{same} unchanged" : "",
        }.Where(s => s.Length > 0));
        RaiseCounts();
    }

    private void OnIncludeChanged() => RaiseCounts();

    private void RaiseCounts()
    {
        Raise(nameof(IncludedCount));
        Raise(nameof(StageText));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    private void SetIncluded(bool include)
    {
        foreach (var r in Preview.Where(r => r.CanApply)) r.Include = include;
    }

    private void Stage()
    {
        if (_field is null) return;
        int staged = 0;
        var problems = new List<string>();
        foreach (var r in Preview.Where(r => r.Include && r.CanApply))
        {
            var reason = _main.TrySetValue(r.Preview.Row, _field.ColumnId, r.New);
            if (reason is null) staged++;
            else problems.Add($"{r.Old}: {reason}");
        }
        _main.Status = $"{staged} {_field.Label.ToLowerInvariant()} change{(staged == 1 ? "" : "s")} staged. Review & apply to write them to the files."
                       + (problems.Count > 0 ? $" Not staged: {string.Join("; ", problems.Take(3))}{(problems.Count > 3 ? "…" : "")}" : "");
        Staged?.Invoke();
    }

    /// <summary>A starting pattern from the first number: "A101" → "A1##", "M-201" → "M-2##".</summary>
    private string SuggestPattern()
    {
        string first = _field is null ? "" : _rows.Select(r => _main.CurrentValue(r, _field.ColumnId) ?? "").FirstOrDefault(v => v.Length > 0) ?? "";
        if (first.Length == 0 || !first.Any(char.IsDigit)) return "A1##";
        int end = first.Length;
        while (end > 0 && !char.IsDigit(first[end - 1])) end--;
        int start = end;
        while (start > 0 && char.IsDigit(first[start - 1])) start--;
        int digits = end - start;
        int keep = digits > 2 ? digits - 2 : 0; // keep the series digit: 101 → 1##
        return first[..(start + keep)] + new string('#', digits - keep) + first[end..];
    }
}

/// <summary>One preview line: include it or not, old → new, and its status.</summary>
public sealed class RenameRow : Observable
{
    private bool _include;

    public RenameRow(RenamePreview preview)
    {
        Preview = preview;
        _include = preview.CanApply;
    }

    public RenamePreview Preview { get; }
    public string Where => $"{Preview.Row.Document}";
    public string Item => Preview.Row.Item.Trim();
    public string Old => Preview.Old;
    public string New => Preview.New;
    public bool CanApply => Preview.CanApply;
    public bool IsChange => Preview.Status != RenameStatus.NoChange;
    public string Status => Preview.Status switch
    {
        RenameStatus.Change => "Will change",
        RenameStatus.NoChange => "Unchanged",
        RenameStatus.Duplicate => "Clash",
        RenameStatus.Locked => "Locked",
        _ => "Empty",
    };
    public string StatusKind => Preview.Status.ToString();
    public string? Message => Preview.Message;

    public event Action? IncludeChanged;

    public bool Include
    {
        get => _include;
        set
        {
            if (!CanApply) value = false;
            if (Set(ref _include, value)) IncludeChanged?.Invoke();
        }
    }
}
