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
    private readonly GlamourPlateFilter plateFilter;
    private readonly ArmoryMoveSellService armoryMoveSell;

    private ScanResult scan = new();
    private PlateFilterResult plateResult = new([], 0, 0, 0, false);
    private bool windowOpen = true;
    private int selectedIndex = -1;
    private string status = "집사님이 정리할 준비를 하고 있습니다.";

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
        plateFilter = new GlamourPlateFilter();
        armoryMoveSell = new ArmoryMoveSellService(excel, config, SaveConfig, Data, GameGui);

        Commands.AddHandler("/kwc", new CommandInfo(OnCommand)
        {
            HelpMessage = "히메짱 옷장 정리기를 엽니다. /kwc 검색, /kwc 중지, /kwc 보관함, /kwc 진단",
        });

        PluginInterface.UiBuilder.Draw += Draw;
        PluginInterface.UiBuilder.OpenMainUi += Open;
        PluginInterface.UiBuilder.OpenConfigUi += Open;
        Framework.Update += OnFrameworkUpdate;

        Rescan();
    }

    public void Dispose()
    {
        armoryPreclean.Stop("플러그인이 종료되어 장비함 정리를 중지했습니다.");
        cleanup.Stop("플러그인이 종료되어 환상의 옷장 정리를 중지했습니다.");
        armoryMoveSell.Stop();

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
        armoryMoveSell.Tick();

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
                armoryMoveSell.Scan();
                windowOpen = true;
                break;

            case "stop":
            case "중지":
                armoryPreclean.Stop();
                cleanup.Stop();
                armoryMoveSell.Stop();
                status = "진행 중인 자동 정리 작업을 중지했습니다.";
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

            selectedIndex = scan.Candidates.Count > 0
                ? Math.Clamp(selectedIndex, 0, scan.Candidates.Count - 1)
                : -1;

            plateResult = plateFilter.Filter(scan.Candidates);
            SaveConfig();

            if (updateStatus)
            {
                status = plateResult.PlateDataReady
                    ? $"검색 완료: 기본 후보 {scan.Candidates.Count}개, 투영세트 기준 정리 가능 {plateResult.Eligible.Count}개."
                    : $"검색 완료: 기본 후보 {scan.Candidates.Count}개. 투영세트 정보는 환상의 옷장을 연 뒤 다시 분석해 주세요.";
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[KWC] 검색 실패");
            status = $"검색 실패: {ex.GetType().Name}: {ex.Message}";
        }
    }

    private void RefreshPlateAnalysis()
    {
        plateResult = plateFilter.Filter(scan.Candidates);
        status = plateResult.PlateDataReady
            ? $"투영세트 분석 완료: 미사용 {plateResult.NotUsedOnPlates} · 사용 중 무염색 {plateResult.UsedUndyed} · 염색 보호 {plateResult.ProtectedDyed}."
            : "투영세트 데이터를 아직 읽을 수 없습니다. 환상의 옷장을 열어 둔 상태에서 다시 시도해 주세요.";
    }

    private void SaveConfig() => PluginInterface.SavePluginConfig(config);

    private void Draw()
    {
        if (!windowOpen) return;

        ImGui.SetNextWindowSize(new Vector2(880, 900), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("히메짱 옷장 정리기 v0.5###KRWardrobeCleaner", ref windowOpen))
        {
            ImGui.End();
            return;
        }

        ImGui.TextWrapped("만사가 귀찮은 공주님들을 위한 자동 정리 집사님!");
        ImGui.Separator();

        DrawArmoryPreclean();
        ImGui.Separator();

        DrawPlateAwareDresserCleanup();
        ImGui.Separator();

        DrawArmoryMoveSell();
        ImGui.Separator();

        DrawOptions();
        ImGui.Separator();

        DrawDresserCandidates();
        DrawDiagnostics();

        ImGui.End();
    }

    private void DrawArmoryPreclean()
    {
        ImGui.TextUnformatted("1단계 · 장비함 → 추억의 보관함");
        ImGui.TextWrapped("추억의 보관함을 직접 연 상태에서 실행합니다. 저장된 장비 세트, 염색/마테리아/투영 등 개별 상태가 있는 장비, 중복 장비는 건너뜁니다.");

        if (!armoryPreclean.IsRunning)
        {
            if (ImGui.Button("장비함 후보 검색##preclean"))
            {
                armoryPreclean.Scan();
                status = armoryPreclean.Status;
            }

            ImGui.SameLine();
            if (ImGui.Button($"안전 후보 보관 시작 ({armoryPreclean.Candidates.Count}개)##precleanstart"))
            {
                if (!cleanup.IsRunning && !armoryMoveSell.IsMoving && !armoryMoveSell.IsSelling)
                {
                    armoryPreclean.Start();
                    status = armoryPreclean.Status;
                }
                else
                {
                    status = "다른 정리 작업이 진행 중입니다.";
                }
            }
        }
        else
        {
            ImGui.TextUnformatted($"진행: {armoryPreclean.Processed}/{armoryPreclean.Total} · 보관 {armoryPreclean.Stored} · 제외 {armoryPreclean.Skipped} · 실패 {armoryPreclean.Failed}");
            if (ImGui.Button("1단계 중지"))
            {
                armoryPreclean.Stop();
                status = armoryPreclean.Status;
            }
        }

        ImGui.TextWrapped($"상태: {armoryPreclean.Status}");
        ImGui.TextWrapped($"검색 제외: 제작직 {armoryPreclean.SkippedCrafting} · 장비 세트 {armoryPreclean.SkippedGearset} · 개별 상태 {armoryPreclean.SkippedModified} · 중복 {armoryPreclean.SkippedDuplicate}");
    }

    private void DrawPlateAwareDresserCleanup()
    {
        ImGui.TextUnformatted("2단계 · 환상의 옷장 → 추억의 보관함");
        ImGui.TextWrapped("추억의 보관함 가능 아이템 중 ① 현재 투영세트에서 쓰이지 않거나, ② 투영세트에서 쓰이지만 염색이 없는 아이템만 복원 대상으로 잡습니다. 투영세트에서 염색된 외형은 보호합니다.");

        if (ImGui.Button("후보 다시 검색##dresser"))
            Rescan();

        ImGui.SameLine();
        if (ImGui.Button("투영세트 기준 다시 분석"))
            RefreshPlateAnalysis();

        ImGui.SameLine();
        if (ImGui.Button("Dungeon Drip 새로고침"))
        {
            Commands.ProcessCommand("/dungeondrip refresh");
            status = "Dungeon Drip 새로고침을 요청했습니다.";
        }

        ImGui.TextWrapped(
            plateResult.PlateDataReady
                ? $"분류: 미사용 {plateResult.NotUsedOnPlates} · 사용 중 무염색 {plateResult.UsedUndyed} · 염색 보호 {plateResult.ProtectedDyed} · 실제 정리 가능 {plateResult.Eligible.Count}"
                : "분류: 투영세트 데이터 대기 중");

        if (plateResult.PlateDataReady && plateResult.Eligible.Any(x => x.SourceDyed && !x.UsedOnPlate))
            ImGui.TextWrapped("주의: 투영세트에서 사용하지 않는 아이템은 조건상 정리 대상입니다. 해당 원본에 염색이 남아 있다면 추억의 보관함 이동 과정에서 그 염색은 사라질 수 있습니다.");

        var displayedStatus = cleanup.IsRunning || cleanup.IsWaitingForAutoDeposit ? cleanup.Status : status;
        ImGui.TextWrapped($"상태: {displayedStatus}");

        var freeSlots = restoreTester.GetFreeBagSlots();
        ImGui.TextUnformatted(
            $"환상의 옷장 사용: {scan.DresserCount} | 보관함 기록: {scan.ArmoireCount} | 기본 후보: {scan.Candidates.Count} | 가방 빈칸: {(freeSlots < 0 ? "확인 불가" : freeSlots)}");

        foreach (var note in scan.Notes)
            ImGui.TextWrapped("안내: " + note);

        if (!cleanup.IsRunning)
        {
            if (ImGui.Button($"투영세트 기준 안전 후보 전체 복원 ({plateResult.Eligible.Count}개)"))
            {
                RefreshPlateAnalysis();
                if (!plateResult.PlateDataReady)
                {
                    // RefreshPlateAnalysis already set a useful status.
                }
                else if (armoryPreclean.IsRunning || armoryMoveSell.IsMoving || armoryMoveSell.IsSelling)
                {
                    status = "다른 정리 작업이 진행 중입니다.";
                }
                else
                {
                    cleanup.Start(plateResult.Eligible);
                    status = cleanup.Status;
                }
            }
        }
        else
        {
            ImGui.TextUnformatted($"진행: {cleanup.Processed}/{cleanup.Total} · 복원 {cleanup.Restored} · 제외 {cleanup.Skipped} · 실패 {cleanup.Failed}");
            if (ImGui.Button("2단계 중지"))
            {
                cleanup.Stop();
                status = cleanup.Status;
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("가방의 보관 가능 아이템 → 추억의 보관함"))
        {
            cleanup.DepositNow();
            status = cleanup.Status;
        }
    }

    private void DrawArmoryMoveSell()
    {
        ImGui.TextUnformatted("옵션 · 장비함 후보를 인벤토리로 이동 / 체크 판매");
        ImGui.TextWrapped("장비함에서 추억의 보관함에 들어갈 수 있는 장비를 별도 목록으로 만듭니다. 체크한 항목만 인벤토리로 옮길 수 있고, 일반 상점 창을 연 뒤 체크 항목만 자동 판매할 수 있습니다.");

        if (!armoryMoveSell.IsMoving && !armoryMoveSell.IsSelling)
        {
            if (ImGui.Button("장비함 판매/이동 후보 검색"))
            {
                armoryMoveSell.Scan();
                status = armoryMoveSell.Status;
            }

            ImGui.SameLine();
            if (ImGui.Button("전체 선택"))
                armoryMoveSell.SelectAll(true);

            ImGui.SameLine();
            if (ImGui.Button("전체 해제"))
                armoryMoveSell.SelectAll(false);

            if (ImGui.Button($"체크 항목 인벤토리로 이동 ({armoryMoveSell.Selected.Count}개)"))
            {
                if (!cleanup.IsRunning && !armoryPreclean.IsRunning)
                {
                    armoryMoveSell.StartMoveSelected();
                    status = armoryMoveSell.Status;
                }
                else
                {
                    status = "다른 정리 작업이 진행 중입니다.";
                }
            }

            ImGui.SameLine();
            if (ImGui.Button($"체크 항목 상점 판매 ({armoryMoveSell.Selected.Count}개)"))
            {
                if (!cleanup.IsRunning && !armoryPreclean.IsRunning)
                {
                    armoryMoveSell.StartSellSelected();
                    status = armoryMoveSell.Status;
                }
                else
                {
                    status = "다른 정리 작업이 진행 중입니다.";
                }
            }
        }
        else
        {
            var mode = armoryMoveSell.IsMoving ? "인벤토리 이동" : "상점 판매";
            ImGui.TextUnformatted($"{mode} 진행 중 · 이동 {armoryMoveSell.Moved} · 판매 {armoryMoveSell.Sold} · 제외 {armoryMoveSell.Skipped} · 실패 {armoryMoveSell.Failed}");
            if (ImGui.Button("선택 작업 중지"))
            {
                armoryMoveSell.Stop();
                status = armoryMoveSell.Status;
            }
        }

        ImGui.TextWrapped($"상태: {armoryMoveSell.Status}");
        if (!string.IsNullOrWhiteSpace(armoryMoveSell.LastItemStatus))
            ImGui.TextWrapped($"최근 처리: {armoryMoveSell.LastItemStatus}");

        ImGui.TextWrapped($"검색 제외: 제작직 {armoryMoveSell.SkippedCrafting} · 장비 세트 {armoryMoveSell.SkippedGearset} · 개별 상태 {armoryMoveSell.SkippedModified} · 중복 {armoryMoveSell.SkippedDuplicate}");

        ImGui.BeginChild("ArmoryMoveSellList", new Vector2(0, 220), true);
        foreach (var entry in armoryMoveSell.Entries)
        {
            var selected = armoryMoveSell.Selected.Contains(entry.ItemId);
            var suffix = entry.AlreadyInArmoire ? " [보관함 보유]" : " [보관함 미보유]";
            if (entry.IsCraftingGear) suffix += " [제작직]";
            if (ImGui.Checkbox($"{entry.Name}{suffix}##manage{entry.ItemId}", ref selected))
                armoryMoveSell.SetSelected(entry.ItemId, selected);
        }
        ImGui.EndChild();

        ImGui.TextWrapped("판매는 되돌릴 수 있는 정리 단계가 아닙니다. 체크한 항목만 판매하며, 실제 판매 시에는 일반 상점의 구매/판매 창이 열려 있어야 합니다.");
    }

    private void DrawOptions()
    {
        if (!ImGui.CollapsingHeader("정리 옵션", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        var includeCraftingPreclean = config.IncludeCraftingGearInArmoryPreclean;
        if (ImGui.Checkbox("1단계에서 제작직 전용 장비도 추억의 보관함에 넣기", ref includeCraftingPreclean))
        {
            armoryPreclean.UpdateSettings(includeCraftingPreclean, config.ArmoryStoreIntervalMs);
            armoryPreclean.Scan();
        }

        var armoryInterval = config.ArmoryStoreIntervalMs;
        if (ImGui.SliderInt("장비함 → 보관함 처리 간격 (ms)", ref armoryInterval, 300, 3000))
            armoryPreclean.UpdateSettings(config.IncludeCraftingGearInArmoryPreclean, armoryInterval);

        var includeCraftingMove = config.IncludeCraftingGearInArmoryMove;
        if (ImGui.Checkbox("옵션 목록에 제작직 전용 장비도 포함", ref includeCraftingMove))
        {
            armoryMoveSell.UpdateSettings(includeCraftingMove, config.ArmoryMoveIntervalMs, config.VendorSellIntervalMs);
            armoryMoveSell.Scan();
        }

        var moveInterval = config.ArmoryMoveIntervalMs;
        if (ImGui.SliderInt("장비함 → 인벤토리 이동 간격 (ms)", ref moveInterval, 300, 3000))
            armoryMoveSell.UpdateSettings(config.IncludeCraftingGearInArmoryMove, moveInterval, config.VendorSellIntervalMs);

        var sellInterval = config.VendorSellIntervalMs;
        if (ImGui.SliderInt("상점 판매 간격 (ms)", ref sellInterval, 400, 3000))
            armoryMoveSell.UpdateSettings(config.IncludeCraftingGearInArmoryMove, config.ArmoryMoveIntervalMs, sellInterval);

        var interval = config.RestoreIntervalMs;
        if (ImGui.SliderInt("환상의 옷장 복원 간격 (ms)", ref interval, 300, 3000))
            cleanup.UpdateSettings(interval, config.ReserveFreeSlots, config.AutoDepositToArmoire);

        var reserve = config.ReserveFreeSlots;
        if (ImGui.SliderInt("복원 중 남겨둘 가방 빈칸", ref reserve, 0, 30))
            cleanup.UpdateSettings(config.RestoreIntervalMs, reserve, config.AutoDepositToArmoire);

        if (!config.IncludeCraftingGearInArmoryPreclean)
        {
            if (config.AutoDepositToArmoire)
                cleanup.UpdateSettings(config.RestoreIntervalMs, config.ReserveFreeSlots, false);

            ImGui.TextDisabled("AutoRetainer 자동 보관: 제작직 장비 제외가 켜져 있어 비활성화됨");
            ImGui.TextWrapped("AutoRetainer의 /autoretainer armoire는 장비함까지 함께 처리할 수 있어 제작직 제외 설정과 충돌할 수 있습니다.");
        }
        else
        {
            var autoDeposit = config.AutoDepositToArmoire;
            if (ImGui.Checkbox("환상의 옷장 복원 완료 후 AutoRetainer로 자동 보관", ref autoDeposit))
                cleanup.UpdateSettings(config.RestoreIntervalMs, config.ReserveFreeSlots, autoDeposit);
        }

        var showIds = config.ShowIds;
        if (ImGui.Checkbox("목록에 아이템 ID 표시", ref showIds))
        {
            config.ShowIds = showIds;
            SaveConfig();
        }

        ImGui.TextWrapped("제작직 전용 장비 판정은 목수·대장장이·갑주제작사·보석공예가·가죽공예가·재봉사·연금술사·요리사만 사용 가능한 장비 기준입니다. 전 직업 공용 의상은 제작직 전용으로 취급하지 않습니다.");
    }

    private void DrawDresserCandidates()
    {
        if (!ImGui.CollapsingHeader("환상의 옷장 정리 후보"))
            return;

        ImGui.BeginChild("DresserCandidates", new Vector2(0, 250), true);

        if (plateResult.PlateDataReady)
        {
            foreach (var item in plateResult.Eligible)
            {
                var candidate = item.Candidate;
                var reason = item.UsedOnPlate ? " [투영세트 사용/무염색]" : " [투영세트 미사용]";
                if (item.SourceDyed) reason += " [원본 염색 있음]";
                var label = config.ShowIds
                    ? $"{candidate.Name} [{candidate.ItemId}]{reason}"
                    : $"{candidate.Name}{reason}";
                ImGui.TextUnformatted(label);
            }
        }
        else
        {
            foreach (var candidate in scan.Candidates)
            {
                var label = config.ShowIds ? $"{candidate.Name} [{candidate.ItemId}]" : candidate.Name;
                ImGui.TextUnformatted(label);
            }
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
                Chat.Print("[히메짱 옷장 정리기] 진단 파일 저장: " + path);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[KWC] 진단 파일 저장 실패");
                status = "진단 파일 저장 실패: " + ex.Message;
            }
        }

        ImGui.TextUnformatted($"기록된 UI 이벤트: {recorder.Traces.Count}");
    }
}
