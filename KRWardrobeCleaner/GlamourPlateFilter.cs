using FFXIVClientStructs.FFXIV.Client.Game;

namespace KRWardrobeCleaner;

public sealed record PlateFilterResult(
    List<Candidate> Eligible,
    int NotUsedOnPlates,
    int UsedUndyed,
    int ProtectedDyed,
    bool PlateDataReady);

public sealed unsafe class GlamourPlateFilter
{
    public PlateFilterResult Filter(IEnumerable<Candidate> candidates)
    {
        var manager = MirageManager.Instance();
        if (manager == null || !manager->PrismBoxLoaded || !manager->GlamourPlatesLoaded)
            return new([], 0, 0, 0, false);

        var eligible = new List<Candidate>();
        var notUsed = 0;
        var usedUndyed = 0;
        var protectedDyed = 0;

        foreach (var candidate in candidates)
        {
            var used = false;
            var hasDyedUse = false;

            foreach (var plate in manager->GlamourPlates)
            {
                for (var slot = 0; slot < plate.ItemIds.Length; slot++)
                {
                    var itemId = plate.ItemIds[slot];
                    if (itemId == 0 || NormalizeItemId(itemId) != candidate.ItemId)
                        continue;

                    used = true;
                    if (plate.Stain0Ids[slot] != 0 || plate.Stain1Ids[slot] != 0)
                        hasDyedUse = true;
                }
            }

            // A dresser item's own stain can be the effective appearance even when a plate has
            // no additional dye stored. If the item is used by a plate, treat the source stain
            // as protected too.
            if (used && !hasDyedUse && TryFindDresserSlot(manager, candidate.ItemId, out var dresserSlot))
            {
                if (manager->PrismBoxStain0Ids[dresserSlot] != 0 || manager->PrismBoxStain1Ids[dresserSlot] != 0)
                    hasDyedUse = true;
            }

            if (hasDyedUse)
            {
                protectedDyed++;
                continue;
            }

            eligible.Add(candidate);
            if (used) usedUndyed++;
            else notUsed++;
        }

        return new(eligible, notUsed, usedUndyed, protectedDyed, true);
    }

    private static uint NormalizeItemId(uint id)
        => id >= 1_000_000 ? id % 1_000_000 : id;

    private static bool TryFindDresserSlot(MirageManager* manager, uint itemId, out int slot)
    {
        for (var i = 0; i < manager->PrismBoxItemIds.Length; i++)
        {
            var raw = manager->PrismBoxItemIds[i];
            if (raw != 0 && NormalizeItemId(raw) == itemId)
            {
                slot = i;
                return true;
            }
        }

        slot = -1;
        return false;
    }
}
