using Nexus.Agent;
using Nexus.Contracts;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI.Events;

namespace Nexus.Agent.Revit;

/// <summary>
/// Files that are not open in Revit: opened in the background (no window, detached from central when
/// workshared, nothing saved) to read them or make PDFs, then closed. The hub sends the path of its own
/// copy, so the original file is never opened by Revit.
/// </summary>
internal sealed class RevitFiles : IFileOpener<Document>, IPdfExporter<Document>
{
    private readonly RevitDispatcher _dispatcher;

    public RevitFiles(RevitDispatcher dispatcher) => _dispatcher = dispatcher;

    /// <summary>The hub's working copies: never listed as open files.</summary>
    public static bool IsWorkCopy(Document doc)
    {
        string path = doc.PathName ?? "";
        return path.Length > 0 && path.StartsWith(NexusPaths.OfflineWorkDir, StringComparison.OrdinalIgnoreCase);
    }

    public Document Open(string path, AgentLog log, out bool openedHere)
    {
        var uiapp = _dispatcher.Current ?? throw new InvalidOperationException("Files can only be opened from the Revit API thread.");
        var app = uiapp.Application;
        foreach (Document open in app.Documents)
        {
            if (!open.IsLinked && string.Equals(open.PathName, path, StringComparison.OrdinalIgnoreCase))
            {
                openedHere = false;
                return open;
            }
        }

        BasicFileInfo info;
        try { info = BasicFileInfo.Extract(path); }
        catch (Exception ex) { throw new AgentException(ErrorCodes.BadRequest, $"{System.IO.Path.GetFileName(path)} is not a Revit file Revit can read: {ex.Message}"); }
        if (int.TryParse(info.Format, out int fileYear) && int.TryParse(app.VersionNumber, out int revitYear) && fileYear > revitYear)
            throw new AgentException(ErrorCodes.BadRequest, $"{System.IO.Path.GetFileName(path)} is a Revit {fileYear} file; Revit {revitYear} cannot open it.");

        var options = new OpenOptions { Audit = false };
        if (info.IsWorkshared)
        {
            // A copy of a central model: detach so nothing ever talks to the central model.
            options.DetachFromCentralOption = DetachFromCentralOption.DetachAndPreserveWorksets;
            // All worksets, so title blocks and everything on sheets are there.
            options.SetOpenWorksetsConfiguration(new WorksetConfiguration(WorksetConfigurationOption.OpenAllWorksets));
        }

        log.Info($"Opening {path} in the background (Revit {info.Format} file, workshared: {info.IsWorkshared}).");
        var dialogs = new List<string>();
        void OnDialog(object? sender, DialogBoxShowingEventArgs e) => Answer(e, dialogs, log);
        uiapp.DialogBoxShowing += OnDialog;
        try
        {
            var doc = app.OpenDocumentFile(ModelPathUtils.ConvertUserVisiblePathToModelPath(path), options)
                      ?? throw new AgentException(ErrorCodes.InternalError, $"Revit did not open {System.IO.Path.GetFileName(path)}.");
            openedHere = true;
            return doc;
        }
        catch (AgentException) { throw; }
        catch (Exception ex)
        {
            string shown = dialogs.Count == 0 ? "" : $" Revit asked: {string.Join("; ", dialogs)}";
            throw new AgentException(ErrorCodes.InternalError, $"Revit could not open {System.IO.Path.GetFileName(path)}: {ex.Message}{shown}");
        }
        finally
        {
            uiapp.DialogBoxShowing -= OnDialog;
        }
    }

    /// <summary>
    /// Questions Revit asks while opening. Only answers Nexus knows are safe are given (keep opening without
    /// missing links); anything else is left on screen for the person at Revit, because the wrong answer can
    /// cancel the open. Every question is written to the log.
    /// </summary>
    private static void Answer(DialogBoxShowingEventArgs e, List<string> seen, AgentLog log)
    {
        string id = e.DialogId ?? "";
        string message = e switch
        {
            TaskDialogShowingEventArgs td => td.Message ?? "",
            MessageBoxShowingEventArgs mb => mb.Message ?? "",
            _ => "",
        };
        message = message.Replace("\r", " ").Replace("\n", " ").Trim();
        seen.Add(id.Length > 0 ? $"{id} ({Shorten(message)})" : Shorten(message));
        try
        {
            // "Ignore and continue opening the project" on the unresolved references (missing links) dialog.
            if (id == "TaskDialog_Unresolved_References")
            {
                e.OverrideResult(1002);
                log.Info($"While opening: {id}: answered 'Ignore and continue opening'.");
                return;
            }
        }
        catch (Exception ex)
        {
            log.Warn($"Could not answer {id}.", ex);
        }
        log.Info($"While opening, Revit asked (left for the user): {(id.Length > 0 ? id : e.GetType().Name)}: {message}");
    }

    private static string Shorten(string text) => text.Length > 120 ? text.Substring(0, 120) + "…" : text;

    public void Close(Document document, AgentLog log)
    {
        try
        {
            string title = document.Title;
            document.Close(false);
            log.Info($"Closed {title} (not saved).");
        }
        catch (Exception ex)
        {
            log.Warn("Could not close a file opened in the background.", ex);
        }
    }

    public ExportPdfResult Export(Document doc, ExportPdfRequest request, AgentLog log, CancellationToken ct)
    {
        var result = new ExportPdfResult();
        var sheets = new List<ViewSheet>();
        if (request.ItemIds.Count == 0)
        {
            sheets.AddRange(new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                .Where(s => !s.IsPlaceholder && !s.IsTemplate).OrderBy(s => s.SheetNumber, StringComparer.OrdinalIgnoreCase));
        }
        else
        {
            foreach (var id in request.ItemIds)
            {
                if (doc.GetElement(id) is ViewSheet s) sheets.Add(s);
                else result.Sheets.Add(new ExportedSheet { ItemId = id, Error = "Sheet not found in this model (it may have been deleted)." });
            }
        }

        string file = System.IO.Path.GetFileNameWithoutExtension(doc.PathName is { Length: > 0 } p ? p : doc.Title);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sheet in sheets)
        {
            ct.ThrowIfCancellationRequested();
            var entry = new ExportedSheet { ItemId = sheet.UniqueId, Number = sheet.SheetNumber, Name = sheet.Name };
            result.Sheets.Add(entry);
            if (sheet.IsPlaceholder)
            {
                entry.Error = "Placeholder sheet (nothing to print).";
                continue;
            }
            try
            {
                string target = PdfNames.Unique(request.OutputFolder, PdfNames.For(request.FileNamePattern, sheet.SheetNumber, sheet.Name, file), taken);
                if (System.IO.File.Exists(target)) System.IO.File.Delete(target);
                var options = new PDFExportOptions
                {
                    Combine = true,
                    FileName = System.IO.Path.GetFileNameWithoutExtension(target),
                    PaperFormat = ExportPaperFormat.Default, // the title block's size
                    HideCropBoundaries = true,
                    HideReferencePlane = true,
                    HideScopeBoxes = true,
                    HideUnreferencedViewTags = true,
                };
                bool ok = doc.Export(request.OutputFolder, new List<ElementId> { sheet.Id }, options);
                if (ok && System.IO.File.Exists(target)) entry.PdfPath = target;
                else entry.Error = "Revit did not create the PDF.";
            }
            catch (Exception ex)
            {
                entry.Error = ex.Message;
                log.Warn($"PDF of sheet {sheet.SheetNumber} failed.", ex);
            }
        }
        return result;
    }
}
