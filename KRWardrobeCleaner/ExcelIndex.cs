using System.Collections;
using System.Reflection;
using Dalamud.Plugin.Services;

namespace KRWardrobeCleaner;

public sealed class ExcelIndex
{
    private readonly IDataManager data;
    private Dictionary<uint, string>? names;
    private HashSet<uint>? validItems;
    private HashSet<uint>? cabinetItems;

    public ExcelIndex(IDataManager data) => this.data = data;

    public IReadOnlyDictionary<uint, string> Names => names ??= BuildItemNames();
    public IReadOnlySet<uint> ValidItems => validItems ??= Names.Keys.ToHashSet();
    public IReadOnlySet<uint> CabinetItems => cabinetItems ??= BuildCabinetItems();

    public string NameOf(uint id) => Names.TryGetValue(id, out var n) && !string.IsNullOrWhiteSpace(n) ? n : $"Item #{id}";

    private IEnumerable GetSheet(string rowTypeName)
    {
        var rowType = Type.GetType($"Lumina.Excel.Sheets.{rowTypeName}, Lumina.Excel")
            ?? throw new InvalidOperationException($"Lumina sheet row type not found: {rowTypeName}");

        var getSheet = typeof(IDataManager).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == "GetExcelSheet" && m.IsGenericMethodDefinition)
            .First(m => m.GetGenericArguments().Length == 1 && m.GetParameters().Length == 2);

        var closed = getSheet.MakeGenericMethod(rowType);
        var sheet = closed.Invoke(data, [null, null]) as IEnumerable
            ?? throw new InvalidOperationException($"Could not load Excel sheet: {rowTypeName}");
        return sheet;
    }

    private Dictionary<uint, string> BuildItemNames()
    {
        var result = new Dictionary<uint, string>();
        foreach (var row in GetSheet("Item"))
        {
            if (row is null) continue;
            var id = ReadRowId(row);
            if (id == 0) continue;
            var prop = row.GetType().GetProperty("Name");
            var raw = prop?.GetValue(row);
            result[id] = raw?.ToString() ?? string.Empty;
        }
        return result;
    }

    private HashSet<uint> BuildCabinetItems()
    {
        var result = new HashSet<uint>();
        foreach (var row in GetSheet("Cabinet"))
        {
            if (row is null) continue;
            var itemProp = row.GetType().GetProperty("Item");
            var itemRef = itemProp?.GetValue(row);
            if (itemRef is null) continue;
            var id = ReadRowId(itemRef);
            if (id != 0) result.Add(id);
        }
        return result;
    }

    private static uint ReadRowId(object value)
    {
        var t = value.GetType();
        foreach (var name in new[] { "RowId", "RowID", "Row" })
        {
            var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (p is null) continue;
            var v = p.GetValue(value);
            try { return Convert.ToUInt32(v); } catch { }
        }
        return 0;
    }
}
