using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace KRWardrobeCleaner;

public readonly record struct ItemLocationKey(InventoryType Container, int Slot)
{
    public string Id => $"{(int)Container}:{Slot}";
}

public sealed record PhysicalGearEntry(
    ItemLocationKey Key,
    uint ItemId,
    string Name,
    int RequiredLevel,
    bool IsCrafterGatherer,
    bool IsDyed,
    bool IsGlamoured,
    bool HasMateria);

public sealed record DresserArmoireEntry(
    int DresserSlot,
    uint ItemId,
    string Name,
    bool IsDyed,
    bool UsedOnPlate);

public sealed record DiscardEntry(
    ItemLocationKey Key,
    uint ItemId,
    string Name,
    int Quantity,
    bool IsEquipment,
    int RequiredLevel,
    bool IsCrafterGatherer,
    bool IsDyed);

public sealed unsafe class InventoryScanner
{
    public static readonly InventoryType[] ArmoryContainers =
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

    public static readonly InventoryType[] BagContainers =
    [
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
    ];

    private readonly ExcelIndex excel;

    public InventoryScanner(ExcelIndex excel) => this.excel = excel;

    public List<PhysicalGearEntry> Scan(bool includeBags, bool includeArmory, bool requireGlamourable = false)
    {
        var result = new List<PhysicalGearEntry>();
        var inventory = InventoryManager.Instance();
        if (inventory == null)
            return result;

        IEnumerable<InventoryType> types = [];
        if (includeBags) types = types.Concat(BagContainers);
        if (includeArmory) types = types.Concat(ArmoryContainers);

        foreach (var type in types)
        {
            var container = inventory->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded)
                continue;

            for (var slotIndex = 0; slotIndex < container->Size; slotIndex++)
            {
                var item = container->GetInventorySlot(slotIndex);
                if (item == null || item->ItemId == 0)
                    continue;

                var itemId = item->GetBaseItemId();
                if (itemId == 0 || !excel.IsEquipment(itemId))
                    continue;
                if (requireGlamourable && !excel.IsGlamourable(itemId))
                    continue;

                result.Add(new(
                    new(type, slotIndex),
                    itemId,
                    excel.NameOf(itemId),
                    excel.RequiredLevelOf(itemId),
                    excel.IsCrafterGathererOnly(itemId),
                    item->Stains[0] != 0 || item->Stains[1] != 0,
                    item->GlamourId != 0,
                    HasMateria(item)));
            }
        }

        return result
            .OrderBy(x => x.Name, StringComparer.CurrentCulture)
            .ThenBy(x => (int)x.Key.Container)
            .ThenBy(x => x.Key.Slot)
            .ToList();
    }

    public List<DiscardEntry> ScanContainerAll(InventoryType type)
    {
        var result = new List<DiscardEntry>();
        var inventory = InventoryManager.Instance();
        if (inventory == null)
            return result;

        var container = inventory->GetInventoryContainer(type);
        if (container == null || !container->IsLoaded)
            return result;

        for (var slotIndex = 0; slotIndex < container->Size; slotIndex++)
        {
            var item = container->GetInventorySlot(slotIndex);
            if (item == null || item->ItemId == 0)
                continue;

            var itemId = item->GetBaseItemId();
            if (itemId == 0)
                continue;

            var equipment = excel.IsEquipment(itemId);
            result.Add(new(
                new(type, slotIndex),
                itemId,
                excel.NameOf(itemId),
                item->Quantity,
                equipment,
                equipment ? excel.RequiredLevelOf(itemId) : 0,
                equipment && excel.IsCrafterGathererOnly(itemId),
                item->Stains[0] != 0 || item->Stains[1] != 0));
        }

        return result.OrderBy(x => x.Key.Slot).ToList();
    }

    public bool TryResolve(ItemLocationKey key, uint expectedItemId, out InventoryItem* item)
    {
        item = null;
        var inventory = InventoryManager.Instance();
        if (inventory == null)
            return false;

        var container = inventory->GetInventoryContainer(key.Container);
        if (container == null || !container->IsLoaded || key.Slot < 0 || key.Slot >= container->Size)
            return false;

        item = container->GetInventorySlot(key.Slot);
        return item != null && item->ItemId != 0 && item->GetBaseItemId() == expectedItemId;
    }

    public bool TryFindFirstEmptyBag(out ItemLocationKey key)
    {
        var inventory = InventoryManager.Instance();
        if (inventory != null)
        {
            foreach (var type in BagContainers)
            {
                var container = inventory->GetInventoryContainer(type);
                if (container == null || !container->IsLoaded)
                    continue;

                for (var i = 0; i < container->Size; i++)
                {
                    var item = container->GetInventorySlot(i);
                    if (item == null || item->ItemId == 0)
                    {
                        key = new(type, i);
                        return true;
                    }
                }
            }
        }

        key = default;
        return false;
    }

    public int CountFreeBagSlots()
    {
        var inventory = InventoryManager.Instance();
        return inventory == null ? 0 : checked((int)inventory->GetEmptySlotsInBag());
    }

    private static bool HasMateria(InventoryItem* item)
    {
        for (var i = 0; i < item->Materia.Length; i++)
            if (item->Materia[i] != 0)
                return true;
        return false;
    }
}

public sealed unsafe class Stage1DresserToInventory
{
    private readonly ExcelIndex excel;
    private readonly GlamourStateCache glamourCache;
    private readonly IGameGui gameGui;
    private readonly Configuration config;
    private readonly System.Action saveConfig;
    private readonly List<DresserArmoireEntry> entries = [];
    private readonly HashSet<int> selected = [];
    private readonly List<(int Slot, uint ItemId)> queue = [];
    private int index;
    private long nextAt;

    public IReadOnlyList<DresserArmoireEntry> Entries => entries;
    public IReadOnlySet<int> Selected => selected;
    public bool IsRunning { get; private set; }
    public int Restored { get; private set; }
    public int Skipped { get; private set; }
    public int Failed { get; private set; }
    public string Status { get; private set; } = "환상의 옷장을 연 뒤 검색해 주세요.";
    public string? LastItemStatus { get; private set; }

    public Stage1DresserToInventory(ExcelIndex excel, GlamourStateCache glamourCache, IGameGui gameGui, Configuration config, System.Action saveConfig)
    {
        this.excel = excel;
        this.glamourCache = glamourCache;
        this.gameGui = gameGui;
        this.config = config;
        this.saveConfig = saveConfig;
    }

    public void UpdateOptions(bool includeDyed, bool includePlate)
    {
        config.Stage1IncludeDyed = includeDyed;
        config.Stage1IncludePlateRegistered = includePlate;
        saveConfig();
    }

    public int Scan()
    {
        if (IsRunning) return entries.Count;

        entries.Clear();
        selected.Clear();
        glamourCache.Observe();

        var manager = MirageManager.Instance();
        if (manager == null || !manager->PrismBoxLoaded)
        {
            Status = "환상의 옷장 데이터를 읽을 수 없습니다. 환상의 옷장을 연 상태에서 검색해 주세요.";
            return 0;
        }

        if (!glamourCache.HasPlateData)
        {
            Status = "투영세트 등록 여부를 판정하려면 투영세트 편집 화면을 한 번 열어 캐시한 뒤 다시 검색해 주세요.";
            return 0;
        }

        for (var slot = 0; slot < manager->PrismBoxItemIds.Length; slot++)
        {
            var raw = manager->PrismBoxItemIds[slot];
            if (raw == 0) continue;

            var itemId = Normalize(raw);
            if (itemId == 0 || excel.IsOutfitSetToken(itemId) || !excel.TryGetCabinetRow(itemId, out _))
                continue;

            var dyed = manager->PrismBoxStain0Ids[slot] != 0 || manager->PrismBoxStain1Ids[slot] != 0;
            glamourCache.TryGetPlateState(itemId, out var usedOnPlate, out _);
            entries.Add(new(slot, itemId, excel.NameOf(itemId), dyed, usedOnPlate));
        }

        entries.Sort((a, b) =>
        {
            var c = StringComparer.CurrentCulture.Compare(a.Name, b.Name);
            return c != 0 ? c : a.DresserSlot.CompareTo(b.DresserSlot);
        });

        Status = $"검색 완료: 추억의 보관함 대응 환상의 옷장 아이템 {entries.Count}개.";
        return entries.Count;
    }

    public void SetSelected(int slot, bool value)
    {
        if (value) selected.Add(slot);
        else selected.Remove(slot);
    }

    public void SelectAll(bool value)
    {
        selected.Clear();
        if (!value) return;
        foreach (var entry in entries)
            selected.Add(entry.DresserSlot);
    }

    public bool Start()
    {
        if (IsRunning) return false;
        if (!IsDresserOpen())
        {
            Status = "환상의 옷장을 연 상태에서 실행해 주세요.";
            return false;
        }

        queue.Clear();
        foreach (var entry in entries.Where(x => selected.Contains(x.DresserSlot)))
        {
            if (entry.IsDyed && !config.Stage1IncludeDyed)
                continue;
            if (entry.UsedOnPlate && !config.Stage1IncludePlateRegistered)
                continue;
            queue.Add((entry.DresserSlot, entry.ItemId));
        }

        if (queue.Count == 0)
        {
            Status = "현재 옵션 조건에서 옮길 체크 항목이 없습니다.";
            return false;
        }

        var inventory = InventoryManager.Instance();
        if (inventory == null || inventory->GetEmptySlotsInBag() == 0)
        {
            Status = "인벤토리 빈칸이 없습니다.";
            return false;
        }

        index = Restored = Skipped = Failed = 0;
        nextAt = 0;
        LastItemStatus = null;
        IsRunning = true;
        Status = $"체크한 항목 {queue.Count}개를 인벤토리로 옮깁니다.";
        return true;
    }

    public void Stop(string reason = "1단계 작업을 중지했습니다.")
    {
        IsRunning = false;
        Status = reason;
    }

    public void Tick()
    {
        if (!IsRunning) return;
        var now = Environment.TickCount64;
        if (now < nextAt) return;

        if (index >= queue.Count)
        {
            IsRunning = false;
            var completed = $"완료: 복원 {Restored} · 제외 {Skipped} · 실패 {Failed}";
            Scan();
            Status = completed;
            return;
        }

        if (!IsDresserOpen())
        {
            Stop("환상의 옷장이 닫혀 작업을 중지했습니다.");
            return;
        }

        var manager = MirageManager.Instance();
        var inventory = InventoryManager.Instance();
        if (manager == null || inventory == null || !manager->PrismBoxLoaded)
        {
            Stop("환상의 옷장 또는 인벤토리 데이터를 잃어 작업을 중지했습니다.");
            return;
        }

        if (inventory->GetEmptySlotsInBag() == 0)
        {
            Stop("인벤토리 빈칸이 없어 작업을 중지했습니다.");
            return;
        }

        var (slot, expectedItemId) = queue[index];
        if (slot < 0 || slot >= manager->PrismBoxItemIds.Length || Normalize(manager->PrismBoxItemIds[slot]) != expectedItemId)
        {
            Skipped++;
            LastItemStatus = $"{excel.NameOf(expectedItemId)}: 옷장 슬롯 내용이 바뀌어 건너뜁니다.";
        }
        else if (manager->RestorePrismBoxItem((uint)slot))
        {
            Restored++;
            LastItemStatus = $"{excel.NameOf(expectedItemId)}: 인벤토리 복원 요청 완료.";
        }
        else
        {
            Failed++;
            LastItemStatus = $"{excel.NameOf(expectedItemId)}: 게임이 복원 요청을 거절했습니다.";
        }

        index++;
        nextAt = now + 700;
        Status = $"진행 {index}/{queue.Count} · 복원 {Restored} · 제외 {Skipped} · 실패 {Failed}";
    }

    private bool IsDresserOpen()
    {
        var addon = gameGui.GetAddonByName("MiragePrismPrismBox");
        return addon != null && addon.IsVisible;
    }

    private static uint Normalize(uint id) => id >= 1_000_000 ? id % 1_000_000 : id;
}

public sealed unsafe class OutfitCatalog
{
    public sealed record SetInfo(uint RowId, string Name, IReadOnlyList<uint> ItemIds);

    private readonly ExcelIndex excel;
    private readonly Dictionary<uint, SetInfo> sets = [];
    private readonly Dictionary<uint, List<SetInfo>> byPiece = [];

    public OutfitCatalog(IDataManager data, ExcelIndex excel)
    {
        this.excel = excel;
        var sheet = data.GetExcelSheet<RawRow>(name: "MirageStoreSetItem");
        if (sheet == null) return;

        foreach (var row in sheet)
        {
            if (row.RowId == 0) continue;
            var ids = new List<uint>();
            for (var i = 0; i < 9; i++)
            {
                uint id;
                try { id = Convert.ToUInt32(row.ReadColumn(2 + i)); }
                catch { id = 0; }
                ids.Add(id);
            }

            if (ids.Count(x => x != 0) == 0) continue;
            var set = new SetInfo(row.RowId, excel.NameOf(row.RowId), ids);
            sets[row.RowId] = set;
            foreach (var id in ids.Where(x => x != 0))
            {
                if (!byPiece.TryGetValue(id, out var list))
                    byPiece[id] = list = [];
                list.Add(set);
            }
        }
    }

    public IEnumerable<SetInfo> SetsFor(uint itemId)
        => byPiece.TryGetValue(itemId, out var list) ? list : [];

    public IEnumerable<SetInfo> All => sets.Values;
}

public sealed unsafe class Stage2InventoryToDresser
{
    private readonly ExcelIndex excel;
    private readonly InventoryScanner scanner;
    private readonly OutfitCatalog outfits;
    private readonly IGameGui gameGui;
    private readonly Configuration config;
    private readonly System.Action saveConfig;

    private readonly List<PhysicalGearEntry> entries = [];
    private readonly HashSet<ItemLocationKey> selected = [];
    private readonly Queue<SetBatch> setQueue = new();
    private readonly Queue<PhysicalGearEntry> singleQueue = new();

    private PhysicalGearEntry? pendingSingle;
    private long pendingSingleAt;
    private long nextSetActionAt;
    private SingleStoreState singleState;

    public IReadOnlyList<PhysicalGearEntry> Entries => entries;
    public IReadOnlySet<ItemLocationKey> Selected => selected;
    public bool IsRunning { get; private set; }
    public int Stored { get; private set; }
    public int Skipped { get; private set; }
    public int Failed { get; private set; }
    public string Status { get; private set; } = "환상의 옷장을 연 뒤 후보를 검색해 주세요.";
    public string? LastItemStatus { get; private set; }

    public Stage2InventoryToDresser(ExcelIndex excel, InventoryScanner scanner, OutfitCatalog outfits, IGameGui gameGui, Configuration config, System.Action saveConfig)
    {
        this.excel = excel;
        this.scanner = scanner;
        this.outfits = outfits;
        this.gameGui = gameGui;
        this.config = config;
        this.saveConfig = saveConfig;
    }

    public void UpdateIncludeDyed(bool value)
    {
        config.Stage2IncludeDyed = value;
        saveConfig();
    }

    public int Scan()
    {
        if (IsRunning) return entries.Count;
        entries.Clear();
        selected.Clear();

        var manager = MirageManager.Instance();
        if (manager == null || !manager->PrismBoxLoaded)
        {
            Status = "환상의 옷장을 연 상태에서 검색해 주세요.";
            return 0;
        }

        var storedPieces = BuildStoredPieceSet(manager);
        foreach (var entry in scanner.Scan(includeBags: true, includeArmory: true, requireGlamourable: true))
        {
            if (storedPieces.Contains(entry.ItemId))
                continue;
            entries.Add(entry);
        }

        Status = $"검색 완료: 환상의 옷장에 넣을 수 있는 장비 {entries.Count}개.";
        return entries.Count;
    }

    public void SetSelected(ItemLocationKey key, bool value)
    {
        if (value) selected.Add(key);
        else selected.Remove(key);
    }

    public void SelectAll(bool value)
    {
        selected.Clear();
        if (!value) return;
        foreach (var entry in entries)
            selected.Add(entry.Key);
    }

    public void SelectExistingSetCandidates()
    {
        selected.Clear();
        var manager = MirageManager.Instance();
        if (manager == null || !manager->PrismBoxLoaded)
        {
            Status = "환상의 옷장 데이터를 읽을 수 없습니다.";
            return;
        }

        var existing = GetExistingSets(manager);
        foreach (var entry in entries)
        {
            if (entry.IsDyed && !config.Stage2IncludeDyed)
                continue;

            foreach (var set in outfits.SetsFor(entry.ItemId))
            {
                if (!existing.TryGetValue(set.RowId, out var boxIndex))
                    continue;

                var slot = IndexOf(set.ItemIds, entry.ItemId);
                if (slot >= 0 && !manager->IsSetSlotUnlocked(boxIndex, slot))
                {
                    selected.Add(entry.Key);
                    break;
                }
            }
        }

        Status = $"기존 세트에 추가 가능한 항목 {selected.Count}개를 체크했습니다.";
    }

    public bool StartExistingSetsOnly() => Start(storeRemainingSingles: false, existingOnly: true);
    public bool StartSetsThenSingles() => Start(storeRemainingSingles: true, existingOnly: false);

    private bool Start(bool storeRemainingSingles, bool existingOnly)
    {
        if (IsRunning) return false;
        if (!IsDresserReady())
        {
            Status = "환상의 옷장과 '아이템 맡기기' 목록을 연 상태에서 실행해 주세요.";
            return false;
        }

        var chosen = entries
            .Where(x => selected.Contains(x.Key))
            .Where(x => config.Stage2IncludeDyed || !x.IsDyed)
            .ToList();

        if (chosen.Count == 0)
        {
            Status = "현재 옵션 조건에서 처리할 체크 항목이 없습니다.";
            return false;
        }

        setQueue.Clear();
        singleQueue.Clear();
        pendingSingle = null;
        nextSetActionAt = 0;
        singleState = SingleStoreState.None;

        var manager = MirageManager.Instance()!;
        var existing = GetExistingSets(manager);
        var consumed = new HashSet<ItemLocationKey>();

        foreach (var set in outfits.All.OrderBy(x => x.Name, StringComparer.CurrentCulture))
        {
            var batch = new List<PhysicalGearEntry>();
            var localUsed = new HashSet<ItemLocationKey>();

            for (var slotIndex = 0; slotIndex < set.ItemIds.Count && slotIndex < 9; slotIndex++)
            {
                var requiredId = set.ItemIds[slotIndex];
                if (requiredId == 0)
                    continue;

                if (existing.TryGetValue(set.RowId, out var existingIndexForSlot) &&
                    manager->IsSetSlotUnlocked(existingIndexForSlot, slotIndex))
                    continue;

                var piece = chosen.FirstOrDefault(x =>
                    x.ItemId == requiredId &&
                    !consumed.Contains(x.Key) &&
                    !localUsed.Contains(x.Key));

                if (piece is null)
                    continue;

                batch.Add(piece);
                localUsed.Add(piece.Key);
            }

            if (batch.Count == 0)
                continue;

            if (existing.TryGetValue(set.RowId, out var existingIndex))
            {
                setQueue.Enqueue(new(set, batch, existingIndex));
                foreach (var piece in batch)
                    consumed.Add(piece.Key);
            }
            else if (!existingOnly && batch.Count >= 2)
            {
                setQueue.Enqueue(new(set, batch, null));
                foreach (var piece in batch)
                    consumed.Add(piece.Key);
            }
        }

        if (storeRemainingSingles)
        {
            foreach (var entry in chosen.Where(x => !consumed.Contains(x.Key)))
                singleQueue.Enqueue(entry);
        }

        if (setQueue.Count == 0 && singleQueue.Count == 0)
        {
            Status = existingOnly
                ? "체크 항목 중 기존 세트에 추가할 수 있는 아이템이 없습니다."
                : "저장할 수 있는 체크 항목이 없습니다.";
            return false;
        }

        Stored = Skipped = Failed = 0;
        LastItemStatus = null;
        IsRunning = true;
        Status = $"저장 시작: 세트 작업 {setQueue.Count}개 · 단벌 {singleQueue.Count}개.";
        return true;
    }

    public void Stop(string reason = "2단계 작업을 중지했습니다.")
    {
        IsRunning = false;
        pendingSingle = null;
        singleState = SingleStoreState.None;
        Status = reason;
    }

    public void Tick()
    {
        if (!IsRunning) return;
        if (!IsDresserReady())
        {
            Stop("환상의 옷장 또는 아이템 맡기기 목록이 닫혀 작업을 중지했습니다.");
            return;
        }

        if (pendingSingle is not null)
        {
            TickSingle();
            return;
        }

        if (Environment.TickCount64 < nextSetActionAt)
            return;

        if (setQueue.TryDequeue(out var batch))
        {
            StoreSetBatch(batch);
            return;
        }

        if (singleQueue.TryDequeue(out var single))
        {
            pendingSingle = single;
            pendingSingleAt = Environment.TickCount64;
            singleState = SingleStoreState.Select;
            TickSingle();
            return;
        }

        IsRunning = false;
        var completed = $"완료: 저장 {Stored} · 제외 {Skipped} · 실패 {Failed}";
        Scan();
        Status = completed;
    }

    private void StoreSetBatch(SetBatch batch)
    {
        var manager = MirageManager.Instance();
        var inventory = InventoryManager.Instance();
        if (manager == null || inventory == null)
        {
            Stop("환상의 옷장 또는 인벤토리 데이터를 읽을 수 없습니다.");
            return;
        }

        Span<InventoryType> containers = stackalloc InventoryType[9];
        Span<ushort> slots = stackalloc ushort[9];
        containers.Fill(InventoryType.Invalid);
        slots.Clear();

        var filled = 0;
        foreach (var piece in batch.Pieces)
        {
            if (!scanner.TryResolve(piece.Key, piece.ItemId, out _))
                continue;

            var slot = IndexOf(batch.Set.ItemIds, piece.ItemId);
            if (slot < 0 || slot >= 9)
                continue;
            if (batch.ExistingIndex is uint idx && manager->IsSetSlotUnlocked(idx, slot))
                continue;

            containers[filled] = piece.Key.Container;
            slots[filled] = (ushort)piece.Key.Slot;
            filled++;
        }

        var minimum = batch.ExistingIndex.HasValue ? 1 : 2;
        if (filled < minimum)
        {
            Skipped++;
            LastItemStatus = $"{batch.Set.Name}: 저장 가능한 체크 항목이 부족해 건너뜁니다.";
            return;
        }

        bool sent;
        fixed (InventoryType* cp = containers)
        fixed (ushort* sp = slots)
        {
            sent = batch.ExistingIndex is uint idx
                ? manager->StoreExistingOutfit(idx, cp, sp)
                : manager->StoreNewOutfit(batch.Set.RowId, cp, sp);
        }

        if (sent)
        {
            Stored += filled;
            LastItemStatus = $"{batch.Set.Name}: {filled}개 세트 저장 요청 완료.";
        }
        else
        {
            Failed += filled;
            LastItemStatus = $"{batch.Set.Name}: 세트 저장 요청이 거절되었습니다.";
        }

        nextSetActionAt = Environment.TickCount64 + 700;
    }

    private void TickSingle()
    {
        var entry = pendingSingle!;
        var now = Environment.TickCount64;

        if (!scanner.TryResolve(entry.Key, entry.ItemId, out _))
        {
            Stored++;
            LastItemStatus = $"{entry.Name}: 인벤토리에서 사라져 저장 완료로 확인했습니다.";
            pendingSingle = null;
            singleState = SingleStoreState.None;
            return;
        }

        if (now - pendingSingleAt > 12000)
        {
            Failed++;
            LastItemStatus = $"{entry.Name}: 단벌 저장 완료를 확인하지 못해 건너뜁니다.";
            pendingSingle = null;
            singleState = SingleStoreState.None;
            return;
        }

        switch (singleState)
        {
            case SingleStoreState.Select:
            {
                var agent = AgentMiragePrismPrismBox.Instance();
                var data = agent == null ? null : agent->Data;
                if (data == null)
                {
                    Stop("환상의 옷장 맡기기 데이터를 읽을 수 없습니다.");
                    return;
                }

                var row = -1;
                for (var i = 0; i < data->CrystallizeItemCount; i++)
                {
                    var candidate = data->CrystallizeItems[i];
                    if (candidate.Inventory == entry.Key.Container &&
                        candidate.Slot == entry.Key.Slot &&
                        Normalize(candidate.ItemId) == entry.ItemId)
                    {
                        row = i;
                        break;
                    }
                }

                if (row < 0)
                {
                    Skipped++;
                    LastItemStatus = $"{entry.Name}: 게임의 맡기기 목록에서 찾지 못해 건너뜁니다.";
                    pendingSingle = null;
                    singleState = SingleStoreState.None;
                    return;
                }

                if (!FireCallback("MiragePrismPrismBoxCrystallize", 0, row))
                    return;

                singleState = SingleStoreState.Confirm;
                return;
            }

            case SingleStoreState.Confirm:
                if (TryConfirmYesNoForItem(entry.Name))
                    singleState = SingleStoreState.Wait;
                return;

            case SingleStoreState.Wait:
                return;
        }
    }

    private HashSet<uint> BuildStoredPieceSet(MirageManager* manager)
    {
        var result = new HashSet<uint>();
        var setById = outfits.All.ToDictionary(x => x.RowId);
        for (var i = 0; i < manager->PrismBoxItemIds.Length; i++)
        {
            var id = Normalize(manager->PrismBoxItemIds[i]);
            if (id == 0) continue;

            if (!setById.TryGetValue(id, out var set))
            {
                result.Add(id);
                continue;
            }

            for (var slot = 0; slot < set.ItemIds.Count; slot++)
                if (set.ItemIds[slot] != 0 && manager->IsSetSlotUnlocked((uint)i, slot))
                    result.Add(set.ItemIds[slot]);
        }
        return result;
    }

    private Dictionary<uint, uint> GetExistingSets(MirageManager* manager)
    {
        var result = new Dictionary<uint, uint>();
        var setIds = outfits.All.Select(x => x.RowId).ToHashSet();
        for (var i = 0; i < manager->PrismBoxItemIds.Length; i++)
        {
            var id = Normalize(manager->PrismBoxItemIds[i]);
            if (id != 0 && setIds.Contains(id))
                result[id] = (uint)i;
        }
        return result;
    }

    private bool IsDresserReady()
    {
        var manager = MirageManager.Instance();
        return manager != null &&
               manager->PrismBoxLoaded &&
               IsAddonReady("MiragePrismPrismBox") &&
               IsAddonReady("MiragePrismPrismBoxCrystallize");
    }

    private bool IsAddonReady(string name)
    {
        var addon = (AtkUnitBase*)gameGui.GetAddonByName(name).Address;
        return addon != null && addon->IsVisible && addon->IsReady;
    }

    private bool FireCallback(string addonName, params int[] values)
    {
        var addon = (AtkUnitBase*)gameGui.GetAddonByName(addonName).Address;
        if (addon == null || !addon->IsVisible || !addon->IsReady)
            return false;

        var atk = stackalloc AtkValue[values.Length];
        for (var i = 0; i < values.Length; i++)
            atk[i].SetInt(values[i]);
        addon->FireCallback((uint)values.Length, atk);
        return true;
    }

    private bool TryConfirmYesNoForItem(string expectedItemName)
    {
        for (var i = 1; i <= 4; i++)
        {
            var yesno = (AddonSelectYesno*)gameGui.GetAddonByName("SelectYesno", i).Address;
            if (yesno == null || !yesno->AtkUnitBase.IsVisible || !yesno->AtkUnitBase.IsReady)
                continue;

            var prompt = yesno->PromptText == null ? string.Empty : yesno->PromptText->NodeText.ToString();
            if (string.IsNullOrWhiteSpace(prompt) ||
                !prompt.Contains(expectedItemName, StringComparison.OrdinalIgnoreCase))
                continue;

            yesno->AtkUnitBase.FireCallbackInt(0);
            return true;
        }

        return false;
    }

    private static int IndexOf(IReadOnlyList<uint> list, uint value)
    {
        for (var i = 0; i < list.Count; i++)
            if (list[i] == value) return i;
        return -1;
    }

    private static uint Normalize(uint id) => id >= 1_000_000 ? id % 1_000_000 : id;

    private sealed record SetBatch(OutfitCatalog.SetInfo Set, IReadOnlyList<PhysicalGearEntry> Pieces, uint? ExistingIndex);
    private enum SingleStoreState { None, Select, Confirm, Wait }
}

public sealed unsafe class Stage3ArmoryToInventory
{
    private readonly InventoryScanner scanner;
    private readonly Configuration config;
    private readonly System.Action saveConfig;
    private readonly List<PhysicalGearEntry> entries = [];
    private readonly HashSet<ItemLocationKey> selected = [];
    private readonly Queue<PhysicalGearEntry> queue = new();
    private long nextAt;

    public IReadOnlyList<PhysicalGearEntry> Entries => entries;
    public IReadOnlySet<ItemLocationKey> Selected => selected;
    public bool IsRunning { get; private set; }
    public int Moved { get; private set; }
    public int Skipped { get; private set; }
    public int Failed { get; private set; }
    public string Status { get; private set; } = "장비함 후보를 검색해 주세요.";
    public string? LastItemStatus { get; private set; }

    public Stage3ArmoryToInventory(InventoryScanner scanner, Configuration config, System.Action saveConfig)
    {
        this.scanner = scanner;
        this.config = config;
        this.saveConfig = saveConfig;
    }

    public void UpdateSmartSettings(bool includeCrafterGatherer, int maxLevel)
    {
        config.Stage3SmartIncludeCrafterGatherer = includeCrafterGatherer;
        config.Stage3SmartMaxLevel = Math.Clamp(maxLevel, 1, 100);
        saveConfig();
    }

    public int Scan()
    {
        if (IsRunning) return entries.Count;
        entries.Clear();
        selected.Clear();
        entries.AddRange(scanner.Scan(includeBags: false, includeArmory: true));
        Status = $"검색 완료: 장비함 장비 {entries.Count}개.";
        return entries.Count;
    }

    public void SetSelected(ItemLocationKey key, bool value)
    {
        if (value) selected.Add(key);
        else selected.Remove(key);
    }

    public void SelectAll(bool value)
    {
        selected.Clear();
        if (!value) return;
        foreach (var entry in entries)
            selected.Add(entry.Key);
    }

    public void SmartSelect()
    {
        selected.Clear();
        foreach (var entry in entries)
        {
            if (entry.RequiredLevel > config.Stage3SmartMaxLevel)
                continue;
            if (entry.IsCrafterGatherer && !config.Stage3SmartIncludeCrafterGatherer)
                continue;
            selected.Add(entry.Key);
        }

        Status = $"스마트 선택 완료: {selected.Count}개.";
    }

    public bool Start()
    {
        if (IsRunning) return false;
        var chosen = entries.Where(x => selected.Contains(x.Key)).ToList();
        if (chosen.Count == 0)
        {
            Status = "옮길 장비를 체크해 주세요.";
            return false;
        }

        if (scanner.CountFreeBagSlots() < chosen.Count)
        {
            Status = $"인벤토리 빈칸이 부족합니다. 필요 {chosen.Count}칸 / 현재 {scanner.CountFreeBagSlots()}칸.";
            return false;
        }

        queue.Clear();
        foreach (var entry in chosen) queue.Enqueue(entry);
        Moved = Skipped = Failed = 0;
        nextAt = 0;
        IsRunning = true;
        Status = $"체크한 장비 {queue.Count}개를 인벤토리로 옮깁니다.";
        return true;
    }

    public void Stop(string reason = "3단계 작업을 중지했습니다.")
    {
        IsRunning = false;
        Status = reason;
    }

    public void Tick()
    {
        if (!IsRunning) return;
        var now = Environment.TickCount64;
        if (now < nextAt) return;

        if (!queue.TryDequeue(out var entry))
        {
            IsRunning = false;
            var completed = $"완료: 이동 {Moved} · 제외 {Skipped} · 실패 {Failed}";
            Scan();
            Status = completed;
            return;
        }

        var inventory = InventoryManager.Instance();
        if (inventory == null)
        {
            Stop("인벤토리 데이터를 읽을 수 없습니다.");
            return;
        }

        if (!scanner.TryResolve(entry.Key, entry.ItemId, out _))
        {
            Skipped++;
            LastItemStatus = $"{entry.Name}: 장비함 위치가 바뀌어 건너뜁니다.";
        }
        else if (!scanner.TryFindFirstEmptyBag(out var destination))
        {
            queue.Enqueue(entry);
            Stop("인벤토리 빈칸이 없어 작업을 중지했습니다.");
            return;
        }
        else
        {
            var rc = inventory->MoveItemSlot(entry.Key.Container, (ushort)entry.Key.Slot, destination.Container, (ushort)destination.Slot, true);
            if (rc == 0)
            {
                Moved++;
                LastItemStatus = $"{entry.Name}: 인벤토리 이동 요청 완료.";
            }
            else
            {
                Failed++;
                LastItemStatus = $"{entry.Name}: 이동 실패 ({rc}).";
            }
        }

        nextAt = now + 500;
        Status = $"진행: 이동 {Moved} · 제외 {Skipped} · 실패 {Failed} · 남음 {queue.Count}";
    }
}

public sealed unsafe class Stage4Discard
{
    private readonly InventoryScanner scanner;
    private readonly IGameGui gameGui;
    private readonly Configuration config;
    private readonly System.Action saveConfig;

    private readonly List<PhysicalGearEntry> entries = [];
    private readonly HashSet<ItemLocationKey> selected = [];
    private readonly Queue<DiscardEntry> queue = new();

    private DiscardEntry? pending;
    private long pendingAt;
    private long nextAt;
    private DiscardMode mode;

    public IReadOnlyList<PhysicalGearEntry> Entries => entries;
    public IReadOnlySet<ItemLocationKey> Selected => selected;
    public bool IsRunning { get; private set; }
    public bool IsBag4Mode => IsRunning && mode == DiscardMode.Inventory4All;
    public int Discarded { get; private set; }
    public int Skipped { get; private set; }
    public int Failed { get; private set; }
    public string Status { get; private set; } = "파기 후보를 검색해 주세요.";
    public string? LastItemStatus { get; private set; }

    public Stage4Discard(InventoryScanner scanner, IGameGui gameGui, Configuration config, System.Action saveConfig)
    {
        this.scanner = scanner;
        this.gameGui = gameGui;
        this.config = config;
        this.saveConfig = saveConfig;
    }

    public void UpdateSmartSettings(bool includeCrafterGatherer, int maxLevel)
    {
        config.Stage4SmartIncludeCrafterGatherer = includeCrafterGatherer;
        config.Stage4SmartMaxLevel = Math.Clamp(maxLevel, 1, 100);
        saveConfig();
    }

    public int Scan()
    {
        if (IsRunning) return entries.Count;
        entries.Clear();
        selected.Clear();
        entries.AddRange(scanner.Scan(includeBags: true, includeArmory: true));
        Status = $"검색 완료: 장비함+인벤토리 장비 {entries.Count}개.";
        return entries.Count;
    }

    public void SetSelected(ItemLocationKey key, bool value)
    {
        if (value) selected.Add(key);
        else selected.Remove(key);
    }

    public void SelectAll(bool value)
    {
        selected.Clear();
        if (!value) return;
        foreach (var entry in entries)
            selected.Add(entry.Key);
    }

    public void SmartSelect()
    {
        selected.Clear();
        foreach (var entry in entries)
        {
            if (entry.RequiredLevel > config.Stage4SmartMaxLevel)
                continue;
            if (entry.IsCrafterGatherer && !config.Stage4SmartIncludeCrafterGatherer)
                continue;
            selected.Add(entry.Key);
        }

        Status = $"스마트 선택 완료: {selected.Count}개.";
    }

    public int PreviewInventory4Count()
        => scanner.ScanContainerAll(InventoryType.Inventory4).Count;

    public bool StartSelected()
    {
        if (IsRunning) return false;

        var chosen = entries
            .Where(x => selected.Contains(x.Key))
            .Select(ToDiscardEntry)
            .ToList();

        if (chosen.Count == 0)
        {
            Status = "파기할 아이템을 체크해 주세요.";
            return false;
        }

        PrepareQueue(chosen, DiscardMode.SelectedGear);
        Status = $"체크한 장비 {queue.Count}개를 순차 파기합니다.";
        return true;
    }

    public bool StartInventory4All()
    {
        if (IsRunning) return false;

        var chosen = scanner.ScanContainerAll(InventoryType.Inventory4);
        if (chosen.Count == 0)
        {
            Status = "4번 인벤토리가 비어 있습니다.";
            return false;
        }

        PrepareQueue(chosen, DiscardMode.Inventory4All);
        Status = $"4번 인벤토리의 모든 아이템 {queue.Count}개 슬롯을 순차 파기합니다.";
        return true;
    }

    public void Stop(string reason = "4단계 파기를 중지했습니다.")
    {
        IsRunning = false;
        pending = null;
        queue.Clear();
        mode = DiscardMode.None;
        Status = reason;
    }

    public void Tick()
    {
        if (!IsRunning) return;
        var now = Environment.TickCount64;

        if (pending is not null)
        {
            AutoConfirmDiscard(pending);
            if (!scanner.TryResolve(pending.Key, pending.ItemId, out _))
            {
                Discarded++;
                LastItemStatus = $"{pending.Name}: 파기 완료.";
                pending = null;
                nextAt = now + 500;
            }
            else if (now - pendingAt > 8000)
            {
                Failed++;
                LastItemStatus = $"{pending.Name}: 파기 완료를 확인하지 못해 다음 항목으로 넘어갑니다.";
                pending = null;
                nextAt = now + 500;
            }
            return;
        }

        if (now < nextAt) return;

        if (!queue.TryDequeue(out var entry))
        {
            IsRunning = false;
            var completed = $"완료: 파기 {Discarded} · 제외 {Skipped} · 실패 {Failed}";
            mode = DiscardMode.None;
            Scan();
            Status = completed;
            return;
        }

        if (!scanner.TryResolve(entry.Key, entry.ItemId, out _))
        {
            Skipped++;
            LastItemStatus = $"{entry.Name}: 위치가 바뀌어 건너뜁니다.";
            nextAt = now + 250;
            return;
        }

        var inventory = InventoryManager.Instance();
        if (inventory == null)
        {
            Stop("인벤토리 데이터를 읽을 수 없습니다.");
            return;
        }

        var rc = inventory->DiscardItem(entry.Key.Container, (ushort)entry.Key.Slot);
        if (rc != 0)
        {
            Failed++;
            LastItemStatus = $"{entry.Name}: 파기 요청이 거절되었습니다. ({rc})";
            nextAt = now + 500;
            return;
        }

        pending = entry;
        pendingAt = now;
        LastItemStatus = $"{entry.Name}: 파기 요청 완료. 확인 창이 뜨면 자동으로 '예'를 선택합니다.";
        Status = $"파기 진행: 완료 {Discarded} · 제외 {Skipped} · 실패 {Failed} · 남음 {queue.Count + 1}";
    }

    private void PrepareQueue(IEnumerable<DiscardEntry> chosen, DiscardMode discardMode)
    {
        queue.Clear();
        foreach (var entry in chosen)
            queue.Enqueue(entry);

        Discarded = Skipped = Failed = 0;
        pending = null;
        nextAt = 0;
        LastItemStatus = null;
        mode = discardMode;
        IsRunning = true;
    }

    private void AutoConfirmDiscard(DiscardEntry expected)
    {
        if (Environment.TickCount64 - pendingAt > 3000)
            return;

        for (var i = 1; i <= 4; i++)
        {
            var yesno = (AddonSelectYesno*)gameGui.GetAddonByName("SelectYesno", i).Address;
            if (yesno == null || !yesno->AtkUnitBase.IsVisible)
                continue;

            var prompt = yesno->PromptText == null ? string.Empty : yesno->PromptText->NodeText.ToString();
            if (string.IsNullOrWhiteSpace(prompt) ||
                !prompt.Contains(expected.Name, StringComparison.OrdinalIgnoreCase))
                continue;

            yesno->AtkUnitBase.FireCallbackInt(0);
            return;
        }
    }

    private static DiscardEntry ToDiscardEntry(PhysicalGearEntry entry)
        => new(
            entry.Key,
            entry.ItemId,
            entry.Name,
            1,
            true,
            entry.RequiredLevel,
            entry.IsCrafterGatherer,
            entry.IsDyed);

    private enum DiscardMode
    {
        None,
        SelectedGear,
        Inventory4All,
    }
}

