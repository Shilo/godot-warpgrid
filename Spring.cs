using Godot;

public struct Spring
{
    public Point End1;
    public Point End2;
    public float TargetLength;
    public float Stiffness;
    public float Damping;

    public Spring(Point end1, Point end2, float stiffness, float damping, float targetLengthScale = 0.95f)
    {
        End1 = end1;
        End2 = end2;
        Stiffness = stiffness;
        Damping = damping;
        TargetLength = end1.Position.DistanceTo(end2.Position) * targetLengthScale;
    }

    public void Update()
    {
        Vector3 x = End1.Position - End2.Position;
        float length = x.Length();

        // These springs can only pull, not push.
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
