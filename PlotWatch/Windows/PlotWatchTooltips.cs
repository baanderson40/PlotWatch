using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace PlotWatch.Windows;

public static class PlotWatchTooltips
{
    public static void Show(params string[] lines)
    {
        var text = string.Join("\n", lines);
        if (text.Length == 0) return;
        var viewport = ImGui.GetWindowViewport();
        var scale = ImGuiHelpers.GlobalScale;
        var padding = ImGui.GetStyle().WindowPadding;
        var margin = 8f * scale;
        var available = Vector2.Max(Vector2.One, viewport.WorkSize - new Vector2(2f * margin));
        var wrapWidth = Math.Max(1f, Math.Min(520f * scale, available.X - 2f * padding.X));
        var size = Vector2.Min(available, ImGui.CalcTextSize(text, false, wrapWidth) + 2f * padding);
        var position = TooltipPlacement.NearCursor(ImGui.GetMousePos(), size, viewport.WorkPos, viewport.WorkSize,
            new Vector2(16f, 20f) * scale, margin);
        // Set position and measured size before the first draw, avoiding stale auto-fit dimensions.
        ImGui.SetNextWindowPos(position, ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + wrapWidth);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }
}
