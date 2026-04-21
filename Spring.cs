using Godot;

namespace GodotWarpGridTest;

public sealed class Spring
{
    public Point End1;
    public Point End2;
    public float Stiffness;
    public float Damping;
    public float TargetLength;

    public Spring(Point end1, Point end2, float stiffness, float damping)
    {
        End1 = end1;
        End2 = end2;
        Stiffness = stiffness;
        Damping = damping;
        TargetLength = end1.Position.DistanceTo(end2.Position) * 0.95f;
    }

    public void Update()
    {
        Vector3 x = End1.Position - End2.Position;
        float length = x.Length();

        if (length <= TargetLength)
        {
            return;
        }

        x = (x / length) * (length - TargetLength);
        Vector3 dv = End2.Velocity - End1.Velocity;
        Vector3 force = Stiffness * x - dv * Damping;
        End1.ApplyForce(-force);
        End2.ApplyForce(force);
    }
}
