using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace KRWardrobeCleaner;

public sealed record ArmoryArmoireCandidate(uint ItemId, string Name, bool IsCraftingGear, uint CabinetRowId);

public sealed unsafe class ArmoryArmoireService
{
    private static readonly InventoryType[] ArmoryContainers =
    [
        InventoryType.ArmoryMainHand,
        InventoryType.ArmoryOffHand,
        InventoryType.ArmoryHead,
        InventoryType.ArmoryBody,
        InventoryType.ArmoryHands,
        InventoryType.ArmoryLegs,
        InventoryType.ArmoryFeets,
        InventoryType.ArmoryEar,
        InventoryType.ArmoryNeck,
        InventoryType.ArmoryWrist,
        InventoryType.ArmoryRings,
        InventoryType.ArmorySoulCrystal,
    ];

    private static readonly InventoryType[] DuplicateCheckContainers =
    [
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
        InventoryType.ArmoryMainHand,
        InventoryType.ArmoryOffHand,
        InventoryType.ArmoryHead,
        InventoryType.ArmoryBody,
        InventoryType.ArmoryHands,
        InventoryType.ArmoryLegs,
        InventoryType.ArmoryFeets,
        InventoryType.ArmoryEar,
        InventoryType.ArmoryNeck,
        InventoryType.ArmoryWrist,
        InventoryType.ArmoryRings,
        InventoryType.ArmorySoulCrystal,
        InventoryType.EquippedItems,
    ];

    private readonly ExcelIndex excel;
    private readonly Configuration config;
    private readonly Action saveConfig;
    private readonly List<ArmoryArmoireCandidate> candidates = [];

    private int index;
    private long nextActionAt;

    public IReadOnlyList<ArmoryArmoireCandidate> Candidates => candidates;
    public bool IsRunning { get; private set; }
    public int Total => candidates.Count;
    public int Processed => index;
    public int Stored { get; private set; }
    public int Skipped { get; private set; }
    public int Failed { get; private set; }
    public int SkippedCrafting { get; private set; }
    public int SkippedGearset { get; private set; }
    public int SkippedModified { get; private set; }
    public int SkippedDuplicate { get; private set; }
    public string Status { get; private set; } = "추억의 보관함을 열고 장비칸 후보를 검색해 주세요.";
    public string? LastItemStatus { get; private set; }

    public ArmoryArmoireService(ExcelIndex excel, Configuration config, Action saveConfig)
    {
        this.excel = excel;
        this.config = config;
        this.saveConfig = saveConfig;
    }

    public bool IsCabinetReady()
    {
        var ui = UIState.Instance();
        return ui != null && ui->Cabinet.IsCabinetLoaded();
    }

    public int Scan()
    {
        candidates.Clear();
        index = 0;
        Stored = 0;
        Skipped = 0;
        Failed = 0;
        SkippedCrafting = 0;
        SkippedGearset = 0;
        SkippedModified = 0;
        SkippedDuplicate = 0;
        LastItemStatus = null;

        if (!IsCabinetReady())
        {
            Status = "추억의 보관함을 연 상태에서 장비칸 후보를 검색해 주세요.";
            return 0;
        }

        var inventory = InventoryManager.Instance();
        var ui = UIState.Instance();
        if (inventory == null || ui == null)
        {
            Status = "인벤토리 또는 추억의 보관함 데이터를 불러올 수 없습니다.";
            return 0;
        }

        var gearsetItems = BuildGearsetItemSet();
        var seen = new HashSet<uint>();

        foreach (var containerType in ArmoryContainers)
        {
            var container = inventory->GetInventoryContainer(containerType);
            if (container == null || !container->IsLoaded)
                continue;

            for (var slotIndex = 0; slotIndex < container->Size; slotIndex++)
            {
                var slot = container->GetInventorySlot(slotIndex);
                if (slot == null || slot->ItemId == 0)
                    continue;

                var itemId = slot->GetBaseItemId();
                if (itemId == 0 || !seen.Add(itemId))
                    continue;

                if (!excel.TryGetCabinetRow(itemId, out var cabinetRowId))
                    continue;

                if (ui->Cabinet.IsItemInCabinet(cabinetRowId))
                    continue;

                var isCrafting = excel.IsCraftingOnly(itemId);
                if (isCrafting && !config.IncludeCraftingGearInArmoryPreclean)
                {
                    SkippedCrafting++;
                    continue;
                }

                if (gearsetItems.Contains(itemId))
                {
                    SkippedGearset++;
                    continue;
                }

                if (CountPhysicalCopies(inventory, itemId) != 1)
                {
                    SkippedDuplicate++;
                    continue;
                }

                // 추억의 보관함은 외형/염색/마테리아 등의 개별 상태를 보존하지 않으므로
                // 자동 정리에서는 수정된 장비를 건드리지 않는다.
                if (slot->GetConditionPercentage() != 100 ||
                    slot->SpiritbondOrCollectability != 0 ||
                    slot->GlamourId != 0 ||
                    slot->Stains[0] != 0 ||
                    slot->Stains[1] != 0 ||
                    HasMateria(slot) ||
                    (slot->Flags & InventoryItem.ItemFlags.CompanyCrestApplied) != 0)
                {
                    SkippedModified++;
                    continue;
                }

                candidates.Add(new(itemId, excel.NameOf(itemId), isCrafting, cabinetRowId));
            }
        }

        candidates.Sort((a, b) => StringComparer.CurrentCulture.Compare(a.Name, b.Name));
        Status = $"장비칸 검색 완료: 안전 보관 후보 {candidates.Count}개.";
        return candidates.Count;
    }

    public bool Start()
    {
        if (IsRunning)
            return false;

        Scan();
        if (candidates.Count == 0)
            return false;

        if (!IsCabinetReady())
            return false;

        IsRunning = true;
        index = 0;
        Stored = 0;
        Skipped = 0;
        Failed = 0;
        nextActionAt = 0;
        Status = $"장비칸 → 추억의 보관함 정리 시작: {candidates.Count}개";
        return true;
    }

    public void Stop(string reason = "장비칸 정리를 중지했습니다.")
    {
        if (!IsRunning)
            return;

        IsRunning = false;
        Status = reason;
    }

    public void Tick()
    {
        if (!IsRunning)
            return;

        var now = Environment.TickCount64;
        if (now < nextActionAt)
            return;

        if (!IsCabinetReady())
        {
            Stop("추억의 보관함이 닫혀 장비칸 정리를 중지했습니다.");
            return;
        }

        if (index >= candidates.Count)
        {
            IsRunning = false;
            Status = $"장비칸 정리 완료: 보관 {Stored}개 · 제외 {Skipped}개 · 실패 {Failed}개";
            return;
        }

        var candidate = candidates[index];
        var ui = UIState.Instance();
        var inventory = InventoryManager.Instance();
        if (ui == null || inventory == null)
        {
            Stop("게임 데이터를 불러올 수 없어 장비칸 정리를 중지했습니다.");
            return;
        }

        if (ui->Cabinet.IsItemInCabinet(candidate.CabinetRowId))
        {
            Skipped++;
            LastItemStatus = $"{candidate.Name}: 이미 추억의 보관함에 있어 건너뜁니다.";
            index++;
            nextActionAt = now + Math.Clamp(config.ArmoryStoreIntervalMs, 300, 3000);
            return;
        }

        // 처리 직전에도 다시 안전 조건을 검사한다.
        var live = FindUniqueArmoryItem(inventory, candidate.ItemId);
        if (live == null || CountPhysicalCopies(inventory, candidate.ItemId) != 1)
        {
            Skipped++;
            LastItemStatus = $"{candidate.Name}: 장비 위치가 바뀌었거나 중복이 생겨 건너뜁니다.";
            index++;
            nextActionAt = now + Math.Clamp(config.ArmoryStoreIntervalMs, 300, 3000);
            return;
        }

        if (live->GetConditionPercentage() != 100 ||
            live->SpiritbondOrCollectability != 0 ||
            live->GlamourId != 0 ||
            live->Stains[0] != 0 ||
            live->Stains[1] != 0 ||
            HasMateria(live) ||
            (live->Flags & InventoryItem.ItemFlags.CompanyCrestApplied) != 0)
        {
            Skipped++;
            LastItemStatus = $"{candidate.Name}: 장비 상태가 변경되어 건너뜁니다.";
            index++;
            nextActionAt = now + Math.Clamp(config.ArmoryStoreIntervalMs, 300, 3000);
            return;
        }

        var ok = ui->Cabinet.StoreCabinetItem(candidate.CabinetRowId);
        if (ok)
        {
            Stored++;
            LastItemStatus = $"{candidate.Name}: 추억의 보관함에 보관 요청 완료.";
        }
        else
        {
            Failed++;
            LastItemStatus = $"{candidate.Name}: 게임이 보관 요청을 받지 않았습니다.";
        }

        index++;
        Status = $"장비칸 정리 중 {index}/{candidates.Count} · 보관 {Stored} · 제외 {Skipped} · 실패 {Failed}";
        nextActionAt = now + Math.Clamp(config.ArmoryStoreIntervalMs, 300, 3000);
    }

    public void UpdateSettings(bool includeCrafting, int intervalMs)
    {
        config.IncludeCraftingGearInArmoryPreclean = includeCrafting;
        config.ArmoryStoreIntervalMs = Math.Clamp(intervalMs, 300, 3000);
        saveConfig();
    }

    private static bool HasMateria(InventoryItem* item)
    {
        for (var i = 0; i < item->Materia.Length; i++)
            if (item->Materia[i] != 0)
                return true;
        return false;
    }

    private static int CountPhysicalCopies(InventoryManager* inventory, uint itemId)
    {
        var count = 0;
        foreach (var containerType in DuplicateCheckContainers)
        {
            var container = inventory->GetInventoryContainer(containerType);
            if (container == null || !container->IsLoaded)
                continue;

            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot != null && slot->ItemId != 0 && slot->GetBaseItemId() == itemId)
                    count++;
            }
        }
        return count;
    }

    private static InventoryItem* FindUniqueArmoryItem(InventoryManager* inventory, uint itemId)
    {
        InventoryItem* found = null;
        foreach (var containerType in ArmoryContainers)
        {
            var container = inventory->GetInventoryContainer(containerType);
            if (container == null || !container->IsLoaded)
                continue;

            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot == null || slot->ItemId == 0 || slot->GetBaseItemId() != itemId)
                    continue;

                if (found != null)
                    return null;

                found = slot;
            }
        }
        return found;
    }

    private static HashSet<uint> BuildGearsetItemSet()
    {
        var result = new HashSet<uint>();
        var module = RaptureGearsetModule.Instance();
        if (module == null)
            return result;

        for (byte i = 0; i < 100; i++)
        {
            if (!module->IsValidGearset(i))
                continue;

            var gearset = module->GetGearset(i);
            if (gearset == null || !gearset->Flags.HasFlag(RaptureGearsetModule.GearsetFlag.Exists))
                continue;

            foreach (var item in gearset->Items.ToArray())
            {
                var id = item.ItemId % 1_000_000u;
                if (id != 0)
                    result.Add(id);
            }
        }

        return result;
    }
}
