using Dalamud.Game.Agent;
using Dalamud.Game.Agent.AgentArgTypes;
using Dalamud.Plugin.Services;

namespace KRWardrobeCleaner;

public sealed class CalibrationRecorder : IDisposable
{
    private readonly IAgentLifecycle agents;
    private readonly IPluginLog log;
    private readonly List<AgentTrace> traces = [];
    private bool enabled;
    private uint? armedItemId;
    private string? armedItemName;

    public IReadOnlyList<AgentTrace> Traces => traces;

    public CalibrationRecorder(IAgentLifecycle agents, IPluginLog log)
    {
        this.agents = agents;
        this.log = log;
        agents.RegisterListener(AgentEvent.PreReceiveEvent, AgentId.MiragePrismPrismBox, OnAgent);
        agents.RegisterListener(AgentEvent.PreReceiveEvent, AgentId.SelectYesno, OnAgent);
    }

    public void SetEnabled(bool value) => enabled = value;

    public void Arm(Candidate candidate)
    {
        armedItemId = candidate.ItemId;
        armedItemName = candidate.Name;
        traces.Clear();
        enabled = true;
        log.Information("[KWC] Calibration armed for {Item} ({Id})", candidate.Name, candidate.ItemId);
    }

    private void OnAgent(AgentEvent type, AgentArgs args)
    {
        if (!enabled || args is not AgentReceiveEventArgs recv) return;

        var values = new List<TraceValue>();
        foreach (var v in recv.AtkValueEnumerable)
        {
            string? value;
            try
            {
                var raw = v.GetValue();
                value = raw switch
                {
                    null => null,
                    string s => s,
                    _ => Convert.ToString(raw, System.Globalization.CultureInfo.InvariantCulture),
                };
            }
            catch (Exception ex)
            {
                value = $"<unreadable:{ex.GetType().Name}>";
            }

            values.Add(new TraceValue(v.ValueType.ToString(), value));
        }

        var trace = new AgentTrace(
            DateTimeOffset.Now,
            args.AgentId.ToString(),
            recv.EventKind,
            recv.ValueCount,
            values,
            armedItemId,
            armedItemName);

        traces.Add(trace);
        if (traces.Count > 100) traces.RemoveAt(0);
        log.Information("[KWC] Agent trace {Agent}, kind={Kind}, values={Count}", trace.Agent, trace.EventKind, trace.ValueCount);
    }

    public void Dispose()
    {
        agents.UnregisterListener(AgentEvent.PreReceiveEvent, AgentId.MiragePrismPrismBox, OnAgent);
        agents.UnregisterListener(AgentEvent.PreReceiveEvent, AgentId.SelectYesno, OnAgent);
    }
}
