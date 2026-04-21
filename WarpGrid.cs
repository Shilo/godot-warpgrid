using System;
using System.Collections.Generic;
using Godot;

namespace GodotWarpGridTest;

public sealed class WarpGrid
{
    public const float MainSpringStiffness = 0.28f;
    public const float MainSpringDamping = 0.06f;
    public const float PerspectiveDepthDefault = 2000.0f;

    public float PerspectiveDepth { get; set; } = PerspectiveDepthDefault;

    public Point[,] Points { get; private set; } = new Point[0, 0];
    public Spring[] Springs { get; private set; } = Array.Empty<Spring>();
    public Vector2 ScreenCenter { get; private set; }

    private bool _wasRightMousePressed;

    public void Initialize(Rect2 size, Vector2 spacing, Vector2? screenCenter = null)
    {
        var springList = new List<Spring>();

        int numColumns = (int)(size.Size.X / spacing.X) + 1;
        int numRows = (int)(size.Size.Y / spacing.Y) + 1;

        Points = new Point[numColumns, numRows];
        Point[,] fixedPoints = new Point[numColumns, numRows];

        int column = 0;
        int row = 0;
        for (float y = size.Position.Y; y <= size.End.Y; y += spacing.Y)
        {
            for (float x = size.Position.X; x <= size.End.X; x += spacing.X)
            {
                Points[column, row] = new Point(new Vector3(x, y, 0f), 1f);
                fixedPoints[column, row] = new Point(new Vector3(x, y, 0f), 0f);
                column++;
            }

            row++;
            column = 0;
        }

        for (int y = 0; y < numRows; y++)
        {
            for (int x = 0; x < numColumns; x++)
            {
                if (x == 0 || y == 0 || x == numColumns - 1 || y == numRows - 1)
                {
                    springList.Add(new Spring(fixedPoints[x, y], Points[x, y], 0.1f, 0.1f));
                }
                else if (x % 3 == 0 && y % 3 == 0)
                {
                    springList.Add(new Spring(fixedPoints[x, y], Points[x, y], 0.002f, 0.02f));
                }

                if (x > 0)
                {
                    springList.Add(new Spring(Points[x - 1, y], Points[x, y], MainSpringStiffness, MainSpringDamping));
                }

                if (y > 0)
                {
                    springList.Add(new Spring(Points[x, y - 1], Points[x, y], MainSpringStiffness, MainSpringDamping));
                }
            }
        }

        Springs = springList.ToArray();
        ScreenCenter = screenCenter ?? (size.Position + size.Size / 2f);
        _wasRightMousePressed = false;
    }

    public void ProcessInteraction(Vector3 mousePosition, bool leftPressed, bool rightPressed)
    {
        if (leftPressed)
        {
            ApplyDirectedForce(new Vector3(0f, 0f, 3000f), mousePosition, 50f);
        }

        if (rightPressed)
        {
            ApplyExplosiveForce(3000f, mousePosition, 80f, increaseDamping: false);
        }

        if (rightPressed && !_wasRightMousePressed)
        {
            ApplyExplosiveForce(3000f, mousePosition, 80f);
        }

        _wasRightMousePressed = rightPressed;
    }

    public void UpdateSimulation()
    {
        foreach (Spring spring in Springs)
        {
            spring.Update();
        }

        foreach (Point mass in Points)
        {
            mass.Update();
        }
    }

    public void ApplyDirectedForce(Vector3 force, Vector3 position, float radius)
    {
        foreach (Point mass in Points)
        {
            if (position.DistanceSquaredTo(mass.Position) < radius * radius)
            {
                mass.ApplyForce(10f * force / (10f + position.DistanceTo(mass.Position)));
            }
        }
    }

    public void ApplyImplosiveForce(float force, Vector3 position, float radius)
    {
        foreach (Point mass in Points)
        {
            float dist2 = position.DistanceSquaredTo(mass.Position);
            if (dist2 < radius * radius)
            {
                mass.ApplyForce(10f * force * (position - mass.Position) / (100f + dist2));
                mass.IncreaseDamping(0.6f);
            }
        }
    }

    public void ApplyExplosiveForce(float force, Vector3 position, float radius)
    {
        ApplyExplosiveForce(force, position, radius, increaseDamping: true);
    }

    public void ApplyExplosiveForce(float force, Vector3 position, float radius, bool increaseDamping)
    {
        foreach (Point mass in Points)
        {
            float dist2 = position.DistanceSquaredTo(mass.Position);
            if (dist2 < radius * radius)
            {
                mass.ApplyForce(100f * force * (mass.Position - position) / (10000f + dist2));
                if (increaseDamping)
                {
                    mass.IncreaseDamping(0.6f);
                }
            }
        }
    }

    public Vector2 ToVec2(Vector3 position)
    {
        float factor = (position.Z + PerspectiveDepth) / PerspectiveDepth;
        return (new Vector2(position.X, position.Y) - ScreenCenter) * factor + ScreenCenter;
    }
}
