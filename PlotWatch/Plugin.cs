using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using System.Reflection;
using System.Collections.Concurrent;
using System.Text.Json;
using System.IO;
using System.Diagnostics;
using PlotWatch.Commands;
using PlotWatch.Data;
using PlotWatch.Diagnostics;
using PlotWatch.Lottery;
using PlotWatch.Paissa;
using PlotWatch.Windows;

namespace PlotWatch;

public sealed class Plugin : IDalamudPlugin
{
    private const string MainCommand = "/plotwatch";
    private const string AliasCommand = "/pw";

    [PluginService]
    internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;

    [PluginService]
    internal static ICommandManager Commands { get; private set; } = null!;

    [PluginService]
    internal static IPluginLog Log { get; private set; } = null!;

    [PluginService]
    internal static IFramework Framework { get; private set; } = null!;

    [PluginService]
    internal static IPlayerState PlayerState { get; private set; } = null!;

    [PluginService]
    internal static IClientState ClientState { get; private set; } = null!;

    [PluginService]
    internal static IDataManager DataManager { get; private set; } = null!;

    [PluginService]
    internal static IChatGui ChatGui { get; private set; } = null!;

    [PluginService]
    internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;

    private readonly WindowSystem windowSystem = new("PlotWatch");
    private readonly Configuration configuration;
    private readonly MainWindow mainWindow;
    private readonly BidTracker bidTracker;
    private long nextBidCharacterRefreshTick;
    private readonly DebugWindow debugWindow;
    private readonly HousingCaptureDiagnostics housingCapture;
    private readonly HousingProbeState housingProbe;
    private readonly PaissaApiClient paissaApiClient = new();
    private readonly Dictionary<int, PaissaHousingWorldData> housingSnapshots = [];
    private readonly string housingCachePath;
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private readonly PluginDiagnostics diagnostics;
    private readonly ConfigurationResetConfirmation resetConfirmation;
    private readonly OwnedCommandRegistration mainCommandRegistration;
    private readonly OwnedCommandRegistration aliasCommandRegistration;
    private readonly Queue<DiagnosticAction> diagnosticQueue = new();
    private readonly ConcurrentQueue<Action> frameworkActions = new();
    private bool paissaRequestPending;
    private CancellationTokenSource? paissaRequestCancellation;
    private bool housingRequestPending;
    private CancellationTokenSource? housingRequestCancellation;
    private bool configurationSaveBlocked;
    private long nextLotteryRefreshTick;
    private bool homeWorldLookupFailureLogged;
    private int housingRateLimitedWorldCount;
    private static readonly TimeSpan ManualRefreshCooldown = TimeSpan.FromMinutes(15);
    private long housingBatchId;
    private int housingBatchCompleted;
    private int housingBatchSucceeded;
    private int housingBatchFailed;
    private int housingBatchExpected;

    public string Name => "PlotWatch";

    public Plugin()
    {
        configuration = LoadConfiguration(out configurationSaveBlocked);
        housingCachePath = Path.Combine(PluginInterface.GetPluginConfigDirectory(), "housing-cache.json");
        paissaApiClient.RequestStarted += OnPaissaRequestStarted;
        diagnostics = new PluginDiagnostics(
            configurationSaveBlocked ? "Load failed; saving disabled until reset." : "Loaded",
            RefreshDependencyStatus,
            typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "Unknown",
            typeof(Plugin).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(attribute => attribute.Key == "PlotWatchBuildId")?.Value ?? "Unknown");
        if (configurationSaveBlocked)
            diagnostics.SetLastError("Configuration could not be loaded safely; saving is blocked until explicit reset.");
        resetConfirmation = new ConfigurationResetConfirmation(ResetConfigurationAndSave);
        mainCommandRegistration = CreateCommandRegistration(MainCommand, "Open PlotWatch housing and lottery tracker.", true);
        aliasCommandRegistration = CreateCommandRegistration(AliasCommand, "Alias for /plotwatch.", false);
        bidTracker = new BidTracker(new BidFileStore(Path.Combine(PluginInterface.GetPluginConfigDirectory(), "bids.json")),
            (success, message) => RecordDiagnostic(success ? DiagnosticSeverity.Debug : DiagnosticSeverity.Warning, message));
        if (bidTracker.IsReadOnly)
            Log.Warning("Bid data requires recovery; see My Bids. Existing files have not been overwritten.");
        mainWindow = new MainWindow(configuration, diagnostics, SaveConfiguration, resetConfirmation, OpenDebugWindow,
            FetchHousingWorlds, () => RefreshHousingWorlds(), CancelHousingRequest, bidTracker);
        LoadHousingCache();
        mainWindow.SetHousingRefreshCooldown(MaxDate(configuration.HousingRefreshCooldownUntilUtc, paissaApiClient.CooldownUntilUtc));
        mainWindow.SetNextAutomaticRefresh(configuration.HousingNextAutomaticRefreshUtc);
        UpdateHomeWorldSelection();
        housingProbe = new HousingProbeState((level, message) =>
        {
            switch (level)
            {
                case HousingProbeLogLevel.Debug: Log.Debug("{HousingProbe:l}", message); break;
                case HousingProbeLogLevel.Warning: Log.Warning("{HousingProbe:l}", message); break;
                default: Log.Verbose("{HousingProbe:l}", message); break;
            }
        });
        housingCapture = new HousingCaptureDiagnostics(housingProbe, Framework, PlayerState, DataManager, ChatGui, AddonLifecycle)
        { CurrentTerritory = () => ClientState.TerritoryType };
        debugWindow = new DebugWindow(configuration, diagnostics, QueueDiagnostic, housingProbe,
            options => frameworkActions.Enqueue(() => housingCapture.Arm(options)));
        windowSystem.AddWindow(mainWindow);
        windowSystem.AddWindow(debugWindow);
        UpdateLotterySnapshot();
        diagnostics.MainWindowStatus = "Registered";
        diagnostics.DebugWindowStatus = "Registered";

        if (!mainCommandRegistration.Register())
            Log.Error("Could not register /plotwatch; another plugin may own the command.");

        if (!aliasCommandRegistration.Register())
            Log.Warning("Could not register /pw because command is already in use. PlotWatch will leave it untouched.");
        RefreshCommandStatus();

        PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += OpenMainUi;
        PluginInterface.UiBuilder.OpenConfigUi += OpenConfigUi;
        PluginInterface.ActivePluginsChanged += OnActivePluginsChanged;
        Framework.Update += OnFrameworkUpdate;
        diagnostics.DrawCallbackStatus = "Registered";
        diagnostics.MainUiCallbackStatus = "Registered";
        diagnostics.ConfigUiCallbackStatus = "Registered";
        RefreshDependencyStatus();
        FetchHousingWorlds();
        Log.Debug("PlotWatch loaded. Version {Version:l}; build ID {BuildId:l}; Dalamud load timestamp UTC {LoadTimestamp:O}.",
            diagnostics.Version,
            diagnostics.BuildId,
            PluginInterface.LoadTimeUTC);
    }

    public void Dispose()
    {
        housingCapture.Dispose();
        PluginInterface.ActivePluginsChanged -= OnActivePluginsChanged;
        lifetimeCancellation.Cancel();
        housingRequestCancellation?.Cancel();
        paissaApiClient.RequestStarted -= OnPaissaRequestStarted;
        paissaApiClient.Dispose();
        lifetimeCancellation.Dispose();
        Framework.Update -= OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= OpenMainUi;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenConfigUi;

        mainCommandRegistration.Dispose();
        aliasCommandRegistration.Dispose();

        windowSystem.RemoveAllWindows();
    }

    private static Configuration LoadConfiguration(out bool failed)
    {
        failed = false;
        try
        {
            if (PluginInterface.GetPluginConfig() is not { } stored)
                return new Configuration();

            if (stored is Configuration configuration)
            {
                if (configuration.Version > Configuration.CurrentVersion)
                {
                    failed = true;
                    Log.Warning("Configuration was written by a newer PlotWatch version; preserving it and disabling saves until explicitly reset.");
                    return new Configuration();
                }

                configuration.Normalize();
                return configuration;
            }

            failed = true;
            Log.Error("Stored PlotWatch configuration has an unexpected type. It will not be overwritten unless reset is explicitly requested.");
        }
        catch (Exception ex)
        {
            failed = true;
            Log.Error(ex, "Failed to load PlotWatch configuration. Saving is disabled until configuration is explicitly reset.");
        }

        return new Configuration();
    }

    private void SaveConfiguration()
    {
        if (configurationSaveBlocked)
            return;

        configuration.Version = Configuration.CurrentVersion;
        try
        {
            PluginInterface.SavePluginConfig(configuration);
            diagnostics.SetConfigurationStatus("Loaded and saved");
        }
        catch (Exception ex)
        {
            diagnostics.SetConfigurationStatus("Save failed");
            diagnostics.SetLastError(ex.Message);
            Log.Error(ex, "Failed to save PlotWatch configuration.");
        }
    }

    private string? ResetConfigurationAndSave()
    {
        configuration.ResetToDefaults();
        configurationSaveBlocked = false;
        diagnostics.SetConfigurationStatus("Reset to defaults");
        try
        {
            PluginInterface.SavePluginConfig(configuration);
            Log.Warning("PlotWatch configuration reset to defaults by user request.");
            return null;
        }
        catch (Exception ex)
        {
            diagnostics.SetConfigurationStatus("Reset; save failed");
            diagnostics.SetLastError(ex.Message);
            Log.Error(ex, "Failed to save reset PlotWatch configuration.");
            return $"Could not save reset configuration: {ex.Message}";
        }
    }

    private void OnCommand(string command, string arguments)
    {
        mainWindow.IsOpen = true;
        mainWindow.SelectDefaultTab();
    }

    private void OpenMainUi()
    {
        mainWindow.SelectDefaultTab();
        mainWindow.IsOpen = true;
    }

    private void OpenConfigUi()
    {
        mainWindow.SelectSettingsTab();
        mainWindow.IsOpen = true;
    }

    private void OpenDebugWindow()
    {
        debugWindow.IsOpen = true;
        debugWindow.BringToFront();
    }

    private void QueueDiagnostic(DiagnosticAction action)
    {
        diagnosticQueue.Enqueue(action);
        diagnostics.IsRequestPending = true;
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        UpdateLotterySnapshot();
        UpdateHomeWorldSelection();

        UpdateBidCharacter();
        housingCapture.Tick();

        while (frameworkActions.TryDequeue(out var frameworkAction))
            frameworkAction();

        CheckAutomaticHousingRefresh();

        if (diagnosticQueue.Count == 0)
        {
            if (!paissaRequestPending)
                diagnostics.IsRequestPending = false;
            return;
        }

        var action = diagnosticQueue.Dequeue();
        diagnostics.IsRequestPending = diagnosticQueue.Count > 0;
        RunDiagnostic(action);
    }

    private void RunDiagnostic(DiagnosticAction action)
    {
        if (action is DiagnosticAction.InspectHousingTemplates or DiagnosticAction.StopHousingProbe or DiagnosticAction.ClearHousingProbe)
        {
            switch (action)
            {
                case DiagnosticAction.InspectHousingTemplates: housingCapture.InspectTemplates(); break;
                case DiagnosticAction.StopHousingProbe: housingCapture.Stop(); break;
                case DiagnosticAction.ClearHousingProbe: housingCapture.Clear(); break;
            }
            return;
        }
        if (action == DiagnosticAction.CancelPaissaRequest)
        {
            CancelPaissaRequest();
            return;
        }

        if (action is DiagnosticAction.FetchPaissaWorlds or DiagnosticAction.InspectPaissaWorld or DiagnosticAction.InspectPaissaSchema)
        {
            StartPaissaRequest(action);
            return;
        }

        var actionName = action switch
        {
            DiagnosticAction.RunAll => "All read-only checks",
            DiagnosticAction.InspectConfiguration => "Configuration inspection",
            DiagnosticAction.InspectCommandsAndUi => "Commands and UI check",
            DiagnosticAction.InspectDependencies => "Dependency check",
            DiagnosticAction.InspectLotteryClock => "Lottery clock inspection",
            DiagnosticAction.FetchPaissaWorlds => "PaissaDB world-list request",
            DiagnosticAction.InspectPaissaWorld => "PaissaDB world inspection",
            DiagnosticAction.InspectPaissaSchema => "PaissaDB OpenAPI inspection",
            DiagnosticAction.ClearHistory => "Clear displayed history",
            _ => "Unknown diagnostic action",
        };

        Log.Debug("{Action:l} started.", actionName);
        try
        {
            if (action == DiagnosticAction.ClearHistory)
                diagnostics.ClearHistory();

            diagnostics.BeginRun(actionName);
            var succeeded = true;
            switch (action)
            {
                case DiagnosticAction.RunAll:
                    InspectConfiguration();
                    InspectCommandsAndUi();
                    succeeded = InspectDependencies();
                    InspectLotteryClock();
                    break;
                case DiagnosticAction.InspectConfiguration:
                    InspectConfiguration();
                    break;
                case DiagnosticAction.InspectCommandsAndUi:
                    InspectCommandsAndUi();
                    break;
                case DiagnosticAction.InspectDependencies:
                    succeeded = InspectDependencies();
                    break;
                case DiagnosticAction.InspectLotteryClock:
                    InspectLotteryClock();
                    break;
                case DiagnosticAction.ClearHistory:
                    RecordDiagnostic(DiagnosticSeverity.Debug, "Displayed diagnostic history cleared.");
                    break;
            }

            if (succeeded)
            {
                RecordDiagnostic(DiagnosticSeverity.Debug, $"{actionName} completed.");
                diagnostics.CompleteRun("Completed");
            }
            else
            {
                RecordDiagnostic(DiagnosticSeverity.Warning, $"{actionName} failed: one or more checks were unavailable.");
                diagnostics.CompleteRun("Warning");
            }
        }
        catch (Exception ex)
        {
            diagnostics.SetLastError(ex.Message);
            RecordDiagnostic(DiagnosticSeverity.Error, $"{actionName} failed: {ex.Message}");
            diagnostics.CompleteRun("Failed");
            Log.Error(ex, "{Action} failed.", actionName);
        }
    }

    private void StartPaissaRequest(DiagnosticAction action)
    {
        paissaRequestPending = true;
        paissaRequestCancellation?.Dispose();
        paissaRequestCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetimeCancellation.Token);
        diagnostics.IsRequestPending = true;
        var actionName = action switch
        {
            DiagnosticAction.FetchPaissaWorlds => "PaissaDB world-list request",
            DiagnosticAction.InspectPaissaWorld => "PaissaDB world inspection",
            _ => "PaissaDB OpenAPI inspection",
        };
        diagnostics.BeginRun(actionName);
        Log.Debug("{Action:l} started.", actionName);
        _ = ExecutePaissaRequestAsync(action, actionName, paissaRequestCancellation.Token);
    }

    private async Task ExecutePaissaRequestAsync(DiagnosticAction action, string actionName, CancellationToken cancellationToken)
    {
        try
        {
            switch (action)
            {
                case DiagnosticAction.FetchPaissaWorlds:
                {
                    var response = await paissaApiClient.GetWorldsAsync(cancellationToken).ConfigureAwait(false);
                    frameworkActions.Enqueue(() => CompletePaissaWorlds(response, actionName));
                    break;
                }
                case DiagnosticAction.InspectPaissaWorld:
                {
                    var worldId = diagnostics.SelectedPaissaWorldId;
                    if (worldId is null)
                    {
                        frameworkActions.Enqueue(() => CompletePaissaFailure(actionName, "Select a world first."));
                        break;
                    }
                    var response = await paissaApiClient.GetWorldAsync(worldId.Value, cancellationToken).ConfigureAwait(false);
                    frameworkActions.Enqueue(() => CompletePaissaInspection(response, actionName));
                    break;
                }
                case DiagnosticAction.InspectPaissaSchema:
                {
                    var response = await paissaApiClient.GetOpenApiAsync(cancellationToken).ConfigureAwait(false);
                    frameworkActions.Enqueue(() => CompletePaissaSchema(response, actionName));
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            frameworkActions.Enqueue(() => CompletePaissaFailure(actionName, ex.Message));
        }
    }

    private void CompletePaissaWorlds(PaissaApiResponse<IReadOnlyList<PaissaWorld>> response, string actionName)
    {
        FinishPaissaRequest();
        var status = FormatApiStatus(response.Url, response.StatusCode, response.Duration, response.FetchedAtUtc, response.Error);
        diagnostics.SetPaissaWorlds(response.IsSuccess ? response.Data : null, status, response.IsSuccess ? response.FetchedAtUtc : null);
        var summary = response.IsSuccess
            ? $"{status}; worlds={response.Data!.Count}; sample={string.Join(", ", response.Data.Take(8).Select(world => $"{world.Name}/{world.DataCenterName}#{world.Id}"))}."
            : $"{actionName} failed: {status}";
        RecordDiagnostic(response.IsSuccess ? DiagnosticSeverity.Debug : DiagnosticSeverity.Warning, summary);
        diagnostics.CompleteRun(response.IsSuccess ? "Completed" : "Warning");
        Log.Debug("{Action:l} completed.", actionName);
    }

    private void CompletePaissaInspection(PaissaApiResponse<PaissaWorldInspection> response, string actionName)
    {
        FinishPaissaRequest();
        var status = FormatApiStatus(response.Url, response.StatusCode, response.Duration, response.FetchedAtUtc, response.Error);
        diagnostics.SetPaissaInspection(response.IsSuccess ? response.Data : null, status, response.IsSuccess ? response.FetchedAtUtc : null);
        var inspection = response.Data;
        var summary = response.IsSuccess
            ? $"{status}; world={inspection!.Name}#{inspection.Id}; reported plots={inspection.ReportedOpenPlots?.ToString() ?? "Unknown"}; districts={inspection.Districts.Count}; parsed plots={inspection.Districts.Sum(district => district.ActualPlotCount)}."
            : $"{actionName} failed: {status}";
        RecordDiagnostic(response.IsSuccess ? DiagnosticSeverity.Debug : DiagnosticSeverity.Warning, summary);
        if (response.IsSuccess && inspection is not null)
        {
            foreach (var district in inspection.Districts)
                RecordDiagnostic(DiagnosticSeverity.Debug,
                    $"PaissaDB district {district.Name}#{district.Id}: API open plots={district.ReportedOpenPlots?.ToString() ?? "Unknown"}; parsed={district.ActualPlotCount}; oldest source observation={FormatUnixTimestamp(district.OldestPlotTime)}.");

            var totalPlots = inspection.Districts.Sum(district => district.ActualPlotCount);
            foreach (var field in inspection.Fields)
                RecordDiagnostic(DiagnosticSeverity.Debug,
                    $"PaissaDB field {field.Name}: present={field.PresentCount}; null={field.NullCount}; missing={field.MissingCount}; sample={field.SampleValue}.");

            foreach (var plot in inspection.Samples)
            {
                var deadline = PaissaApiParser.UnixSecondsToUtc(plot.LottoPhaseUntil);
                var phaseLabel = PaissaApiParser.DescribeLotteryPhase(plot.LottoPhase);
                if (deadline is { } phaseDeadline && phaseDeadline <= response.FetchedAtUtc)
                    phaseLabel += " (deadline expired at fetch time)";

                var ward = plot.WardNumber is { } wardNumber ? $"{wardNumber + 1} (raw {wardNumber})" : "Unknown";
                var plotNumber = plot.PlotNumber is { } plotIndex ? $"{plotIndex + 1} (raw {plotIndex})" : "Unknown";
                RecordDiagnostic(DiagnosticSeverity.Debug,
                    $"PaissaDB sample plot: world={plot.WorldId?.ToString() ?? "Unknown"}; district={plot.DistrictId?.ToString() ?? "Unknown"}; ward={ward}; plot={plotNumber}; size={DescribePlotSize(plot.Size)}; price={plot.Price?.ToString("N0") ?? "Unknown"}; purchase={PaissaApiParser.DescribePurchaseSystem(plot.PurchaseSystem)}; PW-global-phase={PaissaHousingPolicy.GetPhaseLabel(plot.PurchaseSystem, response.FetchedAtUtc)}; global-phase-close={PaissaHousingPolicy.GetDisplayedPhaseDeadline(plot.PurchaseSystem, response.FetchedAtUtc)?.ToString("O") ?? "Unknown"}; raw-API-phase={phaseLabel}; entrants={plot.LottoEntries?.ToString() ?? "Unknown"}; raw-API-deadline={deadline?.ToString("O") ?? "Unknown"}; first seen={FormatUnixTimestamp(plot.FirstSeenTime)}; last observed={FormatUnixTimestamp(plot.LastUpdatedTime)}.");
            }

            foreach (var warning in inspection.Warnings.Take(12))
                RecordDiagnostic(DiagnosticSeverity.Warning, $"PaissaDB response schema warning: {warning}");

            if (totalPlots == 0)
                RecordDiagnostic(DiagnosticSeverity.Debug, "PaissaDB returned zero open plots for this world; this is a successful empty result, not a request failure.");
        }
        diagnostics.CompleteRun(response.IsSuccess ? "Completed" : "Warning");
        Log.Debug("{Action:l} completed.", actionName);
    }

    private void CompletePaissaSchema(PaissaApiResponse<string> response, string actionName)
    {
        FinishPaissaRequest();
        var status = FormatApiStatus(response.Url, response.StatusCode, response.Duration, response.FetchedAtUtc, response.Error);
        string summary = "";
        string? schemaError = null;
        if (response.IsSuccess)
        {
            try
            {
                summary = SummarizeOpenApi(response.Data!);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
            {
                schemaError = $"Schema summary failed: {ex.Message}";
            }
        }
        var succeeded = response.IsSuccess && schemaError is null;
        diagnostics.SetPaissaSchema(summary, schemaError is null ? status : $"{status}; {schemaError}", succeeded ? response.FetchedAtUtc : null);
        RecordDiagnostic(succeeded ? DiagnosticSeverity.Debug : DiagnosticSeverity.Warning,
            succeeded ? $"{status}; {summary}" : $"{actionName} failed: {status}; {schemaError ?? ""}");
        diagnostics.CompleteRun(succeeded ? "Completed" : "Warning");
        Log.Debug("{Action:l} completed.", actionName);
    }

    private void CompletePaissaFailure(string actionName, string error)
    {
        FinishPaissaRequest();
        diagnostics.SetLastError(error);
        RecordDiagnostic(DiagnosticSeverity.Error, $"{actionName} failed: {error}");
        diagnostics.CompleteRun("Failed");
        Log.Warning("{Action} failed: {Error}", actionName, error);
    }

    private bool CancelPaissaRequest()
    {
        if (!paissaRequestPending || paissaRequestCancellation is null)
        {
            RecordDiagnostic(DiagnosticSeverity.Debug, "No PaissaDB request is currently running.");
            return false;
        }

        paissaRequestCancellation.Cancel();
        diagnostics.IsRequestPending = true;
        RecordDiagnostic(DiagnosticSeverity.Debug, "PaissaDB request cancellation requested.");
        return true;
    }

    private void FinishPaissaRequest()
    {
        paissaRequestPending = false;
        paissaRequestCancellation?.Dispose();
        paissaRequestCancellation = null;
        diagnostics.IsRequestPending = diagnosticQueue.Count > 0;
    }

    private static string FormatApiStatus(string url, System.Net.HttpStatusCode? statusCode, TimeSpan duration, DateTimeOffset fetchedAtUtc, string? error)
        => $"{url}; HTTP {(statusCode is null ? "none" : $"{(int)statusCode} {statusCode}")}; {duration.TotalMilliseconds:F0} ms; fetched {fetchedAtUtc:O}{(error is null ? "" : $"; error: {error}")}";

    private static string FormatUnixTimestamp(double? timestamp)
        => PaissaApiParser.UnixSecondsToUtc(timestamp)?.ToString("O") ?? "Unknown";

    private static string DescribePlotSize(int? size) => size switch
    {
        0 => "Small (0)",
        1 => "Medium (1)",
        2 => "Large (2)",
        null => "Unknown",
        _ => $"Unrecognized ({size})",
    };

    private static string SummarizeOpenApi(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var paths = root.GetProperty("paths").EnumerateObject().Select(path => path.Name).ToArray();
        var schemas = root.GetProperty("components").GetProperty("schemas");
        var worldFields = schemas.GetProperty("WorldDetail").GetProperty("properties").EnumerateObject().Select(property => property.Name);
        var plotFields = schemas.GetProperty("OpenPlotDetail").GetProperty("properties").EnumerateObject().Select(property => property.Name);
        return $"OpenAPI {root.GetProperty("openapi").GetString()}; paths={string.Join(", ", paths)}; WorldDetail fields={string.Join(", ", worldFields)}; OpenPlotDetail fields={string.Join(", ", plotFields)}.";
    }

    private void InspectConfiguration()
    {
        var summary = $"Schema {configuration.Version}/{Configuration.CurrentVersion}; state: {diagnostics.ConfigurationStatus}; default tab: {(PlotWatchTab)configuration.DefaultTab}; text scale: {configuration.TextScalePercent:F0}%; schedule display: {(configuration.DisplayLotteryTimesInUtc ? "UTC" : "local")}; notifications chat={configuration.ChatNotificationsEnabled}, toast={configuration.ToastNotificationsEnabled}, sound={configuration.SoundNotificationsEnabled}.";
        RecordDiagnostic(DiagnosticSeverity.Debug, summary);
    }

    private void InspectCommandsAndUi()
    {
        RefreshCommandStatus();
        RecordDiagnostic(DiagnosticSeverity.Debug,
            $"Commands/UI: /plotwatch={diagnostics.MainCommandStatus}; /pw={diagnostics.AliasCommandStatus}; Draw={diagnostics.DrawCallbackStatus}; OpenMainUi={diagnostics.MainUiCallbackStatus}; OpenConfigUi={diagnostics.ConfigUiCallbackStatus}; main window={diagnostics.MainWindowStatus}; debug window={diagnostics.DebugWindowStatus}. Command status checks the live handler delegate; callback/window statuses are PlotWatch's tracked registrations.");
    }

    private bool InspectDependencies()
    {
        if (!RefreshDependencyStatus())
        {
            RecordDiagnostic(DiagnosticSeverity.Warning, $"Dependency inspection failed: {diagnostics.LastError}");
            return false;
        }

        RecordDiagnostic(DiagnosticSeverity.Debug,
            $"Optional travel dependency: Lifestream={diagnostics.LifestreamStatus}. Detection means installed/loaded metadata only; compatibility is unvalidated.");
        return true;
    }

    private void InspectLotteryClock()
    {
        var snapshot = LotterySchedule.GetSnapshot(DateTimeOffset.UtcNow);
        var displayZone = LotterySchedule.GetDisplayZoneLabel(configuration.DisplayLotteryTimesInUtc, TimeZoneInfo.Local, snapshot.NowUtc);
        var summary = $"Now UTC={snapshot.NowUtc:O}; cycle={snapshot.CycleId}; phase={snapshot.CurrentPhase}; phase ends UTC={snapshot.CurrentPhaseEndsAtUtc:O}; remaining={LotterySchedule.FormatRemaining(snapshot.Remaining)}; application UTC={snapshot.Application.StartsAtUtc:O}–{snapshot.Application.EndsAtUtc:O} (next {snapshot.Application.NextAtUtc:O}); results UTC={snapshot.Results.StartsAtUtc:O}–{snapshot.Results.EndsAtUtc:O} (next {snapshot.Results.NextAtUtc:O}); display={displayZone}; anchor UTC={LotterySchedule.ApplicationAnchorUtc:O}.";
        diagnostics.SetLotteryClockStatus(summary);
        RecordDiagnostic(DiagnosticSeverity.Debug, summary);
    }

    private void UpdateLotterySnapshot()
    {
        var tickCount = Environment.TickCount64;
        if (tickCount < nextLotteryRefreshTick)
            return;

        var snapshot = LotterySchedule.GetSnapshot(DateTimeOffset.UtcNow);
        mainWindow.SetLotterySnapshot(snapshot);
        debugWindow.SetLotterySnapshot(snapshot);
        nextLotteryRefreshTick = tickCount + 1_000;
    }

    private void CheckAutomaticHousingRefresh()
    {
        if (housingRequestPending || mainWindow.GetAllHousingWorlds().Count == 0)
            return;

        var now = DateTimeOffset.UtcNow;
        var scheduled = configuration.HousingNextAutomaticRefreshUtc;
        if (scheduled != default && now < scheduled)
            return;
        if (paissaApiClient.CooldownUntilUtc > now)
        {
            mainWindow.SetHousingRefreshCooldown(MaxDate(configuration.HousingRefreshCooldownUntilUtc, paissaApiClient.CooldownUntilUtc));
            return;
        }

        RecordDiagnostic(DiagnosticSeverity.Debug,
            $"Hourly housing refresh due: scheduled={(scheduled == default ? "not set" : scheduled.ToString("O"))}; starting one refresh batch.");
        RefreshHousingWorlds(automatic: true);
    }

    private void FetchHousingWorlds()
    {
        if (housingRequestPending)
        {
            RecordDiagnostic(DiagnosticSeverity.Debug, "Housing cache startup skipped: another housing request is already active.");
            return;
        }

        RecordDiagnostic(DiagnosticSeverity.Debug, $"Housing cache startup: fetching world directory; cache currently has {housingSnapshots.Count} worlds.");
        StartHousingRequest();
        housingRateLimitedWorldCount = 0;
        mainWindow.SetHousingWorlds(null, "Fetching world directory...");
        _ = FetchHousingWorldsAsync(housingRequestCancellation!.Token);
    }

    private async Task FetchHousingWorldsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var response = await paissaApiClient.GetWorldsAsync(cancellationToken).ConfigureAwait(false);
            frameworkActions.Enqueue(() =>
            {
                FinishHousingRequest();
                var status = FormatApiStatus(response.Url, response.StatusCode, response.Duration, response.FetchedAtUtc, response.Error);
                mainWindow.SetHousingWorlds(response.IsSuccess ? response.Data : null,
                    response.IsSuccess ? $"Loaded {response.Data!.Count} worlds · {status}" : $"World request failed · {status}");
                if (response.IsSuccess && response.Data is not null)
                {
                    foreach (var cached in housingSnapshots.Values)
                        mainWindow.SetHousingWorldData(cached.World.Id, cached, "Loaded from local cache");
                    RecordDiagnostic(DiagnosticSeverity.Debug,
                        $"Housing cache startup: directory has {response.Data.Count} worlds; restored cache covers {housingSnapshots.Keys.Count(id => response.Data.Any(world => world.Id == id))}.");
                    RefreshHousingWorlds(startupResume: true);
                }
                RecordDiagnostic(response.IsSuccess ? DiagnosticSeverity.Debug : DiagnosticSeverity.Warning,
                    $"Housing browser world directory: {status}; worlds={response.Data?.Count.ToString() ?? "not updated"}.");
            });
        }
        catch (Exception ex)
        {
            frameworkActions.Enqueue(() =>
            {
                FinishHousingRequest();
                mainWindow.SetHousingWorlds(null, $"World request failed: {ex.Message}");
                RecordDiagnostic(DiagnosticSeverity.Error, $"Housing browser world request failed: {ex.Message}");
            });
        }
    }

    private void RefreshHousingWorlds(bool startupResume = false, bool automatic = false)
    {
        var now = DateTimeOffset.UtcNow;
        var serverCooldown = paissaApiClient.CooldownUntilUtc;
        if (housingRequestPending)
        {
            RecordDiagnostic(DiagnosticSeverity.Debug, "Housing refresh skipped: another housing request is active.");
            return;
        }
        if (serverCooldown > now)
        {
            mainWindow.SetHousingRefreshCooldown(MaxDate(configuration.HousingRefreshCooldownUntilUtc, serverCooldown));
            RecordDiagnostic(DiagnosticSeverity.Warning,
                $"Housing refresh paused by PaissaDB cooldown until {serverCooldown:O}; no queued requests started.");
            return;
        }

        var allWorlds = mainWindow.GetAllHousingWorlds().ToArray();
        if (allWorlds.Length == 0)
        {
            RecordDiagnostic(DiagnosticSeverity.Warning, "Housing refresh not started: world directory is empty.");
            return;
        }

        var cacheComplete = allWorlds.All(world => housingSnapshots.ContainsKey(world.Id));
        var manualCooldown = configuration.HousingRefreshCooldownUntilUtc;
        if (!(startupResume && !cacheComplete) && !automatic && now < manualCooldown)
        {
            mainWindow.SetHousingRefreshCooldown(MaxDate(manualCooldown, serverCooldown));
            RecordDiagnostic(DiagnosticSeverity.Debug,
                $"Housing refresh skipped: cache={housingSnapshots.Count}/{allWorlds.Length}; manual cooldown until {manualCooldown:O}; startup={startupResume}.");
            return;
        }

        var worlds = startupResume && !cacheComplete
            ? allWorlds.Where(world => !housingSnapshots.ContainsKey(world.Id)).ToArray()
            : allWorlds;
        if (worlds.Length == 0)
        {
            RecordDiagnostic(DiagnosticSeverity.Debug, "Housing cache startup: all directory worlds already cached; no missing-world queue needed.");
            return;
        }

        configuration.HousingRefreshCooldownUntilUtc = now + ManualRefreshCooldown;
        SaveConfiguration();
        mainWindow.SetHousingRefreshCooldown(MaxDate(configuration.HousingRefreshCooldownUntilUtc, serverCooldown));
        housingBatchId++;
        housingBatchCompleted = housingBatchSucceeded = housingBatchFailed = housingRateLimitedWorldCount = 0;
        housingBatchExpected = worlds.Length;
        RecordDiagnostic(DiagnosticSeverity.Debug,
            $"Housing cache batch {housingBatchId} started: queued={worlds.Length}; existing-cache={housingSnapshots.Count}/{allWorlds.Length}; startup-resume={startupResume && !cacheComplete}; minimum-request-spacing=10s; max-concurrency=2.");
        StartHousingRequest();
        mainWindow.SetHousingRefreshProgress(0, worlds.Length);
        foreach (var world in worlds)
            mainWindow.SetHousingWorldData(world.Id, null, "Loading listings...");
        _ = RefreshHousingWorldsAsync(worlds, housingRequestCancellation!.Token);
    }

    private async Task RefreshHousingWorldsAsync(IReadOnlyList<PaissaWorld> worlds, CancellationToken cancellationToken)
    {
        var tasks = worlds.Select(async world =>
        {
            (PaissaApiResponse<PaissaWorldInspection>? Response, string? Error) result;
            try
            {
                result = (await GetHousingWorldWithRetryAsync(world, cancellationToken).ConfigureAwait(false), null);
            }
            catch (Exception ex)
            {
                result = (null, ex.Message);
            }
            frameworkActions.Enqueue(() => ApplyHousingWorldResult(world, result.Response, result.Error));
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);

        frameworkActions.Enqueue(() =>
        {
            FinishHousingRequest();
            var until = MaxDate(configuration.HousingRefreshCooldownUntilUtc, paissaApiClient.CooldownUntilUtc);
            configuration.HousingRefreshCooldownUntilUtc = until;
            configuration.HousingNextAutomaticRefreshUtc = DateTimeOffset.UtcNow.AddHours(1);
            SaveConfiguration();
            mainWindow.SetHousingRefreshCooldown(until);
            mainWindow.SetNextAutomaticRefresh(configuration.HousingNextAutomaticRefreshUtc);
            if (housingRateLimitedWorldCount > 0)
                RecordDiagnostic(DiagnosticSeverity.Warning,
                    $"PaissaDB rate-limited {housingRateLimitedWorldCount} world request(s); queued refresh paused until {until:O}. Cached listings retained.");
            var cancelled = cancellationToken.IsCancellationRequested;
            RecordDiagnostic(cancelled ? DiagnosticSeverity.Warning : DiagnosticSeverity.Debug,
                $"Housing cache batch {housingBatchId} {(cancelled ? "cancelled" : "finished")}: success={housingBatchSucceeded}; failed={housingBatchFailed}; rate-limited={housingRateLimitedWorldCount}; completed={housingBatchCompleted}/{housingBatchExpected}; stored-worlds={housingSnapshots.Count}.");
        });
    }

    private async Task<PaissaApiResponse<PaissaWorldInspection>> GetHousingWorldWithRetryAsync(
        PaissaWorld world, CancellationToken cancellationToken)
    {
        const int maxAttempts = 2;
        for (var attempt = 1; ; attempt++)
        {
            var response = await paissaApiClient.GetWorldAsync(world.Id, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != System.Net.HttpStatusCode.TooManyRequests || attempt >= maxAttempts)
            {
                if (attempt > 1 && response.IsSuccess)
                    Log.Debug("PaissaDB retry recovered {World}#{WorldId} on attempt {Attempt}.", world.Name, world.Id, attempt);
                return response;
            }

            Log.Warning("PaissaDB rate-limited {World}#{WorldId}; retrying once after shared cooldown until {RetryAfter:O}. Attempt {Attempt}/{MaxAttempts}.",
                world.Name, world.Id, response.RetryAfterUtc ?? paissaApiClient.CooldownUntilUtc, attempt, maxAttempts);
        }
    }

    private void ApplyHousingWorldResult(PaissaWorld world, PaissaApiResponse<PaissaWorldInspection>? response, string? error)
    {
        housingBatchCompleted++;
        mainWindow.SetHousingRefreshProgress(housingBatchCompleted, housingBatchExpected);
        if (response is null)
        {
            housingBatchFailed++;
            mainWindow.SetHousingWorldData(world.Id, null, $"Refresh failed; last good data retained: {error}");
            if (!string.Equals(error, "Request cancelled.", StringComparison.Ordinal))
                RecordDiagnostic(DiagnosticSeverity.Error, $"Housing browser refresh failed for {world.Name}: {error}");
            return;
        }

        var status = FormatApiStatus(response.Url, response.StatusCode, response.Duration, response.FetchedAtUtc, response.Error);
        if (!response.IsSuccess || response.Data is null)
        {
            housingBatchFailed++;
            mainWindow.SetHousingWorldData(world.Id, null, $"Refresh failed; last good data retained · {status}");
            if (response.Error == "Request cancelled.")
                return;
            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                housingRateLimitedWorldCount++;
                RecordDiagnostic(DiagnosticSeverity.Warning,
                    $"Housing cache batch {housingBatchId}: {world.Name}#{world.Id} HTTP 429; Retry-After={response.RetryAfterUtc?.ToString("O") ?? "fallback cooldown"}; last-good retained; completed={housingBatchCompleted}/{housingBatchExpected}.");
            }
            else
                RecordDiagnostic(DiagnosticSeverity.Warning, $"Housing browser refresh failed for {world.Name}: {status}");
            return;
        }

        var inspection = response.Data;
        if (inspection.Id != world.Id)
        {
            housingBatchFailed++;
            mainWindow.SetHousingWorldData(world.Id, null,
                $"Invalid response: requested world {world.Id}, received {inspection.Id}; last good data retained.");
            RecordDiagnostic(DiagnosticSeverity.Warning,
                $"PaissaDB returned world {inspection.Id} for requested world {world.Id}; response rejected.");
            return;
        }

        var warnings = inspection.Warnings.ToList();
        if (!string.Equals(inspection.Name, world.Name, StringComparison.Ordinal))
            warnings.Add($"World name differs from directory: '{inspection.Name}'.");
        if (warnings.Count > 0 && mainWindow.HasHousingSnapshot(world.Id))
        {
            housingBatchFailed++;
            mainWindow.SetHousingWorldData(world.Id, null,
                $"Partial response rejected; last good data retained ({warnings.Count} issue(s)) · {status}");
            RecordDiagnostic(DiagnosticSeverity.Warning,
                $"Housing browser rejected partial refresh for {world.Name}; retained cached listings. Issues: {string.Join("; ", warnings.Take(8))}");
            return;
        }
        var snapshot = new PaissaHousingWorldData(world, response.FetchedAtUtc, inspection.Listings, warnings);
        var loadedStatus = warnings.Count == 0
            ? $"Loaded {inspection.Listings.Count} plots · {status}"
            : $"Loaded {inspection.Listings.Count} plots with {warnings.Count} schema issue(s) · {status}";
        mainWindow.SetHousingWorldData(world.Id, snapshot, loadedStatus);
        housingSnapshots[world.Id] = snapshot;
        housingBatchSucceeded++;
        RecordVerbose(
            $"Housing cache batch {housingBatchId}: accepted {world.Name}#{world.Id}; plots={inspection.Listings.Count}; downloaded={response.FetchedAtUtc:O}; completed={housingBatchCompleted}/{housingBatchExpected}.");
        SaveHousingCache();
        var loadedMessage = $"Housing browser loaded {world.Name}: {inspection.Listings.Count} plots, {inspection.Districts.Count} districts, {warnings.Count} response issue(s); {status}.";
        if (warnings.Count == 0)
            RecordVerbose(loadedMessage);
        else
            RecordDiagnostic(DiagnosticSeverity.Warning, loadedMessage);
    }

    private void UpdateBidCharacter()
    {
        var tick = Environment.TickCount64;
        if (tick < nextBidCharacterRefreshTick) return;
        nextBidCharacterRefreshTick = tick + 1000;
        try
        {
            if (PlayerState.IsLoaded && PlayerState.ContentId != 0 && PlayerState.HomeWorld.RowId > 0)
            {
                bidTracker.ObserveCharacter(new TrackedCharacter(PlayerState.ContentId, PlayerState.CharacterName,
                    (int)PlayerState.HomeWorld.RowId, PlayerState.HomeWorld.Value.Name.ToString()));
                return;
            }
        }
        catch (Exception)
        {
            // Transient game state is retried on the framework thread. Never log character identifiers.
        }
        bidTracker.ObserveCharacter(null);
    }

    private void UpdateHomeWorldSelection()
    {
        try
        {
            if (PlayerState.IsLoaded)
            {
                var rowId = PlayerState.HomeWorld.RowId;
                if (rowId > 0 && rowId <= int.MaxValue)
                {
                    mainWindow.SetHomeWorld((int)rowId, PlayerState.ContentId);
                    homeWorldLookupFailureLogged = false;
                    return;
                }
            }
            mainWindow.SetHomeWorld(null, null);
        }
        catch (Exception ex)
        {
            mainWindow.SetHomeWorld(null, null);
            if (!homeWorldLookupFailureLogged)
            {
                homeWorldLookupFailureLogged = true;
                Log.Debug(ex, "Home-world selection unavailable; shortcut remains unavailable until game state is ready.");
            }
        }
    }

    private void LoadHousingCache()
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            if (!File.Exists(housingCachePath))
            {
                RecordDiagnostic(DiagnosticSeverity.Debug, $"Housing cache load: no cache file at '{housingCachePath}'; startup will populate it.");
                return;
            }
            var entries = JsonSerializer.Deserialize<List<HousingCacheEntry>>(File.ReadAllText(housingCachePath)) ?? [];
            foreach (var entry in entries)
            {
                if (entry.World is null || entry.Listings is null)
                    continue;
                var data = new PaissaHousingWorldData(entry.World, entry.DownloadedAtUtc, entry.Listings, entry.Warnings ?? []);
                housingSnapshots[entry.World.Id] = data;
            }
            if (housingSnapshots.Count > 0)
                mainWindow.SetHousingWorlds(housingSnapshots.Values.Select(snapshot => snapshot.World).ToArray(),
                    $"Loaded {housingSnapshots.Count} cached worlds; checking directory");
            var plots = housingSnapshots.Values.Sum(snapshot => snapshot.Listings.Count);
            var oldest = housingSnapshots.Values.Select(snapshot => snapshot.DownloadedAtUtc).DefaultIfEmpty().Min();
            stopwatch.Stop();
            RecordDiagnostic(DiagnosticSeverity.Debug,
                $"Housing cache load complete: path='{housingCachePath}'; worlds={housingSnapshots.Count}; plots={plots}; oldest-download={(oldest == default ? "unknown" : oldest.ToString("O"))}; bytes={new FileInfo(housingCachePath).Length}; elapsed={stopwatch.ElapsedMilliseconds}ms.");
        }
        catch (Exception ex)
        {
            RecordDiagnostic(DiagnosticSeverity.Warning,
                $"Housing cache load failed at '{housingCachePath}' after {stopwatch.ElapsedMilliseconds}ms: {ex.GetType().Name}: {ex.Message}; starting with {housingSnapshots.Count} restored world(s).");
        }
    }

    private void SaveHousingCache()
    {
        var stopwatch = Stopwatch.StartNew();
        var plots = housingSnapshots.Values.Sum(snapshot => snapshot.Listings.Count);
        try
        {
            var entries = housingSnapshots.Values.Select(snapshot => new HousingCacheEntry(
                snapshot.World, snapshot.DownloadedAtUtc, snapshot.Listings.ToArray(), snapshot.Warnings.ToArray())).ToArray();
            var tempPath = housingCachePath + ".tmp";
            Directory.CreateDirectory(Path.GetDirectoryName(housingCachePath)!);
            File.WriteAllText(tempPath, JsonSerializer.Serialize(entries));
            File.Move(tempPath, housingCachePath, true);
            stopwatch.Stop();
            RecordVerbose(
                $"Housing cache save complete: path='{housingCachePath}'; worlds={entries.Length}; plots={plots}; bytes={new FileInfo(housingCachePath).Length}; elapsed={stopwatch.ElapsedMilliseconds}ms.");
        }
        catch (Exception ex)
        {
            RecordDiagnostic(DiagnosticSeverity.Warning,
                $"Housing cache save failed at '{housingCachePath}' after {stopwatch.ElapsedMilliseconds}ms: {ex.GetType().Name}: {ex.Message}; in-memory cache has {housingSnapshots.Count} worlds/{plots} plots.");
        }
    }

    private void OnPaissaRequestStarted(string url, TimeSpan pacingWait)
        => Log.Verbose("PaissaDB request started: {Url}; pacing/cooldown wait={WaitMs}ms.", url, (long)pacingWait.TotalMilliseconds);

    private void RecordVerbose(string message)
        => Log.Verbose("{DiagnosticResult}", message);

    private static DateTimeOffset MaxDate(DateTimeOffset left, DateTimeOffset right) => left > right ? left : right;

    private sealed record HousingCacheEntry(PaissaWorld World, DateTimeOffset DownloadedAtUtc,
        PaissaPlotListing[] Listings, string[]? Warnings);

    private void StartHousingRequest()
    {
        housingRequestPending = true;
        mainWindow.SetHousingRequestPending(true);
        housingRequestCancellation?.Dispose();
        housingRequestCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetimeCancellation.Token);
    }

    private void CancelHousingRequest()
    {
        if (!housingRequestPending || housingRequestCancellation is null)
            return;
        housingRequestCancellation.Cancel();
        RecordDiagnostic(DiagnosticSeverity.Debug, "Housing browser request cancellation requested.");
    }

    private void FinishHousingRequest()
    {
        housingRequestPending = false;
        mainWindow.SetHousingRequestPending(false);
        housingRequestCancellation?.Dispose();
        housingRequestCancellation = null;
    }

    private void RecordDiagnostic(DiagnosticSeverity severity, string message)
    {
        diagnostics.Record(severity, message);
        switch (severity)
        {
            case DiagnosticSeverity.Warning:
                Log.Warning("{DiagnosticResult}", message);
                break;
            case DiagnosticSeverity.Error:
                Log.Error("{DiagnosticResult}", message);
                break;
            default:
                Log.Debug("{DiagnosticResult:l}", message);
                break;
        }
    }

    private void OnActivePluginsChanged(IActivePluginsChangedEventArgs args) => RefreshDependencyStatus();

    private OwnedCommandRegistration CreateCommandRegistration(string command, string help, bool showInHelp)
    {
        CommandInfo? ownInfo = null;
        return new OwnedCommandRegistration(
            command,
            () =>
            {
                ownInfo = new CommandInfo(OnCommand)
                {
                    HelpMessage = help,
                    ShowInHelp = showInHelp,
                };
                return Commands.AddHandler(command, ownInfo);
            },
            () => ownInfo is not null && Commands.Commands.TryGetValue(command, out var current) && current.Handler == ownInfo.Handler,
            () => Commands.Commands.ContainsKey(command),
            () => Commands.RemoveHandler(command));
    }

    private void RefreshCommandStatus()
    {
        diagnostics.MainCommandStatus = mainCommandRegistration.GetStatus();
        diagnostics.AliasCommandStatus = aliasCommandRegistration.GetStatus();
    }

    private bool RefreshDependencyStatus()
    {
        try
        {
            var loadedNames = PluginInterface.InstalledPlugins
                .Where(plugin => plugin.IsLoaded)
                .Select(plugin => plugin.InternalName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            diagnostics.SetLifestreamStatus(FindDependency(loadedNames, "Lifestream"));
            return true;
        }
        catch (Exception ex)
        {
            diagnostics.SetLifestreamStatus("Unknown");
            diagnostics.SetLastError($"Could not inspect installed plugins: {ex.Message}");
            Log.Warning(ex, "Could not inspect installed plugin metadata.");
            return false;
        }
    }

    private static string FindDependency(HashSet<string> loadedNames, params string[] candidates)
    {
        if (candidates.Any(loadedNames.Contains))
            return "Detected (compatibility not validated)";

        return "Not detected";
    }
}
