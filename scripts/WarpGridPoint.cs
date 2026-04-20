using SimVector2 = System.Numerics.Vector2;

namespace WarpGrid;

public struct WarpGridPoint
{
    public SimVector2 Position;
    public SimVector2 Velocity;
    public SimVector2 Acceleration;
    public SimVector2 Anchor;

    public WarpGridPoint(SimVector2 anchor)
    {
        Position = anchor;
        Velocity = SimVector2.Zero;
        Acceleration = SimVector2.Zero;
        Anchor = anchor;
    }
}
