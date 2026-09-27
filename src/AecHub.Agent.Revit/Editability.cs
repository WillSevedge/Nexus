using Autodesk.Revit.DB;

namespace AecHub.Agent.Revit;

internal static class Editability
{
    /// <summary>
    /// Returns why the element cannot be edited right now, or null if it can.
    /// Covers read-only/linked documents and worksharing ownership.
    /// </summary>
    public static string? Blocker(Document doc, Element element)
    {
        if (doc.IsLinked) return "Linked document (read-only)";
        if (doc.IsReadOnly) return "Document is read-only";
        if (!doc.IsWorkshared) return null;

        try
        {
            var status = WorksharingUtils.GetCheckoutStatus(doc, element.Id, out string owner);
            if (status == CheckoutStatus.OwnedByOtherUser)
                return $"Borrowed by {owner}";

            var updates = WorksharingUtils.GetModelUpdatesStatus(doc, element.Id);
            if (updates == ModelUpdatesStatus.UpdatedInCentral)
                return "Changed in central: Reload Latest before editing";
            if (updates == ModelUpdatesStatus.DeletedInCentral)
                return "Deleted in central";

            var workset = doc.GetWorksetTable().GetWorkset(element.WorksetId);
            if (workset is not null && !workset.IsEditable && !string.IsNullOrEmpty(workset.Owner)
                && !string.Equals(workset.Owner, doc.Application.Username, StringComparison.OrdinalIgnoreCase))
                return $"Workset '{workset.Name}' is owned by {workset.Owner}";
        }
        catch (Exception ex)
        {
            return "Could not check worksharing status: " + ex.Message;
        }
        return null;
    }

    public static string WorksetName(Document doc, Element element)
    {
        if (!doc.IsWorkshared) return "";
        try { return doc.GetWorksetTable().GetWorkset(element.WorksetId)?.Name ?? ""; }
        catch { return ""; }
    }
}
