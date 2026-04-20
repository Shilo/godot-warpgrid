using System;
using Godot;

namespace WarpGrid;

[GlobalClass, Tool]
public partial class WarpEffector : Node2D
{
    public const string Group = "warp_effectors";

    [Export] public WarpShapeType Shape = WarpShapeType.Radial;
    [Export] public WarpBehaviorType Behavior = WarpBehaviorType.Force;
    [Export] public float Radius = 300.0f;
    [Export] public float Strength = 1.0f;
    [Export] public Vector2 EndOffset = Vector2.Zero;
    [Export] public float ShockwaveSpeed = 240.0f;
    [Export] public float ShockwaveWidth = 36.0f;

    double _spawnTimeSeconds;

    public override void _Ready()
    {
        _spawnTimeSeconds = Time.GetTicksMsec() / 1000.0;
        AddToGroup(Group);
    }

    public override void _ExitTree()
    {
        if (IsInGroup(Group)) RemoveFromGroup(Group);
    }

    public WarpEffectorData ToData(Vector2 gridOrigin, Vector2 gridSizePixels)
    {
        // CPU mass-spring input: keep the effector in pixel space so the solver can
        // consume the same coordinates the scene uses for placement.
        _ = gridSizePixels;
        var startPx = GlobalPosition - gridOrigin;
        var endPx = startPx + EndOffset;
        float ageSeconds = (float)(Time.GetTicksMsec() / 1000.0 - _spawnTimeSeconds);
        bool isShockwave = Behavior == WarpBehaviorType.Shockwave;

        return new WarpEffectorData
        {
            StartPoint = startPx,
            EndPoint = endPx,
            Radius = Radius,
            Strength = Strength,
            AnimatedRadius = isShockwave
                ? Radius + (ShockwaveSpeed * Math.Max(ageSeconds, 0.0f))
                : Radius,
            RingWidth = isShockwave ? Math.Max(ShockwaveWidth, 1.0f) : 0.0f,
            ShapeType = (uint)Shape,
            BehaviorType = (uint)Behavior,
        };
    }
}
