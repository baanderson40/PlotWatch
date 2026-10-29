using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace PlotWatch.Windows;

internal static class PlotWatchTheme
{
    private static readonly Vector4 Background = FromRgb(0x04, 0x1f, 0x1f);
    private static readonly Vector4 Inactive = FromRgb(0x18, 0x3a, 0x37);
    private static readonly Vector4 Text = FromRgb(0xef, 0xd6, 0xac);
    private static readonly Vector4 Selected = FromRgb(0xc4, 0x49, 0x00);

    private static readonly (ImGuiCol Color, Vector4 Value)[] Colors =
    [
        (ImGuiCol.Text, Text),
        (ImGuiCol.TextDisabled, Inactive),
        (ImGuiCol.WindowBg, Background),
        (ImGuiCol.ChildBg, Background),
        (ImGuiCol.PopupBg, Background),
        (ImGuiCol.Border, Inactive),
        (ImGuiCol.FrameBg, Inactive),
        (ImGuiCol.FrameBgHovered, Selected),
        (ImGuiCol.FrameBgActive, Selected),
        (ImGuiCol.TitleBg, Background),
        (ImGuiCol.TitleBgActive, Inactive),
        (ImGuiCol.MenuBarBg, Inactive),
        (ImGuiCol.ScrollbarBg, Background),
        (ImGuiCol.ScrollbarGrab, Inactive),
        (ImGuiCol.ScrollbarGrabHovered, Selected),
        (ImGuiCol.ScrollbarGrabActive, Selected),
        (ImGuiCol.CheckMark, Selected),
        (ImGuiCol.SliderGrab, Inactive),
        (ImGuiCol.SliderGrabActive, Selected),
        (ImGuiCol.Button, Inactive),
        (ImGuiCol.ButtonHovered, Selected),
        (ImGuiCol.ButtonActive, Selected),
        (ImGuiCol.Header, Inactive),
        (ImGuiCol.HeaderHovered, Selected),
        (ImGuiCol.HeaderActive, Selected),
        (ImGuiCol.Separator, Inactive),
        (ImGuiCol.SeparatorHovered, Selected),
        (ImGuiCol.SeparatorActive, Selected),
        (ImGuiCol.ResizeGrip, Inactive),
        (ImGuiCol.ResizeGripHovered, Selected),
        (ImGuiCol.ResizeGripActive, Selected),
        (ImGuiCol.Tab, Inactive),
        (ImGuiCol.TabHovered, Selected),
        (ImGuiCol.TabActive, Selected),
        (ImGuiCol.TabUnfocused, Inactive),
        (ImGuiCol.TabUnfocusedActive, Selected),
        (ImGuiCol.TableHeaderBg, Inactive),
        (ImGuiCol.TableBorderStrong, Inactive),
        (ImGuiCol.TableBorderLight, Inactive),
        (ImGuiCol.TableRowBg, Background),
        (ImGuiCol.TableRowBgAlt, Inactive),
        (ImGuiCol.TextSelectedBg, Selected),
        (ImGuiCol.DragDropTarget, Selected),
        (ImGuiCol.NavHighlight, Selected),
    ];

    public static void Push()
    {
        foreach (var (color, value) in Colors)
            ImGui.PushStyleColor(color, value);
    }

    public static void Pop() => ImGui.PopStyleColor(Colors.Length);

    private static Vector4 FromRgb(byte red, byte green, byte blue)
        => new(red / 255f, green / 255f, blue / 255f, 1f);
}
