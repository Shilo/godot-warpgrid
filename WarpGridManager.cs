using System.Collections.Generic;
using Godot;
using GodotArray = Godot.Collections.Array;

public partial class WarpGridManager : Node2D
{
    [ExportGroup("Grid")]
    [Export]
    public Vector2 GridSpacing { get; set; } = new(28.0f, 28.0f);

    [Export]
    public float PerspectiveDepth { get; set; } = 2000.0f;

    [Export]
    public Color GridColor { get; set; } = new(30.0f / 255.0f, 30.0f / 255.0f, 139.0f / 255.0f, 85.0f / 255.0f);

    [ExportGroup("Spring Tuning")]
    [Export]
    public float SpringStiffness { get; set; } = 0.28f;

    [Export]
    public float SpringDamping { get; set; } = 0.06f;

    [Export]
    public float PointDamping { get; set; } = 0.98f;

    [Export]
    public float RestLengthScale { get; set; } = 0.95f;

    [Export]
    public float BorderAnchorStiffness { get; set; } = 0.10f;

    [Export]
    public float BorderAnchorDamping { get; set; } = 0.10f;

    [Export]
    public float InteriorAnchorStiffness { get; set; } = 0.002f;

    [Export]
    public float InteriorAnchorDamping { get; set; } = 0.02f;

    [ExportGroup("Mouse Force")]
    [Export]
    public float HoldExplosiveForce { get; set; } = 30.0f;

    [Export]
    public float HoldExplosiveRadius { get; set; } = 80.0f;

    [Export]
    public float HoldDirectedForce { get; set; } = 45.0f;

    [Export]
    public float HoldDirectedRadius { get; set; } = 60.0f;

    [Export]
    public float ClickExplosiveForce { get; set; } = 90.0f;

    [Export]
    public float ClickExplosiveRadius { get; set; } = 80.0f;

    [Export]
    public float ClickImplosiveForce { get; set; } = 20.0f;

    [Export]
    public float ClickImplosiveRadius { get; set; } = 140.0f;

    private readonly ArrayMesh _arrayMesh = new();
    private MeshInstance2D _meshInstance = null!;
    private ShaderMaterial _lineMaterial = null!;
    private Spring[] _springs = [];
    private Point[,] _points = new Point[0, 0];
    private Vector2 _screenSize = Vector2.Zero;
    private Vector2 _lastViewportSize = Vector2.Zero;
    private bool _wasLeftMousePressed;
    private bool _wasRightMousePressed;

    public override void _Ready()
    {
        RenderingServer.SetDefaultClearColor(new Color(0.01f, 0.01f, 0.05f, 1.0f));

        _meshInstance = new MeshInstance2D
        {
            Name = "WarpGridMesh",
            Mesh = _arrayMesh
        };

        _lineMaterial = new ShaderMaterial
        {
            Shader = GD.Load<Shader>("res://warp_grid_neon.gdshader")
        };

        _meshInstance.Material = _lineMaterial;
        AddChild(_meshInstance);

        InitializeGrid();
        RebuildMesh();
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_points.Length == 0)
        {
            return;
        }

        HandlePointerForces((float)(delta * 60.0));

        foreach (Spring spring in _springs)
        {
            spring.Update();
        }

        foreach (Point mass in _points)
        {
            mass.Update();
        }
    }

    public override void _Process(double delta)
    {
        Vector2 viewportSize = GetViewportRect().Size;

        if (viewportSize != _lastViewportSize)
        {
            InitializeGrid();
        }

        RebuildMesh();
    }

    public void ResetGrid()
    {
        foreach (Point mass in _points)
        {
            mass.Reset();
        }
    }

    public void ApplyDirectedForce(Vector3 force, Vector3 position, float radius)
    {
        foreach (Point mass in _points)
        {
            if (position.DistanceSquaredTo(mass.Position) < radius * radius)
            {
                mass.ApplyForce(10.0f * force / (10.0f + position.DistanceTo(mass.Position)));
            }
        }
    }

    public void ApplyImplosiveForce(float force, Vector3 position, float radius)
    {
        foreach (Point mass in _points)
        {
            float dist2 = position.DistanceSquaredTo(mass.Position);

            if (dist2 < radius * radius)
            {
                mass.ApplyForce(10.0f * force * (position - mass.Position) / (100.0f + dist2));
                mass.IncreaseDamping(0.6f);
            }
        }
    }

    public void ApplyExplosiveForce(float force, Vector3 position, float radius)
    {
        foreach (Point mass in _points)
        {
            float dist2 = position.DistanceSquaredTo(mass.Position);

            if (dist2 < radius * radius)
            {
                mass.ApplyForce(100.0f * force * (mass.Position - position) / (10000.0f + dist2));
                mass.IncreaseDamping(0.6f);
            }
        }
    }

    public Vector2 ToVec2(Vector3 value)
    {
        float factor = (value.Z + PerspectiveDepth) / PerspectiveDepth;
        return (new Vector2(value.X, value.Y) - (_screenSize / 2.0f)) * factor + (_screenSize / 2.0f);
    }

    private void InitializeGrid()
    {
        Rect2 viewportRect = GetViewportRect();
        _screenSize = viewportRect.Size;
        _lastViewportSize = _screenSize;

        List<Spring> springList = [];
        int numColumns = (int)(viewportRect.Size.X / GridSpacing.X) + 1;
        int numRows = (int)(viewportRect.Size.Y / GridSpacing.Y) + 1;

        _points = new Point[numColumns, numRows];
        Point[,] fixedPoints = new Point[numColumns, numRows];

        int column = 0;
        int row = 0;
        float left = viewportRect.Position.X;
        float top = viewportRect.Position.Y;
        float right = left + viewportRect.Size.X;
        float bottom = top + viewportRect.Size.Y;

        for (float y = top; y <= bottom; y += GridSpacing.Y)
        {
            for (float x = left; x <= right; x += GridSpacing.X)
            {
                _points[column, row] = new Point(new Vector3(x, y, 0.0f), 1.0f, PointDamping);
                fixedPoints[column, row] = new Point(new Vector3(x, y, 0.0f), 0.0f, PointDamping);
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
                    springList.Add(new Spring(fixedPoints[x, y], _points[x, y], BorderAnchorStiffness, BorderAnchorDamping, RestLengthScale));
                }
                else if (x % 3 == 0 && y % 3 == 0)
                {
                    springList.Add(new Spring(fixedPoints[x, y], _points[x, y], InteriorAnchorStiffness, InteriorAnchorDamping, RestLengthScale));
                }

                if (x > 0)
                {
                    springList.Add(new Spring(_points[x - 1, y], _points[x, y], SpringStiffness, SpringDamping, RestLengthScale));
                }

                if (y > 0)
                {
                    springList.Add(new Spring(_points[x, y - 1], _points[x, y], SpringStiffness, SpringDamping, RestLengthScale));
                }
            }
        }

        _springs = springList.ToArray();
    }

    private void RebuildMesh()
    {
        _arrayMesh.ClearSurfaces();

        if (_points.Length == 0)
        {
            return;
        }

        List<Vector2> vertices = [];
        List<Vector2> uvs = [];
        List<Color> colors = [];
        List<int> indices = [];

        int width = _points.GetLength(0);
        int height = _points.GetLength(1);

        for (int y = 1; y < height; y++)
        {
            for (int x = 1; x < width; x++)
            {
                Vector2 left = Vector2.Zero;
                Vector2 up = Vector2.Zero;
                Vector2 point = ToVec2(_points[x, y].Position);

                if (x > 1)
                {
                    left = ToVec2(_points[x - 1, y].Position);
                    float thickness = y % 3 == 1 ? 3.0f : 1.0f;
                    int clampedX = Mathf.Min(x + 1, width - 1);
                    Vector2 mid = CatmullRom(
                        ToVec2(_points[x - 2, y].Position),
                        left,
                        point,
                        ToVec2(_points[clampedX, y].Position),
                        0.5f);

                    if (mid.DistanceSquaredTo((left + point) * 0.5f) > 1.0f)
                    {
                        AddLineQuad(left, mid, thickness, GridColor, vertices, uvs, colors, indices);
                        AddLineQuad(mid, point, thickness, GridColor, vertices, uvs, colors, indices);
                    }
                    else
                    {
                        AddLineQuad(left, point, thickness, GridColor, vertices, uvs, colors, indices);
                    }
                }

                if (y > 1)
                {
                    up = ToVec2(_points[x, y - 1].Position);
                    float thickness = x % 3 == 1 ? 3.0f : 1.0f;
                    int clampedY = Mathf.Min(y + 1, height - 1);
                    Vector2 mid = CatmullRom(
                        ToVec2(_points[x, y - 2].Position),
                        up,
                        point,
                        ToVec2(_points[x, clampedY].Position),
                        0.5f);

                    if (mid.DistanceSquaredTo((up + point) * 0.5f) > 1.0f)
                    {
                        AddLineQuad(up, mid, thickness, GridColor, vertices, uvs, colors, indices);
                        AddLineQuad(mid, point, thickness, GridColor, vertices, uvs, colors, indices);
                    }
                    else
                    {
                        AddLineQuad(up, point, thickness, GridColor, vertices, uvs, colors, indices);
                    }
                }

                if (x > 1 && y > 1)
                {
                    Vector2 upLeft = ToVec2(_points[x - 1, y - 1].Position);
                    AddLineQuad((upLeft + up) * 0.5f, (left + point) * 0.5f, 1.0f, GridColor, vertices, uvs, colors, indices);
                    AddLineQuad((upLeft + left) * 0.5f, (up + point) * 0.5f, 1.0f, GridColor, vertices, uvs, colors, indices);
                }
            }
        }

        if (vertices.Count == 0)
        {
            return;
        }

        GodotArray arrays = [];
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices.ToArray();
        arrays[(int)Mesh.ArrayType.TexUV] = uvs.ToArray();
        arrays[(int)Mesh.ArrayType.Color] = colors.ToArray();
        arrays[(int)Mesh.ArrayType.Index] = indices.ToArray();

        _arrayMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        _arrayMesh.SurfaceSetMaterial(0, _lineMaterial);
    }

    private void HandlePointerForces(float stepScale)
    {
        bool leftPressed = Input.IsMouseButtonPressed(MouseButton.Left);
        bool rightPressed = Input.IsMouseButtonPressed(MouseButton.Right);
        bool ctrlPressed = Input.IsKeyPressed(Key.Ctrl);
        Vector2 mousePosition2D = GetGlobalMousePosition();
        Vector3 mousePosition = new(mousePosition2D.X, mousePosition2D.Y, 0.0f);

        bool leftJustPressed = leftPressed && !_wasLeftMousePressed;
        bool rightJustPressed = rightPressed && !_wasRightMousePressed;

        if (ctrlPressed)
        {
            if (leftJustPressed)
            {
                ApplyExplosiveForce(ClickExplosiveForce, mousePosition, ClickExplosiveRadius);
            }

            if (rightJustPressed)
            {
                ApplyImplosiveForce(ClickImplosiveForce, mousePosition, ClickImplosiveRadius);
            }
        }
        else
        {
            if (leftPressed)
            {
                ApplyExplosiveForce(HoldExplosiveForce * stepScale, mousePosition, HoldExplosiveRadius);
            }

            if (rightPressed)
            {
                ApplyDirectedForce(new Vector3(0.0f, 0.0f, HoldDirectedForce * stepScale), mousePosition, HoldDirectedRadius);
            }
        }

        _wasLeftMousePressed = leftPressed;
        _wasRightMousePressed = rightPressed;
    }

    private static void AddLineQuad(
        Vector2 start,
        Vector2 end,
        float thickness,
        Color color,
        List<Vector2> vertices,
        List<Vector2> uvs,
        List<Color> colors,
        List<int> indices)
    {
        Vector2 delta = end - start;

        if (delta.LengthSquared() <= Mathf.Epsilon)
        {
            return;
        }

        Vector2 normal = delta.Normalized().Orthogonal() * (thickness * 0.5f);
        int index = vertices.Count;

        vertices.Add(start - normal);
        vertices.Add(start + normal);
        vertices.Add(end + normal);
        vertices.Add(end - normal);

        uvs.Add(new Vector2(0.0f, 0.0f));
        uvs.Add(new Vector2(0.0f, 1.0f));
        uvs.Add(new Vector2(1.0f, 1.0f));
        uvs.Add(new Vector2(1.0f, 0.0f));

        colors.Add(color);
        colors.Add(color);
        colors.Add(color);
        colors.Add(color);

        indices.Add(index);
        indices.Add(index + 1);
        indices.Add(index + 2);
        indices.Add(index);
        indices.Add(index + 2);
        indices.Add(index + 3);
    }

    private static Vector2 CatmullRom(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t)
    {
        float t2 = t * t;
        float t3 = t2 * t;

        return 0.5f * (
            (2.0f * p1) +
            (-p0 + p2) * t +
            ((2.0f * p0) - (5.0f * p1) + (4.0f * p2) - p3) * t2 +
            (-p0 + (3.0f * p1) - (3.0f * p2) + p3) * t3);
    }
}
