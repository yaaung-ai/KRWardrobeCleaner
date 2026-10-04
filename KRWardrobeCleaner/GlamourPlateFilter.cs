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

public sealed class GlamourPlateFilter
{
    private readonly GlamourStateCache cache;

    public GlamourPlateFilter(GlamourStateCache cache) => this.cache = cache;

    public PlateFilterResult Filter(IEnumerable<Candidate> candidates)
    {
        cache.Observe();
        if (!cache.HasPlateData || !cache.HasDresserData)
            return new([], 0, 0, 0, false);

        var eligible = new List<DresserCleanupItem>();
        var notUsed = 0;
        var usedUndyed = 0;
        var protectedDyed = 0;

        foreach (var candidate in candidates)
        {
            cache.TryGetPlateState(candidate.ItemId, out var used, out var plateDyed);
            cache.TryGetDresserDyed(candidate.ItemId, out var sourceDyed);

            if (used && (plateDyed || sourceDyed))
            {
                protectedDyed++;
                continue;
            }

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
}
