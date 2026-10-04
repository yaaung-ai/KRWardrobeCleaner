using FFXIVClientStructs.FFXIV.Client.Game;

namespace KRWardrobeCleaner;

public sealed unsafe class GlamourStateCache
{
    private readonly Dictionary<uint, bool> plateDyedByItem = [];
    private readonly Dictionary<uint, bool> dresserDyedByItem = [];
    private long nextObserveAt;

    public bool HasPlateData { get; private set; }
    public bool HasDresserData { get; private set; }
    public DateTimeOffset? PlateCapturedAt { get; private set; }
    public DateTimeOffset? DresserCapturedAt { get; private set; }
    public int PlateItemCount => plateDyedByItem.Count;
    public int DresserItemCount => dresserDyedByItem.Count;

    public void Observe()
    {
        var now = Environment.TickCount64;
        if (now < nextObserveAt)
            return;
        nextObserveAt = now + 250;

        var manager = MirageManager.Instance();
        if (manager == null)
            return;

        if (!manager->PrismBoxRequested &&
            !manager->PrismBoxLoaded &&
            !manager->GlamourPlatesRequested &&
            !manager->GlamourPlatesLoaded)
        {
            Clear();
            return;
        }

        if (manager->GlamourPlatesLoaded)
            CapturePlates(manager);

        if (manager->PrismBoxLoaded)
            CaptureDresser(manager);
    }

    public bool TryGetUsedOnPlates(out HashSet<uint> itemIds)
    {
        Observe();
        if (!HasPlateData)
        {
            itemIds = [];
            return false;
        }

        itemIds = [.. plateDyedByItem.Keys];
        return true;
    }

    public bool TryGetPlateState(uint itemId, out bool used, out bool dyed)
    {
        Observe();
        used = plateDyedByItem.TryGetValue(itemId, out dyed);
        if (!used)
            dyed = false;
        return HasPlateData;
    }

    public bool TryGetDresserDyed(uint itemId, out bool dyed)
    {
        Observe();
        if (!HasDresserData)
        {
            dyed = false;
            return false;
        }

        dyed = dresserDyedByItem.TryGetValue(itemId, out var value) && value;
        return true;
    }

    private void CapturePlates(MirageManager* manager)
    {
        plateDyedByItem.Clear();
        foreach (var plate in manager->GlamourPlates)
        {
            for (var slot = 0; slot < plate.ItemIds.Length; slot++)
            {
                var itemId = NormalizeItemId(plate.ItemIds[slot]);
                if (itemId == 0)
                    continue;

                var dyed = plate.Stain0Ids[slot] != 0 || plate.Stain1Ids[slot] != 0;
                plateDyedByItem[itemId] = plateDyedByItem.TryGetValue(itemId, out var existing)
                    ? existing || dyed
                    : dyed;
            }
        }

        HasPlateData = true;
        PlateCapturedAt = DateTimeOffset.Now;
    }

    private void CaptureDresser(MirageManager* manager)
    {
        dresserDyedByItem.Clear();
        for (var i = 0; i < manager->PrismBoxItemIds.Length; i++)
        {
            var itemId = NormalizeItemId(manager->PrismBoxItemIds[i]);
            if (itemId == 0)
                continue;

            var dyed = manager->PrismBoxStain0Ids[i] != 0 || manager->PrismBoxStain1Ids[i] != 0;
            dresserDyedByItem[itemId] = dresserDyedByItem.TryGetValue(itemId, out var existing)
                ? existing || dyed
                : dyed;
        }

        HasDresserData = true;
        DresserCapturedAt = DateTimeOffset.Now;
    }

    private void Clear()
    {
        plateDyedByItem.Clear();
        dresserDyedByItem.Clear();
        HasPlateData = false;
        HasDresserData = false;
        PlateCapturedAt = null;
        DresserCapturedAt = null;
    }

    private static uint NormalizeItemId(uint id)
        => id >= 1_000_000 ? id % 1_000_000 : id;
}
