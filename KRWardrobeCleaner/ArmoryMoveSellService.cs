using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace KRWardrobeCleaner;

public sealed record ArmoryManageEntry(
    uint ItemId,
    string Name,
    bool IsCraftingGear,
    bool AlreadyInArmoire,
    bool InGearset,
    bool HasModifiedState);

public sealed unsafe class ArmoryMoveSellService
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

    private static readonly InventoryType[] BagContainers =
    [
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
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

    private const uint SellLabelAddonRow = 93;

    private readonly ExcelIndex excel;
    private readonly Configuration config;
    private readonly System.Action saveConfig;
    private readonly IDataManager data;
    private readonly IGameGui gameGui;

    private readonly List<ArmoryManageEntry> entries = [];
    private readonly HashSet<uint> selected = [];
    private readonly List<uint> workQueue = [];

    private int workIndex;
    private long nextActionAt;
    private uint pendingSaleItemId;
    private string pendingSaleItemName = string.Empty;
    private long pendingSaleStartedAt;

    public IReadOnlyList<ArmoryManageEntry> Entries => entries;
    public IReadOnlySet<uint> Selected => selected;
    public IReadOnlyList<ArmoryManageEntry> SelectedEntries => entries.Where(x => selected.Contains(x.ItemId)).ToList();

    public bool IsMoving { get; private set; }
    public bool IsSelling { get; private set; }
    public int Moved { get; private set; }
    public int Sold { get; private set; }
    public int Skipped { get; private set; }
    public int Failed { get; private set; }

    public int SkippedCrafting { get; private set; }
    public int SkippedGearset { get; private set; }
    public int SkippedModified { get; private set; }
    public int SkippedDuplicate { get; private set; }

    public string Status { get; private set; } = "장비함 정리 후보를 검색해 주세요.";
    public string? LastItemStatus { get; private set; }

    public ArmoryMoveSellService(
        ExcelIndex excel,
        Configuration config,
        System.Action saveConfig,
        IDataManager data,
        IGameGui gameGui)
    {
        this.excel = excel;
        this.config = config;
        this.saveConfig = saveConfig;
        this.data = data;
        this.gameGui = gameGui;
    }

    public int Scan()
    {
        if (IsMoving || IsSelling)
            return entries.Count;

        entries.Clear();
        selected.Clear();
        workQueue.Clear();
        SkippedCrafting = 0;
        SkippedGearset = 0;
        SkippedModified = 0;
        SkippedDuplicate = 0;
        LastItemStatus = null;

        var inventory = InventoryManager.Instance();
        if (inventory == null)
        {
            Status = "인벤토리 데이터를 불러올 수 없습니다.";
            return 0;
        }

        var gearsetItems = BuildGearsetItemSet();
        var cabinet = UIState.Instance();
        var seen = new HashSet<uint>();

        foreach (var type in ArmoryContainers)
        {
            var container = inventory->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded)
                continue;

            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot == null || slot->ItemId == 0)
                    continue;

                var itemId = slot->GetBaseItemId();
                if (itemId == 0 || !seen.Add(itemId))
                    continue;

                if (!excel.TryGetCabinetRow(itemId, out var cabinetRow))
                    continue;

                var crafting = excel.IsCraftingOnly(itemId);
                if (crafting && !config.IncludeCraftingGearInArmoryMove)
                {
                    SkippedCrafting++;
                    continue;
                }

                var inGearset = gearsetItems.Contains(itemId);
                var modified = !IsPlainItem(slot);

                if (inGearset)
                    SkippedGearset++;
                if (modified)
                    SkippedModified++;

                if (CountPhysicalCopies(inventory, itemId) != 1)
                {
                    SkippedDuplicate++;
                    continue;
                }

                var alreadyStored = cabinet != null &&
                                    cabinet->Cabinet.IsCabinetLoaded() &&
                                    cabinet->Cabinet.IsItemInCabinet(cabinetRow);

                entries.Add(new(
                    itemId,
                    excel.NameOf(itemId),
                    crafting,
                    alreadyStored,
                    inGearset,
                    modified));
            }
        }

        entries.Sort((a, b) => StringComparer.CurrentCulture.Compare(a.Name, b.Name));
        Status = $"장비함 후보 검색 완료: {entries.Count}개. 장비 세트/염색/마테리아/투영 상태가 있어도 인벤토리 이동 후보에는 표시합니다.";
        return entries.Count;
    }

    public void SetSelected(uint itemId, bool value)
    {
        if (value) selected.Add(itemId);
        else selected.Remove(itemId);
    }

    public void SelectAll(bool value)
    {
        selected.Clear();
        if (!value) return;
        foreach (var entry in entries)
            selected.Add(entry.ItemId);
    }

    public bool StartMoveSelected()
    {
        if (IsMoving || IsSelling)
            return false;

        var ids = entries.Where(x => selected.Contains(x.ItemId)).Select(x => x.ItemId).ToList();
        if (ids.Count == 0)
        {
            Status = "인벤토리로 옮길 항목을 체크해 주세요.";
            return false;
        }

        var inventory = InventoryManager.Instance();
        if (inventory == null)
        {
            Status = "인벤토리 데이터를 불러올 수 없습니다.";
            return false;
        }

        if (CountFreeBagSlots(inventory) < ids.Count)
        {
            Status = $"가방 빈칸이 부족합니다. 필요 {ids.Count}칸 / 현재 {CountFreeBagSlots(inventory)}칸.";
            return false;
        }

        workQueue.Clear();
        workQueue.AddRange(ids);
        workIndex = 0;
        Moved = 0;
        Skipped = 0;
        Failed = 0;
        nextActionAt = 0;
        IsMoving = true;
        Status = $"체크한 장비 {ids.Count}개를 인벤토리로 옮기는 중입니다.";
        return true;
    }

    public bool StartSellSelected()
    {
        if (IsMoving || IsSelling)
            return false;

        if (!ShopReadyToSell())
        {
            Status = "일반 상점의 구매/판매 창을 연 뒤 판매를 시작해 주세요.";
            return false;
        }

        var ids = entries
            .Where(x => selected.Contains(x.ItemId))
            .Where(x => FindBagItem(x.ItemId, out _, out _))
            .Select(x => x.ItemId)
            .ToList();

        if (ids.Count == 0)
        {
            Status = "판매할 체크 항목이 인벤토리에 없습니다. 먼저 '체크 항목 인벤토리로 이동'을 실행해 주세요.";
            return false;
        }

        workQueue.Clear();
        workQueue.AddRange(ids);
        workIndex = 0;
        Sold = 0;
        Skipped = 0;
        Failed = 0;
        pendingSaleItemId = 0;
        pendingSaleItemName = string.Empty;
        nextActionAt = 0;
        IsSelling = true;
        Status = $"체크한 장비 {ids.Count}개를 상점에 판매하는 중입니다.";
        return true;
    }

    public void Stop()
    {
        IsMoving = false;
        IsSelling = false;
        pendingSaleItemId = 0;
        Status = "장비함 선택 작업을 중지했습니다.";
    }

    public void Tick()
    {
        if (IsMoving)
            TickMove();
        if (IsSelling)
            TickSell();
    }

    public void UpdateSettings(bool includeCrafting, int moveInterval, int sellInterval)
    {
        config.IncludeCraftingGearInArmoryMove = includeCrafting;
        config.ArmoryMoveIntervalMs = Math.Clamp(moveInterval, 300, 3000);
        config.VendorSellIntervalMs = Math.Clamp(sellInterval, 400, 3000);
        saveConfig();
    }

    private void TickMove()
    {
        var now = Environment.TickCount64;
        if (now < nextActionAt)
            return;

        if (workIndex >= workQueue.Count)
        {
            IsMoving = false;
            Status = $"인벤토리 이동 완료: 이동 {Moved} · 제외 {Skipped} · 실패 {Failed}";
            return;
        }

        var itemId = workQueue[workIndex];
        var inventory = InventoryManager.Instance();
        if (inventory == null)
        {
            Stop();
            Status = "인벤토리 데이터를 잃어 작업을 중지했습니다.";
            return;
        }

        if (!FindUniqueArmoryItem(inventory, itemId, out var srcType, out var srcSlot, out var item))
        {
            Skipped++;
            LastItemStatus = $"{excel.NameOf(itemId)}: 장비함에서 찾지 못해 건너뜁니다.";
            AdvanceMove(now);
            return;
        }

        if (CountPhysicalCopies(inventory, itemId) != 1)
        {
            Skipped++;
            LastItemStatus = $"{excel.NameOf(itemId)}: 동일 아이템이 여러 개라 이동 대상을 특정할 수 없어 건너뜁니다.";
            AdvanceMove(now);
            return;
        }

        if (!FindFirstEmptyBagSlot(inventory, out var dstType, out var dstSlot))
        {
            IsMoving = false;
            Status = "가방 빈칸이 없어 이동을 중지했습니다.";
            return;
        }

        var rc = inventory->MoveItemSlot(srcType, (ushort)srcSlot, dstType, (ushort)dstSlot, true);
        if (rc >= 0)
        {
            Moved++;
            LastItemStatus = $"{excel.NameOf(itemId)}: 인벤토리로 이동 요청 완료.";
        }
        else
        {
            Failed++;
            LastItemStatus = $"{excel.NameOf(itemId)}: 인벤토리 이동에 실패했습니다. (결과 {rc})";
        }

        AdvanceMove(now);
    }

    private void AdvanceMove(long now)
    {
        workIndex++;
        Status = $"인벤토리 이동 중 {workIndex}/{workQueue.Count} · 이동 {Moved} · 제외 {Skipped} · 실패 {Failed}";
        nextActionAt = now + Math.Clamp(config.ArmoryMoveIntervalMs, 300, 3000);
    }

    private void TickSell()
    {
        var now = Environment.TickCount64;

        if (pendingSaleItemId != 0)
        {
            if (!FindBagItem(pendingSaleItemId, out _, out _))
            {
                Sold++;
                LastItemStatus = $"{pendingSaleItemName}: 판매 완료.";
                pendingSaleItemId = 0;
                pendingSaleItemName = string.Empty;
                workIndex++;
                nextActionAt = now + Math.Clamp(config.VendorSellIntervalMs, 400, 3000);
                return;
            }

            var handler = GetShopHandler();
            if (handler != null && handler->WaitingForSellConfirm)
                TryConfirmSell(pendingSaleItemName);

            if (now - pendingSaleStartedAt > 6000)
            {
                Failed++;
                LastItemStatus = $"{pendingSaleItemName}: 판매 완료를 확인하지 못해 건너뜁니다.";
                pendingSaleItemId = 0;
                pendingSaleItemName = string.Empty;
                workIndex++;
                nextActionAt = now + Math.Clamp(config.VendorSellIntervalMs, 400, 3000);
            }

            return;
        }

        if (now < nextActionAt)
            return;

        if (workIndex >= workQueue.Count)
        {
            IsSelling = false;
            Status = $"상점 판매 완료: 판매 {Sold} · 제외 {Skipped} · 실패 {Failed}";
            return;
        }

        if (!ShopReadyToSell())
        {
            IsSelling = false;
            Status = "상점 창이 닫혔거나 거래 중 상태가 되어 판매를 중지했습니다.";
            return;
        }

        var itemId = workQueue[workIndex];
        if (!FindBagItem(itemId, out var type, out var slot))
        {
            Skipped++;
            LastItemStatus = $"{excel.NameOf(itemId)}: 인벤토리에서 찾지 못해 건너뜁니다.";
            workIndex++;
            nextActionAt = now + Math.Clamp(config.VendorSellIntervalMs, 400, 3000);
            return;
        }

        if (TrySell(type, (short)slot))
        {
            pendingSaleItemId = itemId;
            pendingSaleItemName = excel.NameOf(itemId);
            pendingSaleStartedAt = now;
            LastItemStatus = $"{pendingSaleItemName}: 판매 요청을 보냈습니다.";
        }
        else
        {
            Failed++;
            LastItemStatus = $"{excel.NameOf(itemId)}: 이 상점에서 판매할 수 없거나 판매 메뉴를 찾지 못했습니다.";
            workIndex++;
            nextActionAt = now + Math.Clamp(config.VendorSellIntervalMs, 400, 3000);
        }

        Status = $"상점 판매 중 {workIndex}/{workQueue.Count} · 판매 {Sold} · 제외 {Skipped} · 실패 {Failed}";
    }

    private static bool IsPlainItem(InventoryItem* item)
    {
        if (item == null) return false;
        if (item->GetConditionPercentage() != 100) return false;
        if (item->SpiritbondOrCollectability != 0) return false;
        if (item->GlamourId != 0) return false;
        if (item->Stains[0] != 0 || item->Stains[1] != 0) return false;
        if ((item->Flags & InventoryItem.ItemFlags.CompanyCrestApplied) != 0) return false;
        for (var i = 0; i < item->Materia.Length; i++)
            if (item->Materia[i] != 0) return false;
        return true;
    }

    private static int CountPhysicalCopies(InventoryManager* inventory, uint itemId)
    {
        var count = 0;
        foreach (var type in DuplicateCheckContainers)
        {
            var container = inventory->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded) continue;
            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot != null && slot->ItemId != 0 && slot->GetBaseItemId() == itemId)
                    count++;
            }
        }
        return count;
    }

    private static bool FindUniqueArmoryItem(
        InventoryManager* inventory,
        uint itemId,
        out InventoryType type,
        out int slotIndex,
        out InventoryItem* found)
    {
        type = InventoryType.Invalid;
        slotIndex = -1;
        found = null;
        var matches = 0;

        foreach (var containerType in ArmoryContainers)
        {
            var container = inventory->GetInventoryContainer(containerType);
            if (container == null || !container->IsLoaded) continue;

            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot == null || slot->ItemId == 0 || slot->GetBaseItemId() != itemId)
                    continue;

                matches++;
                type = containerType;
                slotIndex = i;
                found = slot;
            }
        }

        return matches == 1 && found != null;
    }

    private static bool FindFirstEmptyBagSlot(InventoryManager* inventory, out InventoryType type, out int slotIndex)
    {
        foreach (var containerType in BagContainers)
        {
            var container = inventory->GetInventoryContainer(containerType);
            if (container == null || !container->IsLoaded) continue;

            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot == null || slot->ItemId == 0)
                {
                    type = containerType;
                    slotIndex = i;
                    return true;
                }
            }
        }

        type = InventoryType.Invalid;
        slotIndex = -1;
        return false;
    }

    private static int CountFreeBagSlots(InventoryManager* inventory)
    {
        var count = 0;
        foreach (var type in BagContainers)
        {
            var container = inventory->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded) continue;
            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot == null || slot->ItemId == 0) count++;
            }
        }
        return count;
    }

    private static bool FindBagItem(uint itemId, out InventoryType type, out int slotIndex)
    {
        var inventory = InventoryManager.Instance();
        if (inventory != null)
        {
            foreach (var containerType in BagContainers)
            {
                var container = inventory->GetInventoryContainer(containerType);
                if (container == null || !container->IsLoaded) continue;

                for (var i = 0; i < container->Size; i++)
                {
                    var slot = container->GetInventorySlot(i);
                    if (slot != null && slot->ItemId != 0 && slot->GetBaseItemId() == itemId)
                    {
                        type = containerType;
                        slotIndex = i;
                        return true;
                    }
                }
            }
        }

        type = InventoryType.Invalid;
        slotIndex = -1;
        return false;
    }

    private static HashSet<uint> BuildGearsetItemSet()
    {
        var result = new HashSet<uint>();
        var module = RaptureGearsetModule.Instance();
        if (module == null) return result;

        for (byte i = 0; i < 100; i++)
        {
            if (!module->IsValidGearset(i)) continue;
            var gearset = module->GetGearset(i);
            if (gearset == null || !gearset->Flags.HasFlag(RaptureGearsetModule.GearsetFlag.Exists))
                continue;

            foreach (var item in gearset->Items.ToArray())
            {
                var id = item.ItemId % 1_000_000u;
                if (id != 0) result.Add(id);
            }
        }

        return result;
    }

    private ShopEventHandler* GetShopHandler()
    {
        var proxy = ShopEventHandler.AgentProxy.Instance();
        return proxy == null ? null : proxy->Handler;
    }

    private bool ShopReadyToSell()
    {
        var handler = GetShopHandler();
        if (handler == null) return false;

        var addon = gameGui.GetAddonByName("Shop");
        return addon != null &&
               addon.IsVisible &&
               handler->CurrentMode == 1 &&
               !handler->StartingSell &&
               !handler->WaitingForSellConfirm &&
               !handler->WaitingForTransactionToFinish;
    }

    private string SellLabel
    {
        get
        {
            var sheet = data.GetExcelSheet<Addon>();
            return sheet != null && sheet.TryGetRow(SellLabelAddonRow, out var row)
                ? row.Text.ToString()
                : "Sell";
        }
    }

    private bool TrySell(InventoryType container, short slot)
    {
        var ctx = AgentInventoryContext.Instance();
        if (ctx == null) return false;

        AtkUnitBase* menu = null;
        try
        {
            ctx->OpenForItemSlot(container, slot, 0, InventoryOwnerAddonId());

            var menuId = ctx->AgentInterface.GetAddonId();
            if (menuId == 0) return false;

            menu = RaptureAtkUnitManager.Instance()->GetAddonById((ushort)menuId);
            if (menu == null) return false;

            var label = SellLabel;
            for (var i = 0; i < ctx->ContextItemCount; i++)
            {
                var param = ctx->EventParams[ctx->ContexItemStartIndex + i];
                if (param.Type is not (AtkValueType.String or AtkValueType.ManagedString))
                    continue;
                if (!string.Equals(param.GetValueAsString(), label, StringComparison.Ordinal))
                    continue;
                if (ctx->IsContextItemDisabled(i))
                    return false;

                var values = stackalloc AtkValue[5];
                values[0].SetInt(0);
                values[1].SetInt(i);
                values[2].SetUInt(0);
                values[3].SetInt(0);
                values[4].SetInt(0);
                menu->FireCallback(5, values);
                return true;
            }

            return false;
        }
        finally
        {
            ctx->AgentInterface.Hide();
            if (menu != null)
                menu->Close(false);
        }
    }

    private static uint InventoryOwnerAddonId()
    {
        var module = AgentModule.Instance();
        var agent = module == null ? null : module->GetAgentByInternalId(AgentId.Inventory);
        return agent == null ? 0 : agent->GetAddonId();
    }

    private bool TryConfirmSell(string expectedItemName)
    {
        var handler = GetShopHandler();
        if (handler == null || !handler->WaitingForSellConfirm || string.IsNullOrWhiteSpace(expectedItemName))
            return false;

        for (var i = 1; i <= 4; i++)
        {
            var yesno = (AddonSelectYesno*)gameGui.GetAddonByName("SelectYesno", i).Address;
            if (yesno == null || !yesno->AtkUnitBase.IsVisible || yesno->PromptText == null)
                continue;

            var prompt = yesno->PromptText->NodeText.ToString();
            if (string.IsNullOrWhiteSpace(prompt) ||
                !prompt.Contains(expectedItemName, StringComparison.OrdinalIgnoreCase))
                continue;

            yesno->AtkUnitBase.FireCallbackInt(0);
            return true;
        }

        return false;
    }
}
