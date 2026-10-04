using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace KRWardrobeCleaner;

public enum RestoreResultKind
{
    Success,
    DresserClosed,
    DresserNotReady,
    NotFound,
    Ambiguous,
    Dyed,
    Rejected,
}

public sealed record RestoreTestResult(RestoreResultKind Kind, string Message, int? Slot = null)
{
    public bool Success => Kind == RestoreResultKind.Success;
    public bool SafeSkip => Kind is RestoreResultKind.NotFound or RestoreResultKind.Ambiguous or RestoreResultKind.Dyed;
}

public sealed unsafe class DresserRestoreTester
{
    private readonly IGameGui gameGui;

    public DresserRestoreTester(IGameGui gameGui) => this.gameGui = gameGui;

    public bool IsDresserOpen()
    {
        var addon = gameGui.GetAddonByName("MiragePrismPrismBox");
        return addon != null && addon.IsVisible;
    }

    public int GetFreeBagSlots()
    {
        var inventory = InventoryManager.Instance();
        return inventory == null ? -1 : checked((int)inventory->GetEmptySlotsInBag());
    }

    public RestoreTestResult RestoreOne(Candidate candidate, bool allowDyedSource = false)
    {
        if (!IsDresserOpen())
            return new(RestoreResultKind.DresserClosed, "환상의 옷장이 열려 있지 않습니다.");

        var manager = MirageManager.Instance();
        if (manager == null)
            return new(RestoreResultKind.DresserNotReady, "환상의 옷장 데이터를 불러올 수 없습니다.");

        if (!manager->PrismBoxRequested || !manager->PrismBoxLoaded)
            return new(RestoreResultKind.DresserNotReady, "환상의 옷장 데이터가 아직 준비되지 않았습니다.");

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
            return new(RestoreResultKind.NotFound, $"{candidate.Name}: 현재 옷장에서 찾지 못해 건너뜁니다.");

        if (matches > 1)
            return new(RestoreResultKind.Ambiguous, $"{candidate.Name}: 같은 아이템이 {matches}개 있어 안전을 위해 건너뜁니다.");

        var stain0 = manager->PrismBoxStain0Ids[match];
        var stain1 = manager->PrismBoxStain1Ids[match];
        if (!allowDyedSource && (stain0 != 0 || stain1 != 0))
            return new(RestoreResultKind.Dyed, $"{candidate.Name}: 염색된 아이템이라 건너뜁니다. ({stain0}/{stain1})", match);

        var restored = manager->RestorePrismBoxItem((uint)match);
        return restored
            ? new(RestoreResultKind.Success, $"{candidate.Name}: 복원 요청 완료 (옷장 {match + 1}번 칸).", match)
            : new(RestoreResultKind.Rejected, $"{candidate.Name}: 게임이 복원 요청을 받지 않았습니다.", match);
    }
}
