using FFXIVClientStructs.FFXIV.Client.Game;

namespace KRWardrobeCleaner;

public sealed record RestoreTestResult(bool Success, string Message, int? Slot = null);

public sealed unsafe class DresserRestoreTester
{
    public RestoreTestResult RestoreOne(Candidate candidate)
    {
        var manager = MirageManager.Instance();
        if (manager == null)
            return new(false, "MirageManager is unavailable.");

        var match = -1;
        var matches = 0;

        for (var i = 0; i < manager->PrismBoxItemIds.Length; i++)
        {
            var raw = manager->PrismBoxItemIds[i];
            if (raw == 0)
                continue;

            var baseId = raw >= 1_000_000 ? raw % 1_000_000 : raw;
            if (baseId != candidate.ItemId)
                continue;

            match = i;
            matches++;
        }

        if (matches == 0)
            return new(false, "Selected item was not found in the live Glamour Dresser data. Open the dresser and try again.");

        if (matches > 1)
            return new(false, $"Selected item matched {matches} live dresser slots. Refusing because the target is ambiguous.");

        var stain0 = manager->PrismBoxStain0Ids[match];
        var stain1 = manager->PrismBoxStain1Ids[match];
        if (stain0 != 0 || stain1 != 0)
            return new(false, $"Selected item is dyed (stains {stain0}/{stain1}). v0.2 refuses to restore dyed items.");

        var restored = manager->RestorePrismBoxItem((uint)match);
        return restored
            ? new(true, $"Restore request accepted for {candidate.Name} from dresser slot {match + 1}.", match)
            : new(false, $"RestorePrismBoxItem returned false for {candidate.Name} at dresser slot {match + 1}.", match);
    }
}
