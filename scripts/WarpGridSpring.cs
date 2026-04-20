namespace WarpGrid;

public enum GridSpringType
{
    Right,
    Up,
    OriginalPosition,
}

public readonly struct WarpGridSpring
{
    public readonly int PointA;
    public readonly int PointB;
    public readonly GridSpringType Type;
    public readonly float RestLength;

    public WarpGridSpring(int pointA, int pointB, GridSpringType type, float restLength)
    {
        PointA = pointA;
        PointB = pointB;
        Type = type;
        RestLength = restLength;
    }
}
