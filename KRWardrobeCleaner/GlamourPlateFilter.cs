using FFXIVClientStructs.FFXIV.Client.Game;

namespace KRWardrobeCleaner;

public sealed record DresserCleanupItem(
    Candidate Candidate,
    bool UsedOnPlate,
    bool SourceDyed,
    bool AllowDyedSource);

public sealed record PlateFilterResult(
    List<DresserCleanupItem> Eligible,
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

        var eligible = new List<DresserCleanupItem>();
        var notUsed = 0;
        var usedUndyed = 0;
        var protectedDyed = 0;

        foreach (var candidate in candidates)
        {
            var used = false;
            var plateDyed = false;

            foreach (var plate in manager->GlamourPlates)
            {
                for (var slot = 0; slot < plate.ItemIds.Length; slot++)
                {
                    var itemId = plate.ItemIds[slot];
                    if (itemId == 0 || NormalizeItemId(itemId) != candidate.ItemId)
                        continue;

                    used = true;
                    if (plate.Stain0Ids[slot] != 0 || plate.Stain1Ids[slot] != 0)
                        plateDyed = true;
                }
            }

            var sourceDyed = false;
            if (TryFindDresserSlot(manager, candidate.ItemId, out var dresserSlot))
                sourceDyed = manager->PrismBoxStain0Ids[dresserSlot] != 0 || manager->PrismBoxStain1Ids[dresserSlot] != 0;

            // 투영세트에서 사용 중이라면, 투영세트 자체의 염색뿐 아니라 환상의 옷장 원본의
            // 염색도 실제 외형에 영향을 줄 수 있으므로 둘 중 하나라도 염색이면 보호한다.
            if (used && (plateDyed || sourceDyed))
            {
                protectedDyed++;
                continue;
            }

            // 사용하지 않는 아이템은 사용자의 조건상 정리 대상이다. 원본 염색이 있다면
            // 추억의 보관함 이동 과정에서 색이 사라질 수 있음을 UI에 표시한다.
            eligible.Add(new(
                candidate,
                UsedOnPlate: used,
                SourceDyed: sourceDyed,
                AllowDyedSource: !used));

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
