using Nexus.Agent;
using Nexus.Contracts;
using Autodesk.Revit.DB;

namespace Nexus.Agent.Revit;

/// <summary>
/// Creates sheets from the hub (drawings on the deliverables list that the model does not have yet). All sheets go
/// into one transaction, so one Undo in Revit removes them. New sheets get the title block the model's sheets use
/// most; a number that already exists is skipped.
/// </summary>
internal sealed class RevitSheetCreator : ISheetCreator<Document>
{
    public CreateSheetsResult Create(Document doc, CreateSheetsRequest request, AgentLog log, CancellationToken ct)
    {
        var result = new CreateSheetsResult();
        string? blocker = doc.IsLinked ? "Linked document (read-only)"
            : doc.IsReadOnlyFile ? "The file is open read-only"
            : doc.IsReadOnly ? "Revit is busy (for example showing a warning). Close any dialog in Revit and try again"
            : doc.IsModifiable ? "Revit is in the middle of another edit" : null;
        if (blocker is not null)
        {
            result.Sheets.AddRange(request.Sheets.Select(s => new NewSheetResult { Number = s.Number, Message = blocker }));
            return result;
        }

        var existing = new HashSet<string>(new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
            .Select(s => s.SheetNumber), StringComparer.OrdinalIgnoreCase);
        var titleBlock = TitleBlock(doc);
        result.TitleBlock = titleBlock is null ? "none" : $"{titleBlock.FamilyName}: {titleBlock.Name}";

        string undoName = request.Sheets.Count == 1 ? $"Nexus: create sheet {request.Sheets[0].Number}" : $"Nexus: create {request.Sheets.Count} sheets";
        var failures = new RevitWriter.FailureCollector();
        using var transaction = new Transaction(doc, undoName);
        var handling = transaction.GetFailureHandlingOptions();
        handling.SetFailuresPreprocessor(failures);
        handling.SetClearAfterRollback(true);
        transaction.SetFailureHandlingOptions(handling);
        if (transaction.Start() != TransactionStatus.Started)
            throw new AgentException(ErrorCodes.HostBusy, "Revit could not start a transaction. Finish the current command or edit mode and try again.");

        var created = new List<NewSheetResult>();
        try
        {
            if (titleBlock is not null && !titleBlock.IsActive) titleBlock.Activate();
            foreach (var wanted in request.Sheets)
            {
                ct.ThrowIfCancellationRequested();
                var r = new NewSheetResult { Number = wanted.Number };
                result.Sheets.Add(r);
                string number = wanted.Number.Trim();
                if (number.Length == 0)
                {
                    r.Message = "No sheet number";
                    continue;
                }
                if (!existing.Add(number))
                {
                    r.Message = "A sheet with this number already exists";
                    continue;
                }
                try
                {
                    var sheet = ViewSheet.Create(doc, titleBlock?.Id ?? ElementId.InvalidElementId);
                    sheet.SheetNumber = number;
                    if (wanted.Name.Trim().Length > 0) sheet.Name = wanted.Name.Trim();
                    var missing = new List<string>();
                    foreach (var (names, value) in wanted.Values)
                    {
                        if (value.Trim().Length == 0) continue;
                        var p = names.Split('|').Select(n => sheet.LookupParameter(n.Trim()))
                            .FirstOrDefault(x => x is not null && !x.IsReadOnly && x.StorageType == StorageType.String);
                        if (p is null) missing.Add(names.Split('|')[0]);
                        else p.Set(value.Trim());
                    }
                    r.Created = true;
                    r.Id = sheet.UniqueId;
                    if (missing.Count > 0) r.Message = "Not set (the sheet has no such text parameter): " + string.Join(", ", missing);
                    created.Add(r);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    existing.Remove(number);
                    r.Message = ex.Message;
                    log.Warn($"Creating sheet {number} failed: {ex.Message}");
                }
            }

            if (created.Count == 0)
            {
                transaction.RollBack();
                return result;
            }
            if (transaction.Commit() != TransactionStatus.Committed)
            {
                string why = failures.Errors.Count > 0 ? string.Join("; ", failures.Errors) : "Revit did not commit the new sheets.";
                foreach (var r in created)
                {
                    r.Created = false;
                    r.Id = null;
                    r.Message = "Rolled back: " + why;
                }
                return result;
            }
        }
        catch
        {
            if (transaction.HasStarted() && !transaction.HasEnded()) transaction.RollBack();
            throw;
        }
        result.UndoName = undoName;
        return result;
    }

    /// <summary>The title block type placed on the most sheets; else the first one loaded; null when there is none.</summary>
    private static FamilySymbol? TitleBlock(Document doc)
    {
        var used = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks).OfClass(typeof(FamilyInstance))
            .Cast<FamilyInstance>()
            .Where(i => doc.GetElement(i.OwnerViewId) is ViewSheet)
            .GroupBy(i => i.Symbol.Id)
            .OrderByDescending(g => g.Count())
            .Select(g => doc.GetElement(g.Key) as FamilySymbol)
            .FirstOrDefault(s => s is not null);
        return used ?? new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks).OfClass(typeof(FamilySymbol))
            .Cast<FamilySymbol>().FirstOrDefault();
    }
}
