using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace PlotWatch.Travel;

public sealed class LifestreamTravelBackend : IPlotTravelBackend
{
    private readonly IDataManager data;
    private readonly ICallGateSubscriber<bool> busy;
    private readonly ICallGateSubscriber<string, bool> sameDc;
    private readonly ICallGateSubscriber<string, bool> crossDc;
    private readonly ICallGateSubscriber<HousingTravelAddress, object> go;
    private readonly ICallGateSubscriber<object> abort;

    public LifestreamTravelBackend(IDalamudPluginInterface pluginInterface, IDataManager data)
    {
        this.data = data;
        busy = pluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy");
        sameDc = pluginInterface.GetIpcSubscriber<string, bool>("Lifestream.CanVisitSameDC");
        crossDc = pluginInterface.GetIpcSubscriber<string, bool>("Lifestream.CanVisitCrossDC");
        go = pluginInterface.GetIpcSubscriber<HousingTravelAddress, object>("Lifestream.GoToHousingAddress");
        abort = pluginInterface.GetIpcSubscriber<object>("Lifestream.Abort");
    }

    public bool Available => busy.HasFunction && sameDc.HasFunction && crossDc.HasFunction && go.HasAction && abort.HasAction;
    public bool IsBusy() => busy.InvokeFunc();

    private string? WorldName(int id)
        => data.GetExcelSheet<World>().TryGetRow((uint)id, out var world) && world.IsPublic
            ? world.Name.ToString() : null;

    public bool CanVisit(int worldId)
        => WorldName(worldId) is { } name && (sameDc.InvokeFunc(name) || crossDc.InvokeFunc(name));

    public void Go(PlotDestination destination)
    {
        if (PlotTravelPolicy.Validate(destination) is { } error) throw new ArgumentException(error);
        // Resolve by ID, never trust a manually entered bid's world-name spelling.
        var name = WorldName(destination.WorldId!.Value) ?? throw new ArgumentException("Unknown world.");
        // House=0. Address-book tuple uses one-based ward/plot; Lifestream converts internally.
        // GoToHousingAddress constructs a transient entry; it does not save an address-book entry.
        go.InvokeAction(PlotTravelPolicy.BuildAddress(destination with { WorldName = name }));
    }

    public void Abort() => abort.InvokeAction();
}
