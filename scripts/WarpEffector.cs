using Godot;

namespace WarpGrid;

[GlobalClass]
public partial class WarpEffector : Node2D
{
    public const string Group = "warp_effectors";

    [Export] public WarpShapeType Shape = WarpShapeType.Radial;
    [Export] public WarpBehaviorType Behavior = WarpBehaviorType.Force;
    [Export] public float Radius = 300.0f;
    [Export] public float Strength = 0.01f;
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
        float minDim = Mathf.Min(gridSizePixels.X, gridSizePixels.Y);
        var startPx = GlobalPosition - gridOrigin;
        var endPx   = startPx + EndOffset;
        return new WarpEffectorData
        {
            StartPoint   = new Vector2(startPx.X / gridSizePixels.X, startPx.Y / gridSizePixels.Y),
            EndPoint     = new Vector2(endPx.X   / gridSizePixels.X, endPx.Y   / gridSizePixels.Y),
            Radius       = Radius / minDim,
            Strength     = Strength,
            ShapeType    = (uint)Shape,
            BehaviorType = (uint)Behavior,
        };
    }
}
