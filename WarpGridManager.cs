using System;
using System.Collections.Generic;
using Godot;

namespace GodotWarpGridTest;

public partial class WarpGridManager : Node2D
{
    [Export]
    public int ApproximatePointCount { get; set; } = 1600;

    [Export]
    public Color GridColor { get; set; } = new(30f / 255f, 30f / 255f, 139f / 255f, 85f / 255f);

    [Export]
    public bool EnableMeshRebuild { get; set; } = true;

    [Export]
    public float PerspectiveDepth
    {
        get => Grid.PerspectiveDepth;
        set => Grid.PerspectiveDepth = value;
    }

    public WarpGrid Grid { get; } = new();
    public Point[,] Points => Grid.Points;
    public Spring[] Springs => Grid.Springs;

    private ArrayMesh? _mesh;

    public override void _Ready()
    {
        if (Grid.Points.Length == 0)
        {
            Vector2 viewportSize = GetViewportRect().Size;
            float spacingValue = MathF.Sqrt((viewportSize.X * viewportSize.Y) / Math.Max(1f, ApproximatePointCount));
            Vector2 spacing = new(MathF.Max(1f, spacingValue), MathF.Max(1f, spacingValue));
            Initialize(new Rect2(Vector2.Zero, viewportSize), spacing, viewportSize / 2f);
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        if (Grid.Points.Length == 0)
        {
            return;
        }

        Vector2 mousePosition2D = GetGlobalMousePosition();
        Vector3 mousePosition = new(mousePosition2D.X, mousePosition2D.Y, 0f);
        bool leftPressed = Input.IsMouseButtonPressed(MouseButton.Left);
        bool rightPressed = Input.IsMouseButtonPressed(MouseButton.Right);

        Grid.ProcessInteraction(mousePosition, leftPressed, rightPressed);
        Grid.UpdateSimulation();
        RebuildMesh();
    }

    public override void _Draw()
    {
        if (_mesh is not null && _mesh.GetSurfaceCount() > 0)
        {
            DrawMesh(_mesh, null);
        }
    }

    public void Initialize(Rect2 size, Vector2 spacing, Vector2? screenCenter = null)
    {
        Grid.Initialize(size, spacing, screenCenter);
        RebuildMesh();
    }

    public void ProcessInteraction(Vector3 mousePosition, bool leftPressed, bool rightPressed)
    {
        Grid.ProcessInteraction(mousePosition, leftPressed, rightPressed);
    }

    public void UpdateSimulation()
    {
        Grid.UpdateSimulation();
        RebuildMesh();
    }

    public void ApplyDirectedForce(Vector3 force, Vector3 position, float radius)
    {
        Grid.ApplyDirectedForce(force, position, radius);
    }

    public void ApplyImplosiveForce(float force, Vector3 position, float radius)
    {
        Grid.ApplyImplosiveForce(force, position, radius);
    }

    public void ApplyExplosiveForce(float force, Vector3 position, float radius)
    {
        ApplyExplosiveForce(force, position, radius, increaseDamping: true);
    }

    public void ApplyExplosiveForce(float force, Vector3 position, float radius, bool increaseDamping)
    {
        Grid.ApplyExplosiveForce(force, position, radius, increaseDamping);
    }

    public Vector2 ToVec2(Vector3 position)
    {
        return Grid.ToVec2(position);
    }

    private void RebuildMesh()
    {
        if (!EnableMeshRebuild)
        {
            return;
        }

        _mesh ??= new ArrayMesh();
        _mesh.ClearSurfaces();

        if (Grid.Points.Length == 0)
        {
            QueueRedraw();
            return;
        }

        int width = Grid.Points.GetLength(0);
        int height = Grid.Points.GetLength(1);
        var vertices = new List<Vector2>();
        var colors = new List<Color>();
        var indices = new List<int>();

        for (int y = 1; y < height; y++)
        {
            for (int x = 1; x < width; x++)
            {
                Vector2 point = Grid.ToVec2(Grid.Points[x, y].Position);
                if (x > 1)
                {
                    Vector2 left = Grid.ToVec2(Grid.Points[x - 1, y].Position);
                    float thickness = y % 3 == 1 ? 3f : 1f;
                    AddLineQuad(left, point, thickness, vertices, colors, indices);
                }

                if (y > 1)
                {
                    Vector2 up = Grid.ToVec2(Grid.Points[x, y - 1].Position);
                    float thickness = x % 3 == 1 ? 3f : 1f;
                    AddLineQuad(up, point, thickness, vertices, colors, indices);
                }
            }
        }

        if (vertices.Count > 0)
        {
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = vertices.ToArray();
            arrays[(int)Mesh.ArrayType.Color] = colors.ToArray();
            arrays[(int)Mesh.ArrayType.Index] = indices.ToArray();
            _mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        }

        QueueRedraw();
    }

    private void AddLineQuad(
        Vector2 start,
        Vector2 end,
        float thickness,
        List<Vector2> vertices,
        List<Color> colors,
        List<int> indices)
    {
        Vector2 delta = end - start;
        if (delta == Vector2.Zero)
        {
            return;
        }

        Vector2 offset = delta.Normalized().Orthogonal() * (thickness * 0.5f);
        int startIndex = vertices.Count;

        vertices.Add(start - offset);
        vertices.Add(start + offset);
        vertices.Add(end + offset);
        vertices.Add(end - offset);

        colors.Add(GridColor);
        colors.Add(GridColor);
        colors.Add(GridColor);
        colors.Add(GridColor);

        indices.Add(startIndex);
        indices.Add(startIndex + 1);
        indices.Add(startIndex + 2);
        indices.Add(startIndex);
        indices.Add(startIndex + 2);
        indices.Add(startIndex + 3);
    }
}
