using System.Numerics;
using PlotWatch.Windows;

internal static class TooltipPlacementTests
{
    public static readonly (string Name, Action Test)[] Cases =
    [
        ("tracked tooltip starts near cursor without prior window dimensions", FirstFrame),
        ("tracked tooltip flips left and above near viewport edges", Edges),
        ("tracked tooltip supports negative-coordinate viewports", NegativeViewport),
        ("tracked tooltip clamps to viewport margins as content changes", ContentChanges),
    ];
    private static void Equal(Vector2 expected, Vector2 actual)
    {
        if (expected != actual) throw new Exception($"Expected {expected}; got {actual}.");
    }
    private static readonly Vector2 Offset = new(16, 20);

    private static void FirstFrame()
        => Equal(new(216, 170), TooltipPlacement.NearCursor(new(200, 150), new(300, 100), Vector2.Zero, new(1920, 1080), Offset, 8));

    private static void Edges()
        => Equal(new(1584, 940), TooltipPlacement.NearCursor(new(1900, 1060), new(300, 100), Vector2.Zero, new(1920, 1080), Offset, 8));

    private static void NegativeViewport()
        => Equal(new(-326, 940), TooltipPlacement.NearCursor(new(-10, 1060), new(300, 100), new(-1920, 0), new(1920, 1080), Offset, 8));

    private static void ContentChanges()
    {
        var mouse = new Vector2(700, 250);
        Equal(new(716, 270), TooltipPlacement.NearCursor(mouse, new(60, 10), Vector2.Zero, new(800, 300), Offset, 8));
        Equal(new(184, 30), TooltipPlacement.NearCursor(mouse, new(500, 200), Vector2.Zero, new(800, 300), Offset, 8));
        Equal(new(8, 8), TooltipPlacement.NearCursor(mouse, new(784, 284), Vector2.Zero, new(800, 300), Offset, 8));
    }
}
