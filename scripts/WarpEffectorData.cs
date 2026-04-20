using Godot;

namespace WarpGrid;

public enum WarpShapeType    : uint { Radial = 0, Line = 1 }
public enum WarpBehaviorType : uint
{
    Force = 0,
    Impulse = 1,
    Vortex = 2,
    GravityWell = 3,
    Shockwave = 4,
}

public struct WarpEffectorData
{
    public Vector2 StartPoint;
    public Vector2 EndPoint;
    public float   Radius;
    public float   Strength;
    public float   AnimatedRadius;
    public float   RingWidth;
    public uint    ShapeType;
    public uint    BehaviorType;
}
