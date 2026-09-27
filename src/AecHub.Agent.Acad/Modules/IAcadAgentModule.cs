using AecHub.Agent;
using AecHub.Agent.Acad.PropertyEngine;
using Autodesk.AutoCAD.ApplicationServices;

namespace AecHub.Agent.Acad.Modules;

/// <summary>
/// An optional, vertical-specific module (Civil 3D, Map 3D, ...). It lives in its
/// own assembly so the core agent never references the vertical's API. The core
/// loads it only when modules.json's detection rules match the running product.
/// </summary>
public interface IAcadAgentModule
{
    string Name { get; }

    /// <summary>Called once on AutoCAD's main thread after the product finished starting.</summary>
    void Initialize(AcadModuleContext context);
}

public sealed class AcadModuleContext
{
    internal AcadModuleContext(AgentLog log, ReaderRegistry<Document> readers, ObjectPropertyReader objects)
    {
        Log = log;
        Readers = readers;
        Objects = objects;
    }

    public AgentLog Log { get; }
    public ReaderRegistry<Document> Readers { get; }
    public ObjectPropertyReader Objects { get; }
}
