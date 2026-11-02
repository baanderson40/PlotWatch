using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using PlotWatch.Travel;

namespace PlotWatch.Windows;

public sealed partial class MainWindow
{
    private readonly PlotTravel plotTravel;
    private readonly Action<PlotDestination> requestTravel;
    private readonly Action stopTravel;
    private Vector2 plotContextPopupPosition;

    // Call immediately after the target item, before any tooltip submits another window.
    private bool BeginPlotContextPopup(string id)
    {
        if (ImGui.IsItemHovered() && ImGui.IsMouseReleased(ImGuiMouseButton.Right))
        {
            plotContextPopupPosition = ImGui.GetMousePos();
            ImGui.OpenPopup(id);
        }
        if (ImGui.IsPopupOpen(id))
            ImGui.SetNextWindowPos(plotContextPopupPosition, ImGuiCond.Appearing);
        if (!ImGui.BeginPopup(id)) return false;
        // Keep the captured anchor stable as the mouse moves; clamp after autosizing near edges.
        var viewport = ImGui.GetWindowViewport();
        var maximum = Vector2.Max(viewport.WorkPos, viewport.WorkPos + viewport.WorkSize - ImGui.GetWindowSize());
        ImGui.SetWindowPos(Vector2.Clamp(plotContextPopupPosition, viewport.WorkPos, maximum));
        return true;
    }

    private static void DrawPlotMenuNote(string text)
    {
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + 320f * ImGuiHelpers.GlobalScale);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
    }

    private void DrawTravelAction(PlotDestination destination)
    {
        var reason = plotTravel.GetDisabledReason(destination);
        if (ImGui.MenuItem("Travel to plot", "", false, reason is null)) requestTravel(destination);
        if (reason is not null) DrawPlotMenuNote(reason);
        DrawPlotMenuNote("Uses Lifestream settings and housing paths. Travel only; no placard interaction or bidding.");
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
