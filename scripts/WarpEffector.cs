using Godot;

namespace WarpGrid;

[GlobalClass]
public partial class WarpEffector : Node2D
{
    [Export] public WarpShapeType    Shape    = WarpShapeType.Radial;
    [Export] public WarpBehaviorType Behavior = WarpBehaviorType.Force;
    [Export] public float   Radius   = 200.0f;   // pixels
    [Export] public float   Strength = 1.0f;
    // For Line shape: EndOffset is in pixels, relative to GlobalPosition.
    // For Radial: EndOffset is ignored (StartPoint == EndPoint == GlobalPosition).
    [Export] public Vector2 EndOffset = Vector2.Right * 100.0f;
    // For Radial-Directed, you can pack direction in EndOffset as the "end" point;
    // the shader picks up direction from (end - start) when shape == Radial and |end-start| > 0.

    public WarpEffectorData ToData(Vector2 gridOrigin, Vector2 gridSizePixels)
    {
        var startNorm = (GlobalPosition - gridOrigin) / gridSizePixels;
        var endNorm   = Shape == WarpShapeType.Line
            ? (GlobalPosition + EndOffset - gridOrigin) / gridSizePixels
            : startNorm;
        return new WarpEffectorData
        {
            StartPoint   = startNorm,
            EndPoint     = endNorm,
            Radius       = Radius / gridSizePixels.X,
            Strength     = Strength,
            ShapeType    = (uint)Shape,
            BehaviorType = (uint)Behavior,
        };
    }
}
