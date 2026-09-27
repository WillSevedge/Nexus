using AecHub.Contracts;

namespace AecHub.Hub.Core;

/// <summary>One read result plus where it came from.</summary>
public sealed class ResultSource
{
    public required HostInfo Host { get; init; }
    public required string DocumentTitle { get; init; }
    public required ReadResult Result { get; init; }
}

public sealed record ColumnKey(string Group, string Name)
{
    public string Id => Group + " › " + Name;
}

public sealed class TableRow
{
    public string Host { get; init; } = "";
    public string Document { get; init; } = "";
    public string Reader { get; init; } = "";
    public string Path { get; init; } = "";
    public int Depth { get; init; }
    public string ItemType { get; init; } = "";
    public string Item { get; init; } = "";
    public string Key { get; init; } = "";
    public string ItemId { get; init; } = "";
    public Dictionary<string, PropertyValue> Values { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Flattens read results into rows (one per item, children included) and
/// "Group › Property" columns, for display and export.
/// </summary>
public sealed class ResultTable
{
    public List<ColumnKey> Columns { get; } = new();
    public List<TableRow> Rows { get; } = new();

    public static ResultTable Build(IEnumerable<ResultSource> sources, bool includeChildren = true)
    {
        var table = new ResultTable();
        var known = new HashSet<string>(StringComparer.Ordinal);

        foreach (var s in sources)
            foreach (var item in s.Result.Items)
                table.AddItem(s, item, item.Name, 0, includeChildren, known);

        return table;
    }

    private void AddItem(ResultSource s, DataItem item, string path, int depth, bool includeChildren, HashSet<string> known)
    {
        var row = new TableRow
        {
            Host = s.Host.DisplayName,
            Document = s.DocumentTitle,
            Reader = s.Result.ReaderId,
            Path = path,
            Depth = depth,
            ItemType = item.ItemType,
            Item = item.Name,
            Key = item.Key ?? "",
            ItemId = item.Id,
        };

        foreach (var group in item.Groups)
        {
            var seenInGroup = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var prop in group.Properties)
            {
                // Revit allows two parameters with the same name (e.g. a shared and a project one).
                string name = prop.Name;
                int n = seenInGroup.TryGetValue(name, out var c) ? c + 1 : 1;
                seenInGroup[name] = n;
                if (n > 1) name = $"{name} ({n})";

                var key = new ColumnKey(group.Name, name);
                if (known.Add(key.Id)) Columns.Add(key);
                row.Values[key.Id] = prop;
            }
        }
        Rows.Add(row);

        if (!includeChildren) return;
        foreach (var child in item.Children)
            AddItem(s, child, path + " / " + child.Name, depth + 1, true, known);
    }
}
