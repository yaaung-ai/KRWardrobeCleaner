using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace KRWardrobeCleaner;

public enum FinalActionMode
{
    None,
    Armoire,
    Sell,
    Outfit,
}

public sealed unsafe class FinalDispositionService
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

    private const uint SellLabelAddonRow = 93;
    private const int OutfitSlotCount = 9;

    private readonly ExcelIndex excel;
    private readonly IDataManager data;
    private readonly IGameGui gameGui;
    private readonly Configuration config;

    private readonly Dictionary<uint, OutfitSetInfo> outfits = [];
    private readonly Dictionary<uint, List<uint>> outfitIdsByPiece = [];
    private readonly List<uint> queue = [];

    private int index;
    private long nextActionAt;
    private uint pendingSaleItemId;
    private string pendingSaleItemName = string.Empty;
    private long pendingSaleStartedAt;

    private readonly Queue<OutfitBatch> outfitQueue = new();

    public FinalActionMode Mode { get; private set; }
    public bool IsRunning => Mode != FinalActionMode.None;
    public int Total => Mode == FinalActionMode.Outfit ? OutfitTotal : queue.Count;
    public int Processed => Mode == FinalActionMode.Outfit ? OutfitProcessed : index;
    public int Success { get; private set; }
    public int Skipped { get; private set; }
    public int Failed { get; private set; }
    public int OutfitTotal { get; private set; }
    public int OutfitProcessed { get; private set; }
    public string Status { get; private set; } = "정리할 항목을 체크한 뒤 처리 방식을 선택해 주세요.";
    public string? LastItemStatus { get; private set; }

    public FinalDispositionService(ExcelIndex excel, IDataManager data, IGameGui gameGui, Configuration config)
    {
        this.excel = excel;
        this.data = data;
        this.gameGui = gameGui;
        this.config = config;
        BuildOutfitIndex();
    }

    public bool StartArmoire(IReadOnlyCollection<ArmoryManageEntry> selected)
    {
        if (!CanStart(selected))
            return false;

        var ui = UIState.Instance();
        if (ui == null || !ui->Cabinet.IsCabinetLoaded())
        {
            Status = "추억의 보관함을 연 상태에서 실행해 주세요.";
            return false;
        }

        PrepareQueue(selected);
        Mode = FinalActionMode.Armoire;
        Status = $"체크한 장비 {queue.Count}개를 추억의 보관함으로 옮깁니다.";
        return true;
    }

    public bool StartSell(IReadOnlyCollection<ArmoryManageEntry> selected)
    {
        if (!CanStart(selected))
            return false;

        if (!ShopReadyToSell())
        {
            Status = "일반 상점의 구매/판매 창을 연 상태에서 실행해 주세요.";
            return false;
        }

        PrepareQueue(selected);
        Mode = FinalActionMode.Sell;
        Status = $"체크한 장비 {queue.Count}개를 인벤토리로 옮긴 뒤 상점에 판매합니다.";
        return true;
    }

    public bool StartOutfit(IReadOnlyCollection<ArmoryManageEntry> selected)
    {
        if (!CanStart(selected))
            return false;

        var mirage = MirageManager.Instance();
        if (mirage == null || !mirage->PrismBoxLoaded)
        {
            Status = "환상의 옷장을 연 상태에서 실행해 주세요.";
            return false;
        }

        outfitQueue.Clear();
        OutfitProcessed = 0;
        Success = 0;
        Skipped = 0;
        Failed = 0;
        LastItemStatus = null;

        var selectedIds = selected.Select(x => x.ItemId).ToHashSet();
        var usedIds = new HashSet<uint>();

        foreach (var set in outfits.Values.OrderBy(x => x.Name, StringComparer.CurrentCulture))
        {
            var pieces = set.ItemIds.Where(selectedIds.Contains).Distinct().ToList();
            if (pieces.Count < 2)
                continue;

            // 한 아이템이 여러 세트에 걸리는 경우 먼저 가장 많은 체크 항목을 가진 세트가 잡도록 한다.
            pieces = pieces.Where(id => !usedIds.Contains(id)).ToList();
            if (pieces.Count < 2)
                continue;

            foreach (var id in pieces)
                usedIds.Add(id);

            outfitQueue.Enqueue(new OutfitBatch(set, pieces));
        }

        var unmatched = selectedIds.Count - usedIds.Count;
        OutfitTotal = outfitQueue.Count;

        if (OutfitTotal == 0)
        {
            Status = "체크 항목 중 2개 이상을 한 세트로 묶을 수 있는 환상의 옷장 세트가 없습니다.";
            return false;
        }

        Mode = FinalActionMode.Outfit;
        Status = $"세트화 가능한 묶음 {OutfitTotal}개를 환상의 옷장에 저장합니다.";
        if (unmatched > 0)
            Status += $" 세트화할 수 없는 체크 항목 {unmatched}개는 건너뜁니다.";
        return true;
    }

    public void Stop(string reason = "3단계 자동 처리를 중지했습니다.")
    {
        Mode = FinalActionMode.None;
        pendingSaleItemId = 0;
        outfitQueue.Clear();
        Status = reason;
    }

    public void Tick()
    {
        switch (Mode)
        {
            case FinalActionMode.Armoire:
                TickArmoire();
                break;
            case FinalActionMode.Sell:
                TickSell();
                break;
            case FinalActionMode.Outfit:
                TickOutfit();
                break;
        }
    }

    private bool CanStart(IReadOnlyCollection<ArmoryManageEntry> selected)
    {
        if (IsRunning)
        {
            Status = "이미 3단계 작업이 진행 중입니다.";
            return false;
        }

        if (selected.Count == 0)
        {
            Status = "처리할 장비를 먼저 체크해 주세요.";
            return false;
        }

        return true;
    }

    private void PrepareQueue(IReadOnlyCollection<ArmoryManageEntry> selected)
    {
        queue.Clear();
        queue.AddRange(selected.Select(x => x.ItemId).Distinct());
        index = 0;
        Success = 0;
        Skipped = 0;
        Failed = 0;
        LastItemStatus = null;
        nextActionAt = 0;
        pendingSaleItemId = 0;
        pendingSaleItemName = string.Empty;
    }

    private void TickArmoire()
    {
        var now = Environment.TickCount64;
        if (now < nextActionAt)
            return;

        var ui = UIState.Instance();
        var inventory = InventoryManager.Instance();
        if (ui == null || inventory == null || !ui->Cabinet.IsCabinetLoaded())
        {
            Stop("추억의 보관함이 닫혀 3단계 보관을 중지했습니다.");
            return;
        }

        if (index >= queue.Count)
        {
            Mode = FinalActionMode.None;
            Status = $"추억의 보관함 처리 완료: 보관 {Success} · 제외 {Skipped} · 실패 {Failed}";
            return;
        }

        var itemId = queue[index];
        if (!excel.TryGetCabinetRow(itemId, out var cabinetRow))
        {
            SkipCurrent($"{excel.NameOf(itemId)}: 추억의 보관함 대상이 아닙니다.", now);
            return;
        }

        if (ui->Cabinet.IsItemInCabinet(cabinetRow))
        {
            SkipCurrent($"{excel.NameOf(itemId)}: 이미 추억의 보관함에 등록되어 있습니다.", now);
            return;
        }

        if (!TryFindPhysicalItem(inventory, itemId, out _, out _, out _))
        {
            SkipCurrent($"{excel.NameOf(itemId)}: 장비함/인벤토리에서 찾지 못했습니다.", now);
            return;
        }

        var ok = ui->Cabinet.StoreCabinetItem(cabinetRow);
        if (ok)
        {
            Success++;
            LastItemStatus = $"{excel.NameOf(itemId)}: 추억의 보관함에 보관 요청 완료.";
        }
        else
        {
            Failed++;
            LastItemStatus = $"{excel.NameOf(itemId)}: 보관 요청이 거절되었습니다.";
        }

        index++;
        Status = $"추억의 보관함 처리 중 {index}/{queue.Count} · 보관 {Success} · 제외 {Skipped} · 실패 {Failed}";
        nextActionAt = now + Math.Clamp(config.ArmoryStoreIntervalMs, 300, 3000);
    }

    private void TickSell()
    {
        var now = Environment.TickCount64;

        if (!ShopReadyToSell())
        {
            if (pendingSaleItemId == 0)
            {
                Stop("상점 창이 닫혀 판매를 중지했습니다.");
                return;
            }
        }

        if (pendingSaleItemId != 0)
        {
            if (!FindBagItem(pendingSaleItemId, out _, out _))
            {
                Success++;
                LastItemStatus = $"{pendingSaleItemName}: 판매 완료.";
                pendingSaleItemId = 0;
                pendingSaleItemName = string.Empty;
                index++;
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
                index++;
                nextActionAt = now + Math.Clamp(config.VendorSellIntervalMs, 400, 3000);
            }

            return;
        }

        if (now < nextActionAt)
            return;

        if (index >= queue.Count)
        {
            Mode = FinalActionMode.None;
            Status = $"상점 판매 완료: 판매 {Success} · 제외 {Skipped} · 실패 {Failed}";
            return;
        }

        var itemId = queue[index];
        if (!FindBagItem(itemId, out var bagType, out var bagSlot))
        {
            var inventory = InventoryManager.Instance();
            if (inventory == null)
            {
                Stop("인벤토리 데이터를 읽을 수 없어 판매를 중지했습니다.");
                return;
            }

            if (!FindUniqueArmoryItem(inventory, itemId, out var srcType, out var srcSlot, out _))
            {
                SkipCurrent($"{excel.NameOf(itemId)}: 장비함/인벤토리에서 대상을 찾지 못했습니다.", now);
                return;
            }

            if (!FindFirstEmptyBagSlot(inventory, out var dstType, out var dstSlot))
            {
                Stop("가방 빈칸이 없어 판매 준비를 중지했습니다.");
                return;
            }

            var rc = inventory->MoveItemSlot(srcType, (ushort)srcSlot, dstType, (ushort)dstSlot, true);
            if (rc < 0)
            {
                Failed++;
                LastItemStatus = $"{excel.NameOf(itemId)}: 판매 전 인벤토리 이동에 실패했습니다. (결과 {rc})";
                index++;
            }
            else
            {
                LastItemStatus = $"{excel.NameOf(itemId)}: 판매를 위해 인벤토리로 이동했습니다.";
            }

            nextActionAt = now + Math.Clamp(config.ArmoryMoveIntervalMs, 300, 3000);
            return;
        }

        if (TrySell(bagType, (short)bagSlot))
        {
            pendingSaleItemId = itemId;
            pendingSaleItemName = excel.NameOf(itemId);
            pendingSaleStartedAt = now;
            LastItemStatus = $"{pendingSaleItemName}: 판매 요청을 보냈습니다.";
        }
        else
        {
            Failed++;
            LastItemStatus = $"{excel.NameOf(itemId)}: 판매 메뉴를 찾지 못했거나 판매할 수 없습니다.";
            index++;
            nextActionAt = now + Math.Clamp(config.VendorSellIntervalMs, 400, 3000);
        }

        Status = $"상점 판매 중 {index}/{queue.Count} · 판매 {Success} · 제외 {Skipped} · 실패 {Failed}";
    }

    private void TickOutfit()
    {
        var now = Environment.TickCount64;
        if (now < nextActionAt)
            return;

        var mirage = MirageManager.Instance();
        if (mirage == null || !mirage->PrismBoxLoaded)
        {
            Stop("환상의 옷장이 닫혀 세트화를 중지했습니다.");
            return;
        }

        if (outfitQueue.Count == 0)
        {
            Mode = FinalActionMode.None;
            Status = $"세트화 완료: 성공 {Success} · 제외 {Skipped} · 실패 {Failed}";
            return;
        }

        var batch = outfitQueue.Dequeue();
        var inventory = InventoryManager.Instance();
        if (inventory == null)
        {
            Stop("인벤토리 데이터를 읽을 수 없어 세트화를 중지했습니다.");
            return;
        }

        Span<InventoryType> containers = stackalloc InventoryType[OutfitSlotCount];
        Span<ushort> slots = stackalloc ushort[OutfitSlotCount];
        containers.Fill(InventoryType.Invalid);
        slots.Clear();

        var filled = 0;
        var selectedSet = batch.PieceIds.ToHashSet();

        for (var slotIndex = 0; slotIndex < batch.Set.ItemIds.Count && slotIndex < OutfitSlotCount; slotIndex++)
        {
            var requiredId = batch.Set.ItemIds[slotIndex];
            if (requiredId == 0 || !selectedSet.Contains(requiredId))
                continue;

            if (!TryFindPhysicalItem(inventory, requiredId, out var type, out var slot, out _))
                continue;

            containers[slotIndex] = type;
            slots[slotIndex] = (ushort)slot;
            filled++;
        }

        if (filled < 2)
        {
            Skipped++;
            OutfitProcessed++;
            LastItemStatus = $"{batch.Set.Name}: 저장 가능한 체크 항목이 2개 미만이라 건너뜁니다.";
            nextActionAt = now + Math.Clamp(config.ArmoryStoreIntervalMs, 300, 3000);
            return;
        }

        var existingIndex = FindExistingOutfitIndex(mirage, batch.Set.RowId);

        bool sent;
        fixed (InventoryType* containerPtr = containers)
        fixed (ushort* slotPtr = slots)
        {
            sent = existingIndex >= 0
                ? mirage->StoreExistingOutfit((uint)existingIndex, containerPtr, slotPtr)
                : mirage->StoreNewOutfit(batch.Set.RowId, containerPtr, slotPtr);
        }

        if (sent)
        {
            Success++;
            LastItemStatus = $"{batch.Set.Name}: {filled}개 장비를 세트화하여 환상의 옷장에 저장 요청했습니다.";
        }
        else
        {
            Failed++;
            LastItemStatus = $"{batch.Set.Name}: 세트화 저장 요청이 거절되었습니다.";
        }

        OutfitProcessed++;
        Status = $"세트화 중 {OutfitProcessed}/{OutfitTotal} · 성공 {Success} · 제외 {Skipped} · 실패 {Failed}";
        nextActionAt = now + Math.Clamp(config.RestoreIntervalMs, 500, 3000);
    }

    private int FindExistingOutfitIndex(MirageManager* mirage, uint setRowId)
    {
        for (var i = 0; i < mirage->PrismBoxItemIds.Length; i++)
        {
            var raw = mirage->PrismBoxItemIds[i];
            if (raw == 0)
                continue;

            var baseId = raw >= 1_000_000 ? raw % 1_000_000 : raw;
            if (baseId == setRowId)
                return i;
        }

        return -1;
    }

    private void BuildOutfitIndex()
    {
        var sheet = data.GetExcelSheet<RawRow>(name: "MirageStoreSetItem");
        if (sheet == null)
            return;

        foreach (var row in sheet)
        {
            if (row.RowId == 0)
                continue;

            var itemIds = new List<uint>(OutfitSlotCount);
            for (var i = 0; i < OutfitSlotCount; i++)
            {
                uint itemId;
                try { itemId = (uint)row.ReadColumn(2 + i); }
                catch { itemId = 0; }

                if (itemId != 0)
                    itemIds.Add(itemId);
            }

            if (itemIds.Count < 2)
                continue;

            var set = new OutfitSetInfo(row.RowId, excel.NameOf(row.RowId), itemIds);
            outfits[row.RowId] = set;

            foreach (var itemId in itemIds)
            {
                if (!outfitIdsByPiece.TryGetValue(itemId, out var list))
                {
                    list = [];
                    outfitIdsByPiece[itemId] = list;
                }
                list.Add(row.RowId);
            }
        }
    }

    private static bool TryFindPhysicalItem(
        InventoryManager* inventory,
        uint itemId,
        out InventoryType type,
        out int slotIndex,
        out InventoryItem* found)
    {
        if (FindBagItem(itemId, out type, out slotIndex))
        {
            found = inventory->GetInventoryContainer(type)->GetInventorySlot(slotIndex);
            return found != null;
        }

        return FindUniqueArmoryItem(inventory, itemId, out type, out slotIndex, out found);
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

    private void SkipCurrent(string message, long now)
    {
        Skipped++;
        LastItemStatus = message;
        index++;
        nextActionAt = now + Math.Clamp(config.ArmoryStoreIntervalMs, 300, 3000);
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

    private sealed record OutfitSetInfo(uint RowId, string Name, IReadOnlyList<uint> ItemIds);
    private sealed record OutfitBatch(OutfitSetInfo Set, IReadOnlyList<uint> PieceIds);
}
