using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Nexus.Fab;
using StorageSchema = Autodesk.Revit.DB.ExtensibleStorage.Schema;

namespace Nexus.Agent.Revit.Fab;

/// <summary>
/// Where the fabrication data lives in the model: three instance parameters on every piece (Nexus Spool, Nexus
/// Package, Nexus Status), so they tag, schedule and filter like any Revit parameter and travel with the model; and
/// the project's rules, steps, packages and history as one stored value. Nothing is kept outside the model.
/// </summary>
internal static class FabStore
{
    public const string Spool = "Nexus Spool";
    public const string Package = "Nexus Package";
    public const string Status = "Nexus Status";

    // Fixed ids, so the parameters are the same ones in every model (and in schedules and tags made for them).
    public static readonly Guid SpoolId = new("6f0c2d5e-3a41-4b8e-9c57-0d1e2a3b4c51");
    public static readonly Guid PackageId = new("6f0c2d5e-3a41-4b8e-9c57-0d1e2a3b4c52");
    public static readonly Guid StatusId = new("6f0c2d5e-3a41-4b8e-9c57-0d1e2a3b4c53");
    private static readonly Guid SchemaId = new("6f0c2d5e-3a41-4b8e-9c57-0d1e2a3b4c5f");

    /// <summary>The kinds of element that are fabricated: Fabrication parts, and Revit's own pipe, duct and containment.</summary>
    public static readonly BuiltInCategory[] Categories =
    {
        BuiltInCategory.OST_FabricationPipework, BuiltInCategory.OST_FabricationDuctwork, BuiltInCategory.OST_FabricationContainment,
        BuiltInCategory.OST_FabricationHangers,
        BuiltInCategory.OST_PipeCurves, BuiltInCategory.OST_PipeFitting, BuiltInCategory.OST_PipeAccessory, BuiltInCategory.OST_FlexPipeCurves,
        BuiltInCategory.OST_DuctCurves, BuiltInCategory.OST_DuctFitting, BuiltInCategory.OST_DuctAccessory, BuiltInCategory.OST_FlexDuctCurves,
        BuiltInCategory.OST_Conduit, BuiltInCategory.OST_ConduitFitting, BuiltInCategory.OST_CableTray, BuiltInCategory.OST_CableTrayFitting,
    };

    /// <summary>The parameters also go on these, so spool assemblies and placed hangers and sleeves carry them.</summary>
    private static readonly BuiltInCategory[] AlsoBound = { BuiltInCategory.OST_Assemblies, BuiltInCategory.OST_GenericModel };

    public static ElementMulticategoryFilter Filter() => new(Categories);

    // ------------------------------------------------------------------ parameters

    public static string Get(Element e, Guid id) => e.get_Parameter(id)?.AsString() ?? "";

    /// <summary>Sets a value; false when the element does not have the parameter or it cannot be set (a locked workset).</summary>
    public static bool Set(Element e, Guid id, string value)
    {
        var p = e.get_Parameter(id);
        if (p is null || p.IsReadOnly) return false;
        if ((p.AsString() ?? "") == value) return true;
        return p.Set(value);
    }

    /// <summary>
    /// Adds the three parameters to the model when they are not there (call inside a transaction). They are shared
    /// parameters with fixed ids, made through a throw-away shared parameter file: the user's own file is put back.
    /// </summary>
    public static void EnsureParameters(Document doc) => EnsureTextParameters(doc, new (string Name, Guid Id, string Tip)[]
    {
        (Spool, SpoolId, "The spool this piece is fabricated in (set by Nexus)."),
        (Package, PackageId, "The package the spool is ordered, made and shipped in (set by Nexus)."),
        (Status, StatusId, "Where the spool is: Design, Released, Cut, Shipped, Installed… (set by Nexus)."),
    });

    /// <summary>
    /// The id of a text parameter Nexus adds for the parameter mapping: made from its name, so the same name is the
    /// same shared parameter in every model.
    /// </summary>
    public static Guid IdFor(string parameterName)
    {
        using var md5 = System.Security.Cryptography.MD5.Create();
        return new Guid(md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes("Nexus parameter:" + parameterName.Trim().ToUpperInvariant())));
    }

    /// <summary>
    /// Adds text instance parameters to the fabricated categories when the model does not have them (call inside a
    /// transaction). Returns the names added.
    /// </summary>
    public static List<string> EnsureTextParameters(Document doc, IReadOnlyList<(string Name, Guid Id, string Tip)> wanted)
    {
        var added = new List<string>();
        if (wanted.All(w => SharedParameterElement.Lookup(doc, w.Id) is not null)) return added;

        var app = doc.Application;
        string previous = app.SharedParametersFilename;
        string temp = Path.Combine(Path.GetTempPath(), $"nexus-shared-{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllText(temp, "");
            app.SharedParametersFilename = temp;
            var file = app.OpenSharedParameterFile() ?? throw new InvalidOperationException("Revit could not open a shared parameter file.");
            var group = file.Groups.Create("Nexus");
            var categories = app.Create.NewCategorySet();
            foreach (var bic in Categories.Concat(AlsoBound))
            {
                Category? category = null;
                try { category = Category.GetCategory(doc, bic); } catch { /* not in this Revit */ }
                if (category is not null && category.AllowsBoundParameters) categories.Insert(category);
            }
            foreach (var (name, id, tip) in wanted)
            {
                if (SharedParameterElement.Lookup(doc, id) is not null) continue;
                var definition = group.Definitions.Create(new ExternalDefinitionCreationOptions(name, SpecTypeId.String.Text)
                {
                    GUID = id, Description = tip, UserModifiable = true,
                });
                var binding = app.Create.NewInstanceBinding(categories);
                if (!doc.ParameterBindings.Insert(definition, binding, GroupTypeId.General))
                    doc.ParameterBindings.ReInsert(definition, binding, GroupTypeId.General);
                added.Add(name);
            }
        }
        finally
        {
            try { app.SharedParametersFilename = previous ?? ""; } catch { /* Revit keeps the temporary path */ }
            try { File.Delete(temp); } catch { /* left in the temp folder */ }
        }
        return added;
    }

    // ------------------------------------------------------------------ the project (rules, steps, packages, history)

    private static StorageSchema GetSchema()
    {
        var schema = StorageSchema.Lookup(SchemaId);
        if (schema is not null) return schema;
        var builder = new SchemaBuilder(SchemaId);
        builder.SetSchemaName("NexusFabrication");
        builder.SetDocumentation("Nexus fabrication: spooling rules, tracking steps, packages and history.");
        builder.SetReadAccessLevel(AccessLevel.Public);
        builder.SetWriteAccessLevel(AccessLevel.Public);
        builder.AddSimpleField("Json", typeof(string));
        return builder.Finish();
    }

    private static DataStorage? Storage(Document doc, StorageSchema schema) =>
        new FilteredElementCollector(doc).OfClass(typeof(DataStorage)).Cast<DataStorage>().FirstOrDefault(s => s.GetEntity(schema).IsValid());

    public static FabProject Load(Document doc)
    {
        try
        {
            var schema = GetSchema();
            var storage = Storage(doc, schema);
            return storage is null ? new FabProject() : FabProject.FromJson(storage.GetEntity(schema).Get<string>("Json"));
        }
        catch
        {
            return new FabProject();
        }
    }

    /// <summary>Saves the project into the model (call inside a transaction).</summary>
    public static void Save(Document doc, FabProject project)
    {
        var schema = GetSchema();
        var storage = Storage(doc, schema) ?? DataStorage.Create(doc);
        var entity = new Entity(schema);
        entity.Set("Json", project.ToJson());
        storage.SetEntity(entity);
    }
}
