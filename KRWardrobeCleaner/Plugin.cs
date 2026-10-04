using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace KRWardrobeCleaner;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager Commands { get; private set; } = null!;
    [PluginService] internal static IChatGui Chat { get; private set; } = null!;
    [PluginService] internal static IDataManager Data { get; private set; } = null!;
    [PluginService] internal static IAgentLifecycle AgentLifecycle { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;

    private readonly Configuration config;
    private readonly DungeonDripSnapshot snapshots;
    private readonly CalibrationRecorder recorder;
    private readonly DiagnosticExporter exporter;
    private readonly DresserRestoreTester restoreTester;
    private readonly WardrobeCleanupService cleanup;
    private readonly ArmoryArmoireService armoryPreclean;

    private ScanResult scan = new();
    private bool windowOpen = true;
    private int selectedIndex = -1;
    private string status = "1단계로 추억의 보관함을 열어 장비칸을 정리한 뒤, 2단계에서 환상의 옷장을 정리하세요.";

    public Plugin()
    {
        config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        var excel = new ExcelIndex(Data);
        snapshots = new DungeonDripSnapshot(excel);
        recorder = new CalibrationRecorder(AgentLifecycle, Log);
        recorder.SetEnabled(config.CalibrationMode);
        exporter = new DiagnosticExporter(PluginInterface);
        restoreTester = new DresserRestoreTester(GameGui);
        cleanup = new WardrobeCleanupService(restoreTester, Commands, config, SaveConfig);
        armoryPreclean = new ArmoryArmoireService(excel, config, SaveConfig);

        Commands.AddHandler("/kwc", new CommandInfo(OnCommand)
        {
            HelpMessage = "한국판 옷장 정리기를 엽니다. /kwc 검색, /kwc 중지, /kwc 보관함, /kwc 진단",
        });

        PluginInterface.UiBuilder.Draw += Draw;
        PluginInterface.UiBuilder.OpenMainUi += Open;
        PluginInterface.UiBuilder.OpenConfigUi += Open;
        Framework.Update += OnFrameworkUpdate;

        Rescan();
    }

    public void Dispose()
    {
        armoryPreclean.Stop("플러그인이 종료되어 장비칸 정리를 중지했습니다.");
        cleanup.Stop("플러그인이 종료되어 환상의 옷장 정리를 중지했습니다.");
        Framework.Update -= OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw -= Draw;
        PluginInterface.UiBuilder.OpenMainUi -= Open;
        PluginInterface.UiBuilder.OpenConfigUi -= Open;
        Commands.RemoveHandler("/kwc");
        recorder.Dispose();
    }

    private void Open() => windowOpen = true;

    private void OnFrameworkUpdate(IFramework framework)
    {
        armoryPreclean.Tick();
        cleanup.Tick();

        if (cleanup.ConsumeRescanRequest())
        {
            status = cleanup.Status;
            Rescan(updateStatus: false);
        }
    }

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "scan":
            case "검색":
                Rescan();
                windowOpen = true;
                break;
            case "stop":
            case "중지":
                armoryPreclean.Stop();
                cleanup.Stop();
                status = armoryPreclean.IsRunning ? armoryPreclean.Status : cleanup.Status;
                windowOpen = true;
                break;
            case "armoire":
            case "보관함":
                cleanup.DepositNow();
                status = cleanup.Status;
                windowOpen = true;
                break;
            case "calibrate":
            case "진단":
                config.CalibrationMode = !config.CalibrationMode;
                recorder.SetEnabled(config.CalibrationMode);
                SaveConfig();
                status = $"진단 이벤트 기록: {(config.CalibrationMode ? "켜짐" : "꺼짐")}";
                windowOpen = true;
                break;
            default:
                windowOpen = !windowOpen;
                break;
        }
    }

    private void Rescan(bool updateStatus = true)
    {
        try
        {
            if (updateStatus)
                status = "Dungeon Drip 스냅샷과 추억의 보관함 가능 목록을 확인하는 중입니다...";

            scan = snapshots.Scan(config.OwnershipSnapshotPath);
            if (scan.SnapshotPath is not null)
                config.OwnershipSnapshotPath = scan.SnapshotPath;

            selectedIndex = scan.Candidates.Count > 0 ? Math.Clamp(selectedIndex, 0, scan.Candidates.Count - 1) : -1;
            SaveConfig();

            if (updateStatus)
                status = $"검색 완료: 환상의 옷장에서 추억의 보관함으로 옮길 수 있는 후보 {scan.Candidates.Count}개.";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[KWC] 검색 실패");
            status = $"검색 실패: {ex.GetType().Name}: {ex.Message}";
        }
    }

    private void SaveConfig() => PluginInterface.SavePluginConfig(config);

    private void Draw()
    {
        if (!windowOpen) return;

        ImGui.SetNextWindowSize(new Vector2(820, 800), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("한국판 옷장 정리기 v0.4###KRWardrobeCleaner", ref windowOpen))
        {
            ImGui.End();
            return;
        }

        ImGui.TextWrapped("장비칸에 이미 있는 보관 가능 장비를 먼저 추억의 보관함에 넣고, 그 다음 환상의 옷장에서 남은 후보를 복원해 정리합니다.");
        ImGui.Separator();

        DrawArmoryPreclean();
        ImGui.Separator();
        DrawDresserCleanup();
        ImGui.Separator();
        DrawOptions();
        ImGui.Separator();
        DrawCandidates();
        DrawDiagnostics();

        ImGui.End();
    }

    private void DrawArmoryPreclean()
    {
        ImGui.TextUnformatted("1단계 · 장비칸 → 추억의 보관함");
        ImGui.TextWrapped("추억의 보관함을 직접 연 상태에서 실행합니다. 저장된 장비 세트에 포함된 장비, 염색/마테리아/투영 등 개별 상태가 있는 장비, 중복 장비는 자동으로 건너뜁니다.");

        if (!armoryPreclean.IsRunning)
        {
            if (ImGui.Button("장비칸 후보 검색"))
            {
                armoryPreclean.Scan();
                status = armoryPreclean.Status;
            }

            ImGui.SameLine();
            if (ImGui.Button($"장비칸 안전 후보 보관 시작 ({armoryPreclean.Candidates.Count}개)"))
            {
                if (!cleanup.IsRunning)
                {
                    armoryPreclean.Start();
                    status = armoryPreclean.Status;
                }
                else
                {
                    status = "환상의 옷장 정리가 진행 중이라 장비칸 정리를 시작할 수 없습니다.";
                }
            }
        }
        else
        {
            ImGui.TextUnformatted($"진행: {armoryPreclean.Processed}/{armoryPreclean.Total} · 보관 {armoryPreclean.Stored} · 제외 {armoryPreclean.Skipped} · 실패 {armoryPreclean.Failed}");
            if (ImGui.Button("장비칸 정리 중지"))
            {
                armoryPreclean.Stop();
                status = armoryPreclean.Status;
            }
        }

        ImGui.TextWrapped($"상태: {armoryPreclean.Status}");
        if (!string.IsNullOrWhiteSpace(armoryPreclean.LastItemStatus))
            ImGui.TextWrapped($"최근 처리: {armoryPreclean.LastItemStatus}");

        ImGui.TextWrapped(
            $"검색 제외: 제작직 {armoryPreclean.SkippedCrafting} · 장비 세트 {armoryPreclean.SkippedGearset} · 개별 상태 있음 {armoryPreclean.SkippedModified} · 중복 {armoryPreclean.SkippedDuplicate}");
    }

    private void DrawDresserCleanup()
    {
        ImGui.TextUnformatted("2단계 · 환상의 옷장 → 추억의 보관함");
        ImGui.TextWrapped("1단계가 끝난 뒤 환상의 옷장을 열고 실행합니다. 추억의 보관함에 넣을 수 있는 환상의 옷장 아이템을 순서대로 복원합니다.");

        if (ImGui.Button("후보 다시 검색"))
            Rescan();

        ImGui.SameLine();
        if (ImGui.Button("Dungeon Drip 새로고침"))
        {
            Commands.ProcessCommand("/dungeondrip refresh");
            status = "Dungeon Drip 새로고침을 요청했습니다.";
        }

        ImGui.SameLine();
        if (ImGui.Button("가방의 보관 가능 아이템 넣기"))
        {
            cleanup.DepositNow();
            status = cleanup.Status;
        }

        var displayedStatus = cleanup.IsRunning || cleanup.IsWaitingForAutoDeposit ? cleanup.Status : status;
        ImGui.TextWrapped($"상태: {displayedStatus}");
        if (!string.IsNullOrWhiteSpace(cleanup.LastItemStatus))
            ImGui.TextWrapped($"최근 처리: {cleanup.LastItemStatus}");

        var freeSlots = restoreTester.GetFreeBagSlots();
        ImGui.TextUnformatted(
            $"환상의 옷장 사용: {scan.DresserCount} | 보관함 기록: {scan.ArmoireCount} | 정리 후보: {scan.Candidates.Count} | 가방 빈칸: {(freeSlots < 0 ? "확인 불가" : freeSlots)}");

        foreach (var note in scan.Notes)
            ImGui.TextWrapped("안내: " + note);

        if (!cleanup.IsRunning)
        {
            if (ImGui.Button($"환상의 옷장 안전 후보 전체 복원 ({scan.Candidates.Count}개)"))
            {
                if (!armoryPreclean.IsRunning)
                {
                    cleanup.Start(scan.Candidates);
                    status = cleanup.Status;
                }
                else
                {
                    status = "장비칸 정리가 진행 중이라 환상의 옷장 정리를 시작할 수 없습니다.";
                }
            }
        }
        else
        {
            ImGui.TextUnformatted($"진행: {cleanup.Processed}/{cleanup.Total} · 복원 {cleanup.Restored} · 제외 {cleanup.Skipped} · 실패 {cleanup.Failed}");
            if (ImGui.Button("환상의 옷장 정리 중지"))
            {
                cleanup.Stop();
                status = cleanup.Status;
            }
        }

        ImGui.SameLine();
        if (selectedIndex >= 0 && selectedIndex < scan.Candidates.Count && !cleanup.IsRunning)
        {
            var selected = scan.Candidates[selectedIndex];
            if (ImGui.Button($"선택 항목 1개만 복원##one{selected.ItemId}"))
            {
                var result = restoreTester.RestoreOne(selected);
                status = result.Message;
                if (result.Success)
                {
                    Chat.Print("[옷장 정리기] " + result.Message);
                    Commands.ProcessCommand("/dungeondrip refresh");
                }
                else
                {
                    Chat.PrintError("[옷장 정리기] " + result.Message);
                }
            }
        }
    }

    private void DrawOptions()
    {
        if (!ImGui.CollapsingHeader("정리 옵션", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        var includeCrafting = config.IncludeCraftingGearInArmoryPreclean;
        if (ImGui.Checkbox("1단계에서 제작직 전용 장비도 추억의 보관함에 넣기", ref includeCrafting))
        {
            armoryPreclean.UpdateSettings(includeCrafting, config.ArmoryStoreIntervalMs);
            armoryPreclean.Scan();
        }
        ImGui.TextWrapped("꺼짐(기본값): 목수·대장장이·갑주제작사·보석공예가·가죽공예가·재봉사·연금술사·요리사 전용 장비는 장비칸에 남겨둡니다. 전 직업 공용 의상은 제작직 전용 장비로 보지 않습니다.");

        var armoryInterval = config.ArmoryStoreIntervalMs;
        if (ImGui.SliderInt("장비칸 보관 간격 (ms)", ref armoryInterval, 300, 3000))
            armoryPreclean.UpdateSettings(config.IncludeCraftingGearInArmoryPreclean, armoryInterval);

        var interval = config.RestoreIntervalMs;
        if (ImGui.SliderInt("환상의 옷장 복원 간격 (ms)", ref interval, 300, 3000))
            cleanup.UpdateSettings(interval, config.ReserveFreeSlots, config.AutoDepositToArmoire);

        var reserve = config.ReserveFreeSlots;
        if (ImGui.SliderInt("복원 중 남겨둘 가방 빈칸", ref reserve, 0, 30))
            cleanup.UpdateSettings(config.RestoreIntervalMs, reserve, config.AutoDepositToArmoire);

        var autoDeposit = config.AutoDepositToArmoire;
        if (ImGui.Checkbox("2단계 복원 완료 후 AutoRetainer로 추억의 보관함에 자동 보관", ref autoDeposit))
            cleanup.UpdateSettings(config.RestoreIntervalMs, config.ReserveFreeSlots, autoDeposit);

        ImGui.TextWrapped("자동 보관을 켜면 복원이 끝난 뒤 환상의 옷장을 닫았을 때 /autoretainer armoire 명령을 실행합니다.");

        var showIds = config.ShowIds;
        if (ImGui.Checkbox("목록에 아이템 ID 표시", ref showIds))
        {
            config.ShowIds = showIds;
            SaveConfig();
        }

        ImGui.TextWrapped("공통 안전 규칙: 염색/마테리아/투영 등 개별 상태가 있는 장비와 중복 대상은 자동 보관하지 않습니다. 1단계에서는 저장된 장비 세트에 포함된 아이템도 항상 제외합니다.");
    }

    private void DrawCandidates()
    {
        ImGui.TextUnformatted("환상의 옷장 보관 후보");
        ImGui.BeginChild("Candidates", new Vector2(0, 260), true);

        for (var i = 0; i < scan.Candidates.Count; i++)
        {
            var candidate = scan.Candidates[i];
            var label = config.ShowIds ? $"{candidate.Name} [{candidate.ItemId}]" : candidate.Name;
            if (ImGui.Selectable(label + $"##{candidate.ItemId}", selectedIndex == i))
                selectedIndex = i;
        }

        ImGui.EndChild();
    }

    private void DrawDiagnostics()
    {
        if (!ImGui.CollapsingHeader("진단 도구"))
            return;

        var cal = config.CalibrationMode;
        if (ImGui.Checkbox("게임 UI 이벤트 기록", ref cal))
        {
            config.CalibrationMode = cal;
            recorder.SetEnabled(cal);
            SaveConfig();
        }

        if (selectedIndex >= 0 && selectedIndex < scan.Candidates.Count)
        {
            var candidate = scan.Candidates[selectedIndex];
            if (ImGui.Button($"선택 항목 진단 준비: {candidate.Name}"))
            {
                recorder.Arm(candidate);
                config.CalibrationMode = true;
                recorder.SetEnabled(true);
                SaveConfig();
                status = $"{candidate.Name} 진단 준비 완료. 이 아이템을 수동 복원한 뒤 진단 파일을 내보내세요.";
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("진단 파일 내보내기"))
        {
            try
            {
                var path = exporter.Export(scan, recorder.Traces);
                status = "진단 파일 저장 완료: " + path;
                Chat.Print("[옷장 정리기] 진단 파일 저장: " + path);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[KWC] 진단 파일 저장 실패");
                status = "진단 파일 저장 실패: " + ex.Message;
            }
        }

        ImGui.TextUnformatted($"기록된 UI 이벤트: {recorder.Traces.Count}");

        if (ImGui.CollapsingHeader("스냅샷 파서 경로"))
        {
            ImGui.TextUnformatted("환상의 옷장 경로:");
            foreach (var p in scan.DresserPaths) ImGui.BulletText(p);
            ImGui.TextUnformatted("추억의 보관함 경로:");
            foreach (var p in scan.ArmoirePaths) ImGui.BulletText(p);
        }
    }
}
