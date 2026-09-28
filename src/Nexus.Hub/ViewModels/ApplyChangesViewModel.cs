using System.Collections.ObjectModel;
using Nexus.Hub.Core;

namespace Nexus.Hub.ViewModels;

/// <summary>One pending edit in the review dialog, and what happened when it was applied.</summary>
public sealed class ChangeRow : Observable
{
    private string _status = "";
    private string? _message;
    private string? _result;

    public ChangeRow(CellEdit edit) => Edit = edit;

    public CellEdit Edit { get; }
    public string Document => Edit.Row.Document;
    public string Item => Edit.Row.Item.Trim();
    public string Property => Edit.ColumnId;
    public string OldValue => Edit.OldValue;
    public string NewValue => Edit.NewValue;

    /// <summary>"" before applying, then Applied / Unchanged / Skipped / Failed.</summary>
    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public string? Message
    {
        get => _message;
        set => Set(ref _message, value);
    }

    /// <summary>The value as the host shows it after applying.</summary>
    public string? Result
    {
        get => _result;
        private set => Set(ref _result, value);
    }

    public void SetOutcome(string status, string? message, string? result)
    {
        Status = status;
        Message = message;
        Result = result;
    }
}

public sealed class ApplyChangesViewModel : Observable
{
    private readonly Func<IReadOnlyList<ChangeRow>, Task<List<ResultRun>>> _apply;
    private bool _applying;
    private bool _applied;
    private string _summary;

    public ApplyChangesViewModel(IEnumerable<CellEdit> edits, Func<IReadOnlyList<ChangeRow>, Task<List<ResultRun>>> apply)
    {
        _apply = apply;
        foreach (var e in edits) Changes.Add(new ChangeRow(e));
        int docs = Changes.Select(c => (c.Edit.ProcessId, c.Edit.DocumentId)).Distinct().Count();
        _summary = $"{Changes.Count} change(s) in {docs} document(s). Each document gets one undo step " +
                   "(\"Nexus: edit …\"), so Undo in Revit reverts the whole batch. " +
                   "Values that changed in the model since they were read are skipped.";
        ApplyCommand = new RelayCommand(ApplyAsync, () => !_applying && !_applied);
    }

    public ObservableCollection<ChangeRow> Changes { get; } = new();
    public RelayCommand ApplyCommand { get; }

    /// <summary>Results whose documents were changed and should be read again.</summary>
    public List<ResultRun> ChangedRuns { get; private set; } = new();

    public string Summary
    {
        get => _summary;
        private set => Set(ref _summary, value);
    }

    public bool Applied
    {
        get => _applied;
        private set
        {
            if (Set(ref _applied, value)) Raise(nameof(CloseLabel));
        }
    }

    /// <summary>True while changes are being sent; the window cannot close meanwhile.</summary>
    public bool IsApplying => _applying;

    public string CloseLabel => _applied ? "Close" : "Cancel";

    private async Task ApplyAsync()
    {
        _applying = true;
        Summary = "Applying…";
        try
        {
            ChangedRuns = await _apply(Changes);
        }
        finally
        {
            _applying = false;
            Applied = true;
        }

        var counts = Changes.GroupBy(c => c.Status).Select(g => $"{g.Count()} {g.Key.ToLowerInvariant()}");
        Summary = "Done: " + string.Join(", ", counts) + "." +
                  (Changes.Any(c => c.Status is "Failed" or "Skipped") ? " See the Message column for why." : "") +
                  (ChangedRuns.Count > 0 ? " The table will refresh from the model when you close this window." : "");
    }
}
