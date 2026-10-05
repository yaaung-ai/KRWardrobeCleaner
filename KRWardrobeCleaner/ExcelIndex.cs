using System.Collections;
using System.Reflection;
using Dalamud.Plugin.Services;

namespace KRWardrobeCleaner;

public sealed class ExcelIndex
{
    private static readonly HashSet<string> CrafterGathererJobs = new(StringComparer.Ordinal)
    {
        "CRP", "BSM", "ARM", "GSM", "LTW", "WVR", "ALC", "CUL", "MIN", "BTN", "FSH",
    };

    private static readonly HashSet<string> CombatJobs = new(StringComparer.Ordinal)
    {
        "GLA", "PGL", "MRD", "LNC", "ARC", "CNJ", "THM", "PLD", "MNK", "WAR", "DRG", "BRD",
        "WHM", "BLM", "ACN", "SMN", "SCH", "ROG", "NIN", "MCH", "DRK", "AST", "SAM", "RDM",
        "GNB", "DNC", "RPR", "SGE", "VPR", "PCT", "BLU",
    };

    private readonly IDataManager data;
    private Dictionary<uint, string>? names;
    private Dictionary<uint, uint>? cabinetRows;
    private HashSet<uint>? equipmentItems;
    private HashSet<uint>? glamourableItems;
    private HashSet<uint>? crafterGathererOnlyItems;
    private Dictionary<uint, int>? requiredLevels;
    private HashSet<uint>? outfitSetIds;

    public ExcelIndex(IDataManager data) => this.data = data;

    public IReadOnlyDictionary<uint, string> Names => names ??= BuildItemNames();
    public IReadOnlyDictionary<uint, uint> CabinetRows => cabinetRows ??= BuildCabinetRows();
    public IReadOnlySet<uint> CabinetItems => CabinetRows.Keys.ToHashSet();
    public IReadOnlySet<uint> EquipmentItems => equipmentItems ??= BuildEquipmentItems();
    public IReadOnlySet<uint> GlamourableItems => glamourableItems ??= BuildGlamourableItems();
    public IReadOnlySet<uint> CrafterGathererOnlyItems => crafterGathererOnlyItems ??= BuildCrafterGathererOnlyItems();
    public IReadOnlyDictionary<uint, int> RequiredLevels => requiredLevels ??= BuildRequiredLevels();
    public IReadOnlySet<uint> OutfitSetIds => outfitSetIds ??= BuildOutfitSetIds();

    public string NameOf(uint id)
        => Names.TryGetValue(id, out var name) && !string.IsNullOrWhiteSpace(name) ? name : $"아이템 #{id}";

    public bool TryGetCabinetRow(uint itemId, out uint rowId) => CabinetRows.TryGetValue(itemId, out rowId);
    public bool IsEquipment(uint itemId) => EquipmentItems.Contains(itemId);
    public bool IsGlamourable(uint itemId) => GlamourableItems.Contains(itemId);
    public bool IsCrafterGathererOnly(uint itemId) => CrafterGathererOnlyItems.Contains(itemId);
    public int RequiredLevelOf(uint itemId) => RequiredLevels.TryGetValue(itemId, out var level) ? level : 0;
    public bool IsOutfitSetToken(uint itemId) => OutfitSetIds.Contains(itemId);

    private IEnumerable GetSheet(string rowTypeName)
    {
        var rowType = Type.GetType($"Lumina.Excel.Sheets.{rowTypeName}, Lumina.Excel")
            ?? throw new InvalidOperationException($"Lumina 시트 행 형식을 찾지 못했습니다: {rowTypeName}");

        var getSheet = typeof(IDataManager).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == "GetExcelSheet" && m.IsGenericMethodDefinition)
            .First(m => m.GetGenericArguments().Length == 1 && m.GetParameters().Length == 2);

        var closed = getSheet.MakeGenericMethod(rowType);
        return closed.Invoke(data, [null, null]) as IEnumerable
            ?? throw new InvalidOperationException($"Excel 시트를 불러오지 못했습니다: {rowTypeName}");
    }

    private Dictionary<uint, string> BuildItemNames()
    {
        var result = new Dictionary<uint, string>();
        foreach (var row in GetSheet("Item"))
        {
            if (row is null) continue;
            var id = ReadRowId(row);
            if (id == 0) continue;
            result[id] = row.GetType().GetProperty("Name")?.GetValue(row)?.ToString() ?? string.Empty;
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

            var itemRef = row.GetType().GetProperty("Item")?.GetValue(row);
            if (itemRef is null) continue;

            var itemId = ReadRowId(itemRef);
            if (itemId != 0)
                result[itemId] = cabinetRowId;
        }
        return result;
    }

    private HashSet<uint> BuildEquipmentItems()
    {
        var result = new HashSet<uint>();
        foreach (var row in GetSheet("Item"))
        {
            if (row is null) continue;
            var itemId = ReadRowId(row);
            if (itemId == 0) continue;

            var equipRef = row.GetType().GetProperty("EquipSlotCategory")?.GetValue(row);
            if (equipRef is not null && ReadRowId(equipRef) != 0)
                result.Add(itemId);
        }
        return result;
    }

    private HashSet<uint> BuildGlamourableItems()
    {
        var result = new HashSet<uint>();
        foreach (var row in GetSheet("Item"))
        {
            if (row is null) continue;
            var itemId = ReadRowId(row);
            if (itemId == 0) continue;

            var prop = row.GetType().GetProperty("IsGlamorous");
            if (prop?.PropertyType == typeof(bool) && (bool)(prop.GetValue(row) ?? false))
                result.Add(itemId);
        }
        return result;
    }

    private Dictionary<uint, int> BuildRequiredLevels()
    {
        var result = new Dictionary<uint, int>();
        foreach (var row in GetSheet("Item"))
        {
            if (row is null) continue;
            var itemId = ReadRowId(row);
            if (itemId == 0) continue;

            var raw = row.GetType().GetProperty("LevelEquip")?.GetValue(row);
            try { result[itemId] = Convert.ToInt32(raw); }
            catch { result[itemId] = 0; }
        }
        return result;
    }

    private HashSet<uint> BuildCrafterGathererOnlyItems()
    {
        var result = new HashSet<uint>();
        foreach (var row in GetSheet("Item"))
        {
            if (row is null) continue;
            var itemId = ReadRowId(row);
            if (itemId == 0) continue;

            var categoryRef = row.GetType().GetProperty("ClassJobCategory")?.GetValue(row);
            if (categoryRef is null) continue;

            var category = ReadReferencedValue(categoryRef);
            if (category is null) continue;

            var hasCrafterGatherer = false;
            var hasCombat = false;
            foreach (var prop in category.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (prop.PropertyType != typeof(bool) || !prop.CanRead)
                    continue;

                bool enabled;
                try { enabled = (bool)(prop.GetValue(category) ?? false); }
                catch { continue; }
                if (!enabled) continue;

                if (CrafterGathererJobs.Contains(prop.Name))
                    hasCrafterGatherer = true;
                if (CombatJobs.Contains(prop.Name))
                    hasCombat = true;
            }

            if (hasCrafterGatherer && !hasCombat)
                result.Add(itemId);
        }

        return result;
    }

    private HashSet<uint> BuildOutfitSetIds()
    {
        var result = new HashSet<uint>();
        foreach (var row in GetSheet("MirageStoreSetItem"))
        {
            if (row is null) continue;
            var id = ReadRowId(row);
            if (id != 0)
                result.Add(id);
        }
        return result;
    }

    private static object? ReadReferencedValue(object reference)
    {
        var type = reference.GetType();
        foreach (var name in new[] { "Value", "ValueNullable" })
        {
            var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (prop is null) continue;
            try
            {
                var value = prop.GetValue(reference);
                if (value is not null) return value;
            }
            catch { }
        }
        return reference;
    }

    private static uint ReadRowId(object value)
    {
        var type = value.GetType();
        foreach (var name in new[] { "RowId", "RowID", "Row" })
        {
            var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (prop is null) continue;
            try { return Convert.ToUInt32(prop.GetValue(value)); }
            catch { }
        }
        return 0;
    }
}
