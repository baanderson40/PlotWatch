namespace PlotWatch.Diagnostics;

public enum DiagnosticSeverity
{
    Debug,
    Warning,
    Error,
}

public enum DiagnosticAction
{
    RunAll,
    InspectConfiguration,
    InspectCommandsAndUi,
    InspectDependencies,
    InspectLotteryClock,
    ClearHistory,
}

public enum DiagnosticSection
{
    Overview,
    Configuration,
    CommandsAndUi,
    Dependencies,
    LotteryClock,
    LogsAndErrors,
}

public sealed record DiagnosticEntry(DateTime TimestampLocal, DiagnosticSeverity Severity, string Message);

public sealed class DiagnosticRunResult(string action, DateTime startedAtLocal)
{
    private readonly List<DiagnosticEntry> findings = [];

    public string Action { get; } = action;
    public DateTime StartedAtLocal { get; } = startedAtLocal;
    public DateTime? CompletedAtLocal { get; private set; }
    public string Status { get; private set; } = "Running";
    public IReadOnlyList<DiagnosticEntry> Findings => findings;

    internal void Add(DiagnosticEntry entry) => findings.Add(entry);

    internal void Complete(string status)
    {
        Status = status;
        CompletedAtLocal = DateTime.Now;
    }
}

public sealed class PluginDiagnostics
{
    private const int MaxHistoryEntries = 100;
    private const int MaxRuns = 30;
    private readonly Func<bool> refreshDependencies;
    private readonly List<DiagnosticEntry> history = [];
    private readonly List<DiagnosticRunResult> runs = [];
    private DiagnosticRunResult? activeRun;

    public string ConfigurationStatus { get; private set; }
    public string LastError { get; private set; } = "None";
    public string PaissaHouseStatus { get; private set; } = "Unknown";
    public string LifestreamStatus { get; private set; } = "Unknown";
    public string NavigationStatus { get; private set; } = "Unknown";
    public string LotteryClockStatus { get; private set; } = "Not inspected";
    public string MainCommandStatus { get; set; } = "Not registered";
    public string AliasCommandStatus { get; set; } = "Not registered";
    public string DrawCallbackStatus { get; set; } = "Not registered";
    public string MainUiCallbackStatus { get; set; } = "Not registered";
    public string ConfigUiCallbackStatus { get; set; } = "Not registered";
    public string MainWindowStatus { get; set; } = "Not created";
    public string DebugWindowStatus { get; set; } = "Not created";
    public string Version { get; }
    public string BuildId { get; }
    public DateTime? LastCheckLocal { get; private set; }
    public IReadOnlyList<DiagnosticEntry> History => history;
    public IReadOnlyList<DiagnosticRunResult> Runs => runs;
    public bool IsRequestPending { get; set; }

    public PluginDiagnostics(string configurationStatus, Func<bool> refreshDependencies, string version, string buildId)
    {
        ConfigurationStatus = configurationStatus;
        this.refreshDependencies = refreshDependencies;
        Version = version;
        BuildId = buildId;
    }

    public bool RefreshDependencies() => refreshDependencies();

    public void SetConfigurationStatus(string value) => ConfigurationStatus = value;

    public void SetLastError(string value) => LastError = value;

    public void SetLotteryClockStatus(string value) => LotteryClockStatus = value;

    public void SetDependencies(string paissaHouse, string lifestream, string navigation)
    {
        PaissaHouseStatus = paissaHouse;
        LifestreamStatus = lifestream;
        NavigationStatus = navigation;
    }

    public void Record(DiagnosticSeverity severity, string message)
    {
        LastCheckLocal = DateTime.Now;
        var entry = new DiagnosticEntry(LastCheckLocal.Value, severity, message);
        history.Add(entry);
        activeRun?.Add(entry);
        if (history.Count > MaxHistoryEntries)
            history.RemoveAt(0);
    }

    public DiagnosticRunResult BeginRun(string action)
    {
        activeRun = new DiagnosticRunResult(action, DateTime.Now);
        runs.Add(activeRun);
        if (runs.Count > MaxRuns)
            runs.RemoveAt(0);
        return activeRun;
    }

    public void CompleteRun(string status)
    {
        activeRun?.Complete(status);
        activeRun = null;
    }

    public void ClearHistory()
    {
        history.Clear();
        runs.Clear();
        activeRun = null;
        LastCheckLocal = null;
    }
}
