using System.Numerics;

namespace PlotWatch.Windows;

/// <summary>Place a premeasured tooltip before rendering, not using last frame's window size.</summary>
public static class TooltipPlacement
{
    public static Vector2 NearCursor(Vector2 mouse, Vector2 size, Vector2 workPosition, Vector2 workSize, Vector2 offset, float margin)
    {
        var minimum = workPosition + new Vector2(margin);
        var maximum = Vector2.Max(minimum, workPosition + workSize - new Vector2(margin) - size);
        var position = mouse + offset;
        if (position.X > maximum.X) position.X = mouse.X - offset.X - size.X;
        if (position.Y > maximum.Y) position.Y = mouse.Y - offset.Y - size.Y;
        return Vector2.Clamp(position, minimum, maximum);
    }
}
