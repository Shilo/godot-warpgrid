using Godot;

namespace WarpGrid;

[GlobalClass]
public partial class WarpEffector : Node2D
{
    public const string Group = "warp_effectors";

    [Export] public WarpShapeType Shape = WarpShapeType.Radial;
    [Export] public WarpBehaviorType Behavior = WarpBehaviorType.Force;
    [Export] public float Radius = 300.0f;
    [Export] public float Strength = 1.0f;
    [Export] public Vector2 EndOffset = Vector2.Zero;

    public override void _Ready()
    {
        AddToGroup(Group);
    }

    public override void _ExitTree()
    {
        if (IsInGroup(Group)) RemoveFromGroup(Group);
    }

    public WarpEffectorData ToData(Vector2 gridOrigin, Vector2 gridSizePixels)
    {
        // Phase 7: pixel-space physics — positions and radius feed the shader as ABSOLUTE
        // PIXELS (same frame as node positions). No normalization; no min-dim scaling.
        var startPx = GlobalPosition - gridOrigin;
        var endPx   = startPx + EndOffset;
        return new WarpEffectorData
        {
            StartPoint   = startPx,
            EndPoint     = endPx,
            Radius       = Radius,
            Strength     = Strength,
            ShapeType    = (uint)Shape,
            BehaviorType = (uint)Behavior,
        };
    }
}
