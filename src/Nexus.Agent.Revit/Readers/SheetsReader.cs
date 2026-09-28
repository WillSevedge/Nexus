using System.Globalization;
using Nexus.Agent;
using Nexus.Contracts;
using Autodesk.Revit.DB;

namespace Nexus.Agent.Revit.Readers;

/// <summary>
/// Every ViewSheet: all sheet parameters (built-in, project, shared), title block
/// instance and type parameters, and the current revision.
/// </summary>
internal sealed class SheetsReader : IHostDataReader<Document>
{
    public ReaderDescriptor Descriptor { get; } = new()
    {
        Id = "revit.sheets",
        DisplayName = "Sheets",
        Domain = "Sheets",
        Description = "All sheets with every sheet parameter, title block instance/type parameters and current revision.",
        Options =
        {
            ReaderOption.Bool("titleBlockInstance", "Title block instance parameters", true),
            ReaderOption.Bool("titleBlockType", "Title block type parameters", true),
            ReaderOption.Bool("revisions", "Current revision details", true),
            ReaderOption.Bool("hiddenParameters", "Include parameters not shown in Properties", true),
            ReaderOption.Bool("placeholders", "Include placeholder sheets", true),
        },
    };

    public void Read(Document doc, ReadContext ctx)
    {
        bool tbInstance = ctx.GetBool("titleBlockInstance");
        bool tbType = ctx.GetBool("titleBlockType");
        bool revisions = ctx.GetBool("revisions");
        bool hidden = ctx.GetBool("hiddenParameters");
        bool placeholders = ctx.GetBool("placeholders");

        var sheets = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewSheet))
            .Cast<ViewSheet>()
            .Where(s => placeholders || !s.IsPlaceholder)
            .OrderBy(s => s.SheetNumber, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var sheet in sheets)
        {
            ctx.Cancellation.ThrowIfCancellationRequested();
            try
            {
                ctx.Items.Add(ReadSheet(doc, sheet, ctx, tbInstance, tbType, revisions, hidden));
            }
            catch (Exception ex)
            {
                ctx.Warn($"Sheet {SafeNumber(sheet)}: {ex.Message}");
                ctx.Log.Warn($"Sheet {SafeNumber(sheet)} failed.", ex);
            }
        }
    }

    private static DataItem ReadSheet(Document doc, ViewSheet sheet, ReadContext ctx,
        bool tbInstance, bool tbType, bool revisions, bool hidden)
    {
        string? blocker = Editability.Blocker(doc, sheet);
        var item = new DataItem
        {
            Id = sheet.UniqueId,
            ItemType = sheet.IsPlaceholder ? "Placeholder Sheet" : "Sheet",
            Name = $"{sheet.SheetNumber} - {sheet.Name}",
            Key = sheet.SheetNumber,
        };

        var element = ElementInfo.Group(doc, sheet, "Element", blocker);
        element.Properties.Add(ElementInfo.Derived("Placeholder", sheet.IsPlaceholder ? "Yes" : "No"));
        item.Groups.Add(element);

        item.Groups.AddRange(ParameterReader.ReadGroups(doc, sheet, "Sheet · ", blocker, hidden));

        if (revisions)
            item.Groups.Add(ReadRevisions(doc, sheet));

        if (tbInstance || tbType)
            ReadTitleBlocks(doc, sheet, item, tbInstance, tbType, hidden, ctx);

        return item;
    }

    private static void ReadTitleBlocks(Document doc, ViewSheet sheet, DataItem item,
        bool instance, bool type, bool hidden, ReadContext ctx)
    {
        var titleBlocks = new FilteredElementCollector(doc, sheet.Id)
            .OfCategory(BuiltInCategory.OST_TitleBlocks)
            .WhereElementIsNotElementType()
            .ToElements();

        var summary = new PropertyGroup("Title Block");
        summary.Properties.Add(ElementInfo.Derived("Title Block Count", titleBlocks.Count.ToString(CultureInfo.InvariantCulture)));
        item.Groups.Add(summary);

        if (titleBlocks.Count > 1)
            ctx.Warn($"Sheet {sheet.SheetNumber} has {titleBlocks.Count} title blocks; each is listed separately.");

        for (int i = 0; i < titleBlocks.Count; i++)
        {
            var tb = titleBlocks[i];
            string label = titleBlocks.Count == 1 ? "Title Block" : $"Title Block {i + 1}";
            var tbType = doc.GetElement(tb.GetTypeId()) as ElementType;

            summary.Properties.Add(ElementInfo.Derived($"{label} Family", tbType?.FamilyName ?? ""));
            summary.Properties.Add(ElementInfo.Derived($"{label} Type", tbType?.Name ?? ""));

            if (instance)
                item.Groups.AddRange(ParameterReader.ReadGroups(doc, tb, $"{label} (Instance) · ",
                    Editability.Blocker(doc, tb), hidden));

            if (type && tbType is not null)
                item.Groups.AddRange(ParameterReader.ReadGroups(doc, tbType, $"{label} (Type) · ",
                    Editability.Blocker(doc, tbType), hidden));
        }
    }

    private static PropertyGroup ReadRevisions(Document doc, ViewSheet sheet)
    {
        var g = new PropertyGroup("Current Revision");
        var currentId = sheet.GetCurrentRevision();
        var current = currentId == ElementId.InvalidElementId ? null : doc.GetElement(currentId) as Revision;

        if (current is null)
        {
            g.Properties.Add(ElementInfo.Derived("Revision Number", ""));
            g.Properties.Add(ElementInfo.Derived("Revision Date", ""));
            g.Properties.Add(ElementInfo.Derived("Revision Description", ""));
        }
        else
        {
            string onSheet;
            try { onSheet = sheet.GetRevisionNumberOnSheet(currentId); }
            catch { onSheet = current.RevisionNumber; }

            g.Properties.Add(ElementInfo.Derived("Revision Number", onSheet));
            g.Properties.Add(ElementInfo.Derived("Revision Date", current.RevisionDate));
            g.Properties.Add(ElementInfo.Derived("Revision Description", current.Description));
            g.Properties.Add(ElementInfo.Derived("Revision Sequence", current.SequenceNumber.ToString(CultureInfo.InvariantCulture)));
            g.Properties.Add(ElementInfo.Derived("Revision Issued", current.Issued ? "Yes" : "No"));
            g.Properties.Add(ElementInfo.Derived("Revision Issued By", current.IssuedBy));
            g.Properties.Add(ElementInfo.Derived("Revision Issued To", current.IssuedTo));
        }

        var all = sheet.GetAllRevisionIds();
        g.Properties.Add(ElementInfo.Derived("Revisions On Sheet", all.Count.ToString(CultureInfo.InvariantCulture)));
        var lines = new List<string>();
        foreach (var id in all)
        {
            if (doc.GetElement(id) is not Revision r) continue;
            string num;
            try { num = sheet.GetRevisionNumberOnSheet(id); } catch { num = r.RevisionNumber; }
            lines.Add($"{num} | {r.RevisionDate} | {r.Description}");
        }
        g.Properties.Add(ElementInfo.Derived("Revision History", string.Join("; ", lines)));
        return g;
    }

    private static string SafeNumber(ViewSheet s)
    {
        try { return s.SheetNumber; } catch { return s.Id.Value.ToString(CultureInfo.InvariantCulture); }
    }
}
