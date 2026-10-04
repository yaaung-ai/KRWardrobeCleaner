using Dalamud.Game.Command;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace KRWardrobeCleaner;

public sealed record DresserUnusedEntry(
    uint ItemId,
    string Name,
    bool IsDyed);

public sealed unsafe class DresserUnusedService
{
    private readonly ExcelIndex excel;
    private readonly DresserRestoreTester restoreTester;
    private readonly Configuration config;
    private readonly ICommandManager commands;

    private readonly List<DresserUnusedEntry> entries = [];
    private readonly HashSet<uint> selected = [];
    private readonly List<uint> queue = [];
    private int index;
    private long nextActionAt;

    public IReadOnlyList<DresserUnusedEntry> Entries => entries;
    public IReadOnlySet<uint> Selected => selected;
    public bool IsRunning { get; private set; }
    public int Restored { get; private set; }
    public int Skipped { get; private set; }
    public int Failed { get; private set; }
    public string Status { get; private set; } = "환상의 옷장을 연 뒤 4단계 후보를 검색해 주세요.";
    public string? LastItemStatus { get; private set; }

    public DresserUnusedService(
        ExcelIndex excel,
        DresserRestoreTester restoreTester,
        Configuration config,
        ICommandManager commands)
    {
        this.excel = excel;
        this.restoreTester = restoreTester;
        this.config = config;
        this.commands = commands;
    }

    public int Scan(ScanResult scan)
    {
        if (IsRunning)
            return entries.Count;

        entries.Clear();
        selected.Clear();

        var manager = MirageManager.Instance();
        if (manager == null || !manager->PrismBoxLoaded || !manager->GlamourPlatesLoaded)
        {
            Status = "환상의 옷장과 투영세트 데이터를 읽을 수 없습니다. 환상의 옷장을 연 상태에서 다시 시도해 주세요.";
            return 0;
        }

        var usedOnPlates = new HashSet<uint>();
        foreach (var plate in manager->GlamourPlates)
        {
            foreach (var raw in plate.ItemIds)
            {
                var id = NormalizeItemId(raw);
                if (id != 0)
                    usedOnPlates.Add(id);
            }
        }

        foreach (var itemId in scan.DresserDirectItems.OrderBy(excel.NameOf, StringComparer.CurrentCulture))
        {
            if (scan.DresserOutfitPieces.Contains(itemId))
                continue;
            if (usedOnPlates.Contains(itemId))
                continue;

            var dyed = false;
            if (TryFindSingleLiveSlot(manager, itemId, out var slot))
                dyed = manager->PrismBoxStain0Ids[slot] != 0 || manager->PrismBoxStain1Ids[slot] != 0;

            entries.Add(new(itemId, excel.NameOf(itemId), dyed));
        }

        Status = $"4단계 후보 검색 완료: 세트화/투영세트 미사용 직접 보관 아이템 {entries.Count}개.";
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

    public bool Start()
    {
        if (IsRunning)
            return false;

        queue.Clear();
        queue.AddRange(entries.Where(x => selected.Contains(x.ItemId)).Select(x => x.ItemId));

        if (queue.Count == 0)
        {
            Status = "인벤토리로 옮길 항목을 체크해 주세요.";
            return false;
        }

        if (!restoreTester.IsDresserOpen())
        {
            Status = "환상의 옷장을 연 상태에서 실행해 주세요.";
            return false;
        }

        var free = restoreTester.GetFreeBagSlots();
        if (free <= config.ReserveFreeSlots)
        {
            Status = $"가방 빈칸이 {free}칸이라 시작할 수 없습니다. 최소 {config.ReserveFreeSlots + 1}칸이 필요합니다.";
            return false;
        }

        index = 0;
        Restored = 0;
        Skipped = 0;
        Failed = 0;
        LastItemStatus = null;
        nextActionAt = 0;
        IsRunning = true;
        Status = $"체크한 환상의 옷장 아이템 {queue.Count}개를 인벤토리로 옮깁니다.";
        return true;
    }

    public void Stop(string reason = "4단계 복원을 중지했습니다.")
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

        if (index >= queue.Count)
        {
            IsRunning = false;
            Status = $"4단계 완료: 복원 {Restored} · 제외 {Skipped} · 실패 {Failed}";
            commands.ProcessCommand("/dungeondrip refresh");
            return;
        }

        if (!restoreTester.IsDresserOpen())
        {
            Stop("환상의 옷장이 닫혀 4단계 복원을 중지했습니다.");
            return;
        }

        var free = restoreTester.GetFreeBagSlots();
        if (free <= config.ReserveFreeSlots)
        {
            Stop($"가방 빈칸이 {free}칸 남아 안전을 위해 중지했습니다.");
            return;
        }

        var itemId = queue[index];
        var candidate = new Candidate(itemId, excel.NameOf(itemId), "4단계 미사용 환상의 옷장 직접 보관");
        var result = restoreTester.RestoreOne(candidate, allowDyedSource: true);
        LastItemStatus = result.Message;

        if (result.Success)
            Restored++;
        else if (result.SafeSkip)
            Skipped++;
        else
            Failed++;

        index++;
        Status = $"4단계 복원 중 {index}/{queue.Count} · 복원 {Restored} · 제외 {Skipped} · 실패 {Failed}";
        nextActionAt = now + Math.Clamp(config.RestoreIntervalMs, 300, 3000);
    }

    private static uint NormalizeItemId(uint id)
        => id >= 1_000_000 ? id % 1_000_000 : id;

    private static bool TryFindSingleLiveSlot(MirageManager* manager, uint itemId, out int slot)
    {
        slot = -1;
        var matches = 0;

        for (var i = 0; i < manager->PrismBoxItemIds.Length; i++)
        {
            var raw = manager->PrismBoxItemIds[i];
            if (raw == 0 || NormalizeItemId(raw) != itemId)
                continue;

            slot = i;
            matches++;
        }

        return matches == 1;
    }
}
