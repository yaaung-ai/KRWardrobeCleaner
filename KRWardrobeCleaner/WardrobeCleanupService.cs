using Dalamud.Game.Command;
using Dalamud.Plugin.Services;

namespace KRWardrobeCleaner;

public sealed class WardrobeCleanupService
{
    private readonly DresserRestoreTester restoreTester;
    private readonly ICommandManager commands;
    private readonly Configuration config;
    private readonly Action saveConfig;

    private readonly List<Candidate> queue = [];
    private int index;
    private long nextActionAt;
    private long rescanAt;
    private long dresserClosedAt;
    private bool pendingAutoDeposit;

    public bool IsRunning { get; private set; }
    public bool IsWaitingForAutoDeposit => pendingAutoDeposit;
    public int Total => queue.Count;
    public int Processed => index;
    public int Restored { get; private set; }
    public int Skipped { get; private set; }
    public int Failed { get; private set; }
    public string Status { get; private set; } = "대기 중";
    public string? LastItemStatus { get; private set; }
    public bool RescanRequested { get; private set; }

    public WardrobeCleanupService(
        DresserRestoreTester restoreTester,
        ICommandManager commands,
        Configuration config,
        Action saveConfig)
    {
        this.restoreTester = restoreTester;
        this.commands = commands;
        this.config = config;
        this.saveConfig = saveConfig;
    }

    public bool Start(IEnumerable<Candidate> candidates)
    {
        if (IsRunning)
            return false;

        queue.Clear();
        queue.AddRange(candidates);
        index = 0;
        Restored = 0;
        Skipped = 0;
        Failed = 0;
        LastItemStatus = null;
        pendingAutoDeposit = false;
        rescanAt = 0;
        RescanRequested = false;
        dresserClosedAt = 0;

        if (queue.Count == 0)
        {
            Status = "정리할 추억의 보관함 후보가 없습니다.";
            return false;
        }

        if (!restoreTester.IsDresserOpen())
        {
            Status = "환상의 옷장을 연 상태에서 정리를 시작해 주세요.";
            return false;
        }

        var free = restoreTester.GetFreeBagSlots();
        if (free < 0)
        {
            Status = "가방 빈칸을 확인할 수 없습니다.";
            return false;
        }

        if (free <= config.ReserveFreeSlots)
        {
            Status = $"가방 빈칸이 {free}칸뿐입니다. 최소 {config.ReserveFreeSlots + 1}칸이 필요합니다.";
            return false;
        }

        IsRunning = true;
        nextActionAt = 0;
        Status = $"정리 시작: {queue.Count}개 후보를 순서대로 확인합니다.";
        return true;
    }

    public void Stop(string reason = "사용자가 정리를 중지했습니다.")
    {
        if (!IsRunning)
            return;

        IsRunning = false;
        Status = reason;
    }

    public bool DepositNow()
    {
        if (!config.IncludeCraftingGearInArmoryPreclean)
        {
            Status = "제작직 장비 제외가 켜져 있어 AutoRetainer 자동 보관을 실행하지 않습니다. AutoRetainer는 장비칸까지 함께 처리할 수 있습니다.";
            return false;
        }

        if (IsRunning)
        {
            Status = "복원 작업이 끝난 뒤 추억의 보관함 보관을 실행해 주세요.";
            return false;
        }

        if (restoreTester.IsDresserOpen())
        {
            Status = "환상의 옷장을 닫은 뒤 추억의 보관함 보관을 실행해 주세요.";
            return false;
        }

        if (!commands.Commands.ContainsKey("/autoretainer"))
        {
            Status = "AutoRetainer를 찾지 못했습니다. 추억의 보관함에 직접 보관해 주세요.";
            return false;
        }

        var ok = commands.ProcessCommand("/autoretainer armoire");
        Status = ok ? "AutoRetainer에 추억의 보관함 보관 작업을 전달했습니다." : "AutoRetainer가 추억의 보관함 보관 명령을 받지 못했습니다.";
        pendingAutoDeposit = false;
        return ok;
    }

    public void Tick()
    {
        var now = Environment.TickCount64;

        if (IsRunning)
        {
            if (now < nextActionAt)
                return;

            if (!restoreTester.IsDresserOpen())
            {
                Stop("환상의 옷장이 닫혀 정리를 중지했습니다.");
                return;
            }

            if (index >= queue.Count)
            {
                Complete(now);
                return;
            }

            var free = restoreTester.GetFreeBagSlots();
            if (free < 0)
            {
                Stop("가방 빈칸을 확인할 수 없어 정리를 중지했습니다.");
                return;
            }

            if (free <= config.ReserveFreeSlots)
            {
                Stop($"가방 빈칸이 {free}칸 남아 안전을 위해 중지했습니다. 복원된 아이템을 추억의 보관함에 보관한 뒤 다시 시작하세요.");
                return;
            }

            var candidate = queue[index];
            var result = restoreTester.RestoreOne(candidate);
            LastItemStatus = result.Message;

            if (result.Success)
            {
                Restored++;
                index++;
            }
            else if (result.SafeSkip)
            {
                Skipped++;
                index++;
            }
            else if (result.Kind is RestoreResultKind.DresserClosed or RestoreResultKind.DresserNotReady)
            {
                Stop(result.Message);
                return;
            }
            else
            {
                Failed++;
                index++;
            }

            Status = $"정리 중 {index}/{queue.Count} · 복원 {Restored} · 제외 {Skipped} · 실패 {Failed}";
            nextActionAt = now + Math.Clamp(config.RestoreIntervalMs, 300, 3000);
            return;
        }

        if (rescanAt > 0 && now >= rescanAt)
        {
            commands.ProcessCommand("/dungeondrip refresh");
            rescanAt = 0;
            RescanRequested = true;
        }

        if (!pendingAutoDeposit)
            return;

        if (restoreTester.IsDresserOpen())
        {
            dresserClosedAt = 0;
            Status = "복원이 끝났습니다. 환상의 옷장을 닫으면 AutoRetainer 추억의 보관함 보관을 시작합니다.";
            return;
        }

        if (dresserClosedAt == 0)
        {
            dresserClosedAt = now;
            return;
        }

        if (now - dresserClosedAt < 1200)
            return;

        DepositNow();
    }

    private void Complete(long now)
    {
        IsRunning = false;
        Status = $"복원 완료: {Restored}개 · 제외 {Skipped}개 · 실패 {Failed}개";
        rescanAt = now + 1500;
        pendingAutoDeposit = config.AutoDepositToArmoire && config.IncludeCraftingGearInArmoryPreclean && Restored > 0;
        dresserClosedAt = 0;

        if (pendingAutoDeposit)
            Status += " · 환상의 옷장을 닫으면 추억의 보관함 보관을 이어서 실행합니다.";
    }

    public bool ConsumeRescanRequest()
    {
        if (!RescanRequested)
            return false;

        RescanRequested = false;
        return true;
    }

    public void UpdateSettings(int intervalMs, int reserveSlots, bool autoDeposit)
    {
        config.RestoreIntervalMs = Math.Clamp(intervalMs, 300, 3000);
        config.ReserveFreeSlots = Math.Clamp(reserveSlots, 0, 30);
        config.AutoDepositToArmoire = autoDeposit;
        saveConfig();
    }
}
