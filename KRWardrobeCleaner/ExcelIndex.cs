using System.Collections;
using System.Reflection;
using Dalamud.Plugin.Services;

namespace KRWardrobeCleaner;

public sealed class ExcelIndex
{
    private static readonly HashSet<string> CrafterJobs =
        new(StringComparer.Ordinal) { "CRP", "BSM", "ARM", "GSM", "LTW", "WVR", "ALC", "CUL" };

    private readonly IDataManager data;
    private Dictionary<uint, string>? names;
    private HashSet<uint>? validItems;
    private Dictionary<uint, uint>? cabinetRows;
    private HashSet<uint>? craftingOnlyItems;

    public ExcelIndex(IDataManager data) => this.data = data;

    public IReadOnlyDictionary<uint, string> Names => names ??= BuildItemNames();
    public IReadOnlySet<uint> ValidItems => validItems ??= Names.Keys.ToHashSet();
    public IReadOnlyDictionary<uint, uint> CabinetRows => cabinetRows ??= BuildCabinetRows();
    public IReadOnlySet<uint> CabinetItems => CabinetRows.Keys.ToHashSet();
    public IReadOnlySet<uint> CraftingOnlyItems => craftingOnlyItems ??= BuildCraftingOnlyItems();

    public string NameOf(uint id) => Names.TryGetValue(id, out var n) && !string.IsNullOrWhiteSpace(n) ? n : $"아이템 #{id}";
    public bool TryGetCabinetRow(uint itemId, out uint rowId) => CabinetRows.TryGetValue(itemId, out rowId);
    public bool IsCraftingOnly(uint itemId) => CraftingOnlyItems.Contains(itemId);

    private IEnumerable GetSheet(string rowTypeName)
    {
        var rowType = Type.GetType($"Lumina.Excel.Sheets.{rowTypeName}, Lumina.Excel")
            ?? throw new InvalidOperationException($"Lumina 시트 행 형식을 찾지 못했습니다: {rowTypeName}");

        var getSheet = typeof(IDataManager).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == "GetExcelSheet" && m.IsGenericMethodDefinition)
            .First(m => m.GetGenericArguments().Length == 1 && m.GetParameters().Length == 2);

        var closed = getSheet.MakeGenericMethod(rowType);
        var sheet = closed.Invoke(data, [null, null]) as IEnumerable
            ?? throw new InvalidOperationException($"Excel 시트를 불러오지 못했습니다: {rowTypeName}");
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

    private Dictionary<uint, uint> BuildCabinetRows()
    {
        var result = new Dictionary<uint, uint>();
        foreach (var row in GetSheet("Cabinet"))
        {
            if (row is null) continue;
            var cabinetRowId = ReadRowId(row);
            if (cabinetRowId == 0) continue;

            var itemProp = row.GetType().GetProperty("Item");
            var itemRef = itemProp?.GetValue(row);
            if (itemRef is null) continue;

            var itemId = ReadRowId(itemRef);
            if (itemId != 0)
                result[itemId] = cabinetRowId;
        }
        return result;
    }

    private HashSet<uint> BuildCraftingOnlyItems()
    {
        var result = new HashSet<uint>();

        foreach (var row in GetSheet("Item"))
        {
            if (row is null) continue;
            var itemId = ReadRowId(row);
            if (itemId == 0) continue;

            var categoryProp = row.GetType().GetProperty("ClassJobCategory");
            var categoryRef = categoryProp?.GetValue(row);
            if (categoryRef is null) continue;

            var category = ReadReferencedValue(categoryRef);
            if (category is null) continue;

            var boolProps = category.GetType()
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.PropertyType == typeof(bool) && p.CanRead)
                .ToArray();

            var hasCrafter = false;
            var hasNonCrafter = false;

            foreach (var p in boolProps)
            {
                bool enabled;
                try { enabled = (bool)(p.GetValue(category) ?? false); }
                catch { continue; }

                if (!enabled) continue;

                if (CrafterJobs.Contains(p.Name))
                    hasCrafter = true;
                else
                    hasNonCrafter = true;
            }

            // "제작직 장비"는 제작직 사용 가능이면서 전투/채집 등 다른 직군에는
            // 허용되지 않는 장비로 정의한다. 전 직업 이벤트 의상은 이 분류에 들어가지 않는다.
            if (hasCrafter && !hasNonCrafter)
                result.Add(itemId);
        }

        return result;
    }

    private static object? ReadReferencedValue(object reference)
    {
        var t = reference.GetType();
        foreach (var name in new[] { "Value", "ValueNullable" })
        {
            var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (p is null) continue;
            try
            {
                var value = p.GetValue(reference);
                if (value is not null) return value;
            }
            catch { }
        }
        return reference;
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
