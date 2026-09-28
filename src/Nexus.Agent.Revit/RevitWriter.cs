using System.Globalization;
using Nexus.Agent;
using Nexus.Contracts;
using Autodesk.Revit.DB;

namespace Nexus.Agent.Revit;

/// <summary>
/// Applies hub edits to parameters. All changes to one document go into one
/// transaction, so a single Undo in Revit reverts the whole batch. If Revit
/// reports an error when committing, the batch is rolled back and nothing changes.
/// </summary>
internal sealed class RevitWriter : IHostDataWriter<Document>
{
    public WriteResult Write(Document doc, WriteRequest request, AgentLog log, CancellationToken ct)
    {
        var result = new WriteResult();
        for (int i = 0; i < request.Changes.Count; i++)
            result.Results.Add(new ChangeResult { Index = i, Status = ChangeStatus.Skipped });

        string? docBlocker = doc.IsLinked ? "Linked document (read-only)"
            : doc.IsReadOnly ? "Document is read-only"
            : doc.IsModifiable ? "Revit is in the middle of another edit" : null;
        if (docBlocker is not null)
        {
            foreach (var r in result.Results) r.Message = docBlocker;
            return result;
        }
        ct.ThrowIfCancellationRequested();

        string undoName = request.Changes.Count == 1
            ? $"Nexus: edit {request.Changes[0].PropertyName}"
            : $"Nexus: edit {request.Changes.Count} values";

        var failures = new FailureCollector();
        var applied = new List<(ChangeResult Result, Parameter Parameter)>();

        using var transaction = new Transaction(doc, undoName);
        var handling = transaction.GetFailureHandlingOptions();
        handling.SetFailuresPreprocessor(failures);
        handling.SetClearAfterRollback(true);
        transaction.SetFailureHandlingOptions(handling);

        if (transaction.Start() != TransactionStatus.Started)
            throw new AgentException(ErrorCodes.HostBusy, "Revit could not start a transaction. Finish the current command or edit mode and try again.");

        try
        {
            for (int i = 0; i < request.Changes.Count; i++)
            {
                var change = request.Changes[i];
                var r = result.Results[i];
                try
                {
                    var p = Apply(doc, change, r);
                    if (p is not null && r.Status == ChangeStatus.Applied) applied.Add((r, p));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    r.Status = ChangeStatus.Failed;
                    r.Message = ex.Message;
                    log.Warn($"Setting '{change.PropertyName}' on {change.OwnerId} failed: {ex.Message}");
                }
            }

            if (applied.Count == 0)
            {
                transaction.RollBack();
                return result;
            }

            var status = transaction.Commit();
            result.Warnings.AddRange(failures.Warnings);
            if (status != TransactionStatus.Committed)
            {
                string why = failures.Errors.Count > 0 ? string.Join("; ", failures.Errors) : $"Revit did not commit the changes ({status}).";
                foreach (var (r, _) in applied)
                {
                    r.Status = ChangeStatus.Failed;
                    r.Message = "Rolled back: " + why;
                    r.NewValue = null;
                }
                return result;
            }
        }
        catch
        {
            if (transaction.HasStarted() && !transaction.HasEnded()) transaction.RollBack();
            throw;
        }

        result.Committed = true;
        result.UndoName = undoName;
        // Report values as Revit shows them after the commit (formulas, formatting, rounding).
        foreach (var (r, p) in applied)
        {
            try { r.NewValue = ParameterReader.CurrentValue(doc, p).Value; } catch { /* keep the pre-commit value */ }
        }
        return result;
    }

    /// <summary>Sets one value inside the open transaction. Returns the parameter when it was set.</summary>
    private static Parameter? Apply(Document doc, PropertyChange change, ChangeResult r)
    {
        var element = string.IsNullOrEmpty(change.OwnerId) ? null : doc.GetElement(change.OwnerId);
        if (element is null)
        {
            r.Message = "The element no longer exists.";
            return null;
        }

        string? blocker = Editability.Blocker(doc, element);
        if (blocker is not null)
        {
            r.Message = blocker;
            return null;
        }

        var p = FindParameter(element, change);
        if (p is null)
        {
            r.Status = ChangeStatus.Failed;
            r.Message = $"Parameter '{change.PropertyName}' was not found on the element.";
            return null;
        }
        if (p.IsReadOnly)
        {
            r.Message = "Read-only parameter (calculated or controlled by Revit).";
            return null;
        }

        var before = ParameterReader.CurrentValue(doc, p);
        if (change.ExpectedRawValue is not null && !string.Equals(before.RawValue ?? "", change.ExpectedRawValue, StringComparison.Ordinal))
        {
            r.Message = $"Changed in Revit since it was read (now '{before.Value}'). Run the reader again.";
            return null;
        }

        string? error = SetValue(p, change.Value);
        if (error is not null)
        {
            r.Status = ChangeStatus.Failed;
            r.Message = error;
            return null;
        }

        var after = ParameterReader.CurrentValue(doc, p);
        r.NewValue = after.Value;
        r.Status = string.Equals(after.RawValue, before.RawValue, StringComparison.Ordinal) ? ChangeStatus.Unchanged : ChangeStatus.Applied;
        return p;
    }

    /// <summary>Returns null on success, else why the value was not accepted.</summary>
    private static string? SetValue(Parameter p, string value)
    {
        value ??= "";
        switch (p.StorageType)
        {
            case StorageType.String:
                return p.Set(value) ? null : "Revit rejected the value.";

            case StorageType.Integer:
                if (IsYesNo(p))
                {
                    bool? b = ParseYesNo(value);
                    if (b is null) return $"'{value}' is not Yes or No.";
                    return p.Set(b.Value ? 1 : 0) ? null : "Revit rejected the value.";
                }
                if (int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out int i)
                    || int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out i))
                    return p.Set(i) ? null : "Revit rejected the value.";
                return TrySetValueString(p, value) ? null : $"'{value}' is not a whole number.";

            case StorageType.Double:
                if (string.IsNullOrWhiteSpace(value)) return "A number is required.";
                // Parses in the project's display units, e.g. 10' 6" or 3200 mm.
                return TrySetValueString(p, value) ? null : $"'{value}' is not a valid {DataTypeLabel(p)} value.";

            case StorageType.ElementId:
                return "Editing values that refer to other elements (materials, types, levels...) is not supported yet.";

            default:
                return "This parameter has no editable value.";
        }
    }

    private static bool TrySetValueString(Parameter p, string value)
    {
        try { return p.SetValueString(value.Trim()); }
        catch { return false; }
    }

    private static bool IsYesNo(Parameter p)
    {
        try { return p.Definition.GetDataType() == SpecTypeId.Boolean.YesNo; }
        catch { return false; }
    }

    private static bool? ParseYesNo(string value) => value.Trim().ToLowerInvariant() switch
    {
        "yes" or "y" or "true" or "1" or "on" or "x" => true,
        "no" or "n" or "false" or "0" or "off" or "" => false,
        _ => null,
    };

    private static string DataTypeLabel(Parameter p)
    {
        try { return LabelUtils.GetLabelForSpec(p.Definition.GetDataType()).ToLowerInvariant(); }
        catch { return "number"; }
    }

    private static Parameter? FindParameter(Element element, PropertyChange change)
    {
        string id = change.PropertyId ?? "";
        switch (change.Source)
        {
            case PropertySource.BuiltIn when Enum.TryParse<BuiltInParameter>(id, out var bip):
                return element.get_Parameter(bip);
            case PropertySource.Shared when Guid.TryParse(id, out var guid):
                return element.get_Parameter(guid);
        }

        if (long.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out long paramId))
            foreach (Parameter p in element.Parameters)
                if (p.Id.Value == paramId) return p;

        var byName = element.GetParameters(change.PropertyName);
        return byName.Count == 1 ? byName[0] : null;
    }

    /// <summary>Turns commit-time warnings into messages for the hub instead of Revit dialogs; errors roll back.</summary>
    private sealed class FailureCollector : IFailuresPreprocessor
    {
        public List<string> Warnings { get; } = new();
        public List<string> Errors { get; } = new();

        public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
        {
            bool error = false;
            foreach (var f in accessor.GetFailureMessages())
            {
                string text = f.GetDescriptionText();
                if (f.GetSeverity() == FailureSeverity.Warning)
                {
                    if (!Warnings.Contains(text)) Warnings.Add(text);
                    accessor.DeleteWarning(f);
                }
                else
                {
                    if (!Errors.Contains(text)) Errors.Add(text);
                    error = true;
                }
            }
            return error ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
        }
    }
}
