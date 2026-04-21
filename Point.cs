using Godot;

namespace GodotWarpGridTest;

public sealed class Point
{
    public const float BaseDamping = 0.98f;

    public Vector3 Position;
    public Vector3 Velocity;
    public float InverseMass;

    private Vector3 _acceleration;
    private float _currentDamping = BaseDamping;

    public Point(Vector3 position, float inverseMass)
    {
        Position = position;
        InverseMass = inverseMass;
    }

    public void ApplyForce(Vector3 force)
    {
        _acceleration += force * InverseMass;
    }

    public void IncreaseDamping(float factor)
    {
        _currentDamping *= factor;
    }

    public void Update()
    {
        Velocity += _acceleration;
        Position += Velocity;
        _acceleration = Vector3.Zero;
        Velocity *= _currentDamping;
        _currentDamping = BaseDamping;
    }
}
