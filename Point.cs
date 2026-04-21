using Godot;

public sealed class Point
{
    private readonly float _baseDamping;
    private float _damping;

    public Point(Vector3 position, float inverseMass, float baseDamping = 0.98f)
    {
        OriginalPosition = position;
        Position = position;
        InverseMass = inverseMass;
        _baseDamping = baseDamping;
        _damping = baseDamping;
        Velocity = Vector3.Zero;
        Acceleration = Vector3.Zero;
    }

    public Vector3 OriginalPosition { get; }

    public Vector3 Position { get; set; }

    public Vector3 Velocity { get; set; }

    public Vector3 Acceleration { get; set; }

    public float InverseMass { get; }

    public void ApplyForce(Vector3 force)
    {
        Acceleration += force * InverseMass;
    }

    public void IncreaseDamping(float factor)
    {
        _damping *= factor;
    }

    public void Reset()
    {
        Position = OriginalPosition;
        Velocity = Vector3.Zero;
        Acceleration = Vector3.Zero;
        _damping = _baseDamping;
    }

    public void Update()
    {
        Velocity += Acceleration;
        Position += Velocity;
        Acceleration = Vector3.Zero;

        if (Velocity.LengthSquared() < 0.001f * 0.001f)
        {
            Velocity = Vector3.Zero;
        }

        Velocity *= _damping;
        _damping = _baseDamping;
    }
}
