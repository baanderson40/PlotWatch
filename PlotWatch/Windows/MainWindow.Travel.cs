using Dalamud.Bindings.ImGui;
using PlotWatch.Travel;

namespace PlotWatch.Windows;

public sealed partial class MainWindow
{
    private readonly PlotTravel plotTravel;
    private readonly Action<PlotDestination> requestTravel;
    private readonly Action stopTravel;

    private void DrawTravelAction(PlotDestination destination)
    {
        var reason = plotTravel.GetDisabledReason(destination);
        if (ImGui.MenuItem("Travel to plot", "", false, reason is null)) requestTravel(destination);
        if (reason is not null) ImGui.TextWrapped(reason);
        ImGui.TextWrapped("Uses Lifestream settings and housing paths. Travel only; no placard interaction or bidding.");
    }

    private void DrawTravelStatus()
    {
        ImGui.Separator();
        ImGui.TextWrapped(plotTravel.Status);
        if (plotTravel.Destination is { } destination)
            ImGui.TextWrapped($"Requested destination: {destination.Label}");
        if (plotTravel.Busy)
            ImGui.TextUnformatted(plotTravel.OwnsRequest ? "Lifestream: busy (PW travel requested)" : "Lifestream: busy");
        else
            ImGui.TextDisabled(plotTravel.Available ? "Lifestream: idle" : "Lifestream: unavailable");
        if (plotTravel.CanStop)
        {
            if (ImGui.SmallButton("Stop travel")) stopTravel();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Aborts Lifestream's current task queue and movement, including any other queued tasks.");
        }
    }
}
