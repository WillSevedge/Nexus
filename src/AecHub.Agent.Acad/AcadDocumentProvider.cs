using System.Security.Cryptography;
using System.Text;
using AecHub.Agent;
using AecHub.Contracts;
using Autodesk.AutoCAD.ApplicationServices;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AecHub.Agent.Acad;

/// <summary>Lists and resolves open drawings. Main thread only.</summary>
public sealed class AcadDocumentProvider : IDocumentProvider<Document>
{
    public IReadOnlyList<DocumentInfo> ListDocuments()
    {
        var active = AcApp.DocumentManager.MdiActiveDocument;
        var list = new List<DocumentInfo>();
        foreach (Document doc in AcApp.DocumentManager)
        {
            var info = Describe(doc);
            info.IsActive = ReferenceEquals(doc, active) || (active is not null && Id(active) == info.Id);
            list.Add(info);
        }
        return list.OrderByDescending(d => d.IsActive).ThenBy(d => d.Title, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public Document? Find(string documentId)
    {
        if (string.Equals(documentId, "active", StringComparison.OrdinalIgnoreCase))
            return AcApp.DocumentManager.MdiActiveDocument;
        foreach (Document doc in AcApp.DocumentManager)
            if (Id(doc) == documentId) return doc;
        return null;
    }

    public DocumentInfo Describe(Document doc)
    {
        string name = doc.Name ?? "";
        var info = new DocumentInfo
        {
            Id = Id(doc),
            Title = Path.GetFileName(name),
            Path = Path.IsPathRooted(name) ? name : null,
            IsReadOnly = doc.IsReadOnly,
        };
        try
        {
            if (ReferenceEquals(doc, AcApp.DocumentManager.MdiActiveDocument))
                info.IsModified = Convert.ToInt32(AcApp.GetSystemVariable("DBMOD")) != 0;
        }
        catch { /* ignored */ }
        try
        {
            if (!string.IsNullOrEmpty(doc.CommandInProgress))
                info.Extra["CommandInProgress"] = doc.CommandInProgress;
        }
        catch { /* ignored */ }
        return info;
    }

    /// <summary>Stable for the session: hash of the document name and database fingerprint.</summary>
    public static string Id(Document doc)
    {
        string fingerprint;
        try { fingerprint = doc.Database.FingerprintGuid; } catch { fingerprint = ""; }
        byte[] hash = SHA1.HashData(Encoding.UTF8.GetBytes($"{doc.Name}|{fingerprint}".ToLowerInvariant()));
        return "dwg-" + Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
    }

    /// <summary>
    /// Lock a document for reading from the application context. Fails with HostBusy
    /// if the document is busy (for example a command is running in it).
    /// </summary>
    public static DocumentLock LockForRead(Document doc)
    {
        try
        {
            return doc.LockDocument();
        }
        catch (Exception ex)
        {
            throw new AgentException(ErrorCodes.HostBusy,
                $"'{Path.GetFileName(doc.Name)}' is busy (is a command running?). Try again when AutoCAD is idle.", ex);
        }
    }
}
