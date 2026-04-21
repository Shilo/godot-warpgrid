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
    public float HoldExplosiveForce { get; set; } = 120.0f;

    [Export]
    public float HoldDirectedForce { get; set; } = 45.0f;

    [Export]
    public float ClickExplosiveForce { get; set; } = 8000.0f;

    private readonly ArrayMesh _arrayMesh = new();
    private MeshInstance2D _meshInstance = null!;
    private Spring[] _springs = [];
    private Point[,] _points = new Point[0, 0];
    private Vector2 _screenSize = Vector2.Zero;
    private Vector2 _lastViewportSize = Vector2.Zero;
    private bool _queuedExplosiveClick;
    private Vector3 _queuedExplosivePosition = Vector3.Zero;

    public override void _Ready()
    {
        RenderingServer.SetDefaultClearColor(new Color(0.01f, 0.01f, 0.05f, 1.0f));

        _meshInstance = new MeshInstance2D
        {
            Name = "WarpGridMesh",
            Mesh = _arrayMesh
        };

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

        HandlePointerForces();

        foreach (Spring spring in _springs)
        {
            spring.Update();
        }

        foreach (Point point in _points)
        {
            point.Update();
        }
    }

    public override void _Process(double delta)
    {
        Vector2 viewportSize = GetViewportRect().Size;

        if (Mathf.Abs(viewportSize.X - _lastViewportSize.X) > 1.0f || Mathf.Abs(viewportSize.Y - _lastViewportSize.Y) > 1.0f)
        {
            InitializeGrid();
        }

        RebuildMesh();
    }

    public void ResetGrid()
    {
        foreach (Point point in _points)
        {
            point.Reset();
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

    public void ApplyContinuousForce(float force, Vector3 position, float radius)
    {
        foreach (Point mass in _points)
        {
            float dist2 = position.DistanceSquaredTo(mass.Position);

            if (dist2 < radius * radius)
            {
                mass.ApplyForce(100.0f * force * (mass.Position - position) / (10000.0f + dist2));
            }
        }
    }

    public Vector2 ToVec2(Vector3 value)
    {
        Vector2 screenCenter = _screenSize / 2.0f;
        float factor = (value.Z + PerspectiveDepth) / PerspectiveDepth;
        return (new Vector2(value.X, value.Y) - screenCenter) * factor + screenCenter;
    }

    private void InitializeGrid()
    {
        Rect2 viewportRect = GetViewportRect();
        _screenSize = viewportRect.Size;
        _lastViewportSize = _screenSize;

        int numColumns = (int)(viewportRect.Size.X / GridSpacing.X) + 1;
        int numRows = (int)(viewportRect.Size.Y / GridSpacing.Y) + 1;
        _points = new Point[numColumns, numRows];

        Point[,] fixedPoints = new Point[numColumns, numRows];
        List<Spring> springList = [];

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
                Vector3 position = new(x, y, 0.0f);
                _points[column, row] = new Point(position, 1.0f, PointDamping);
                fixedPoints[column, row] = new Point(position, 0.0f, PointDamping);
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

        if (_springs.Length == 0)
        {
            return;
        }

        List<Vector2> vertices = new(_springs.Length * 2);
        List<Color> colors = new(_springs.Length * 2);

        foreach (Spring spring in _springs)
        {
            vertices.Add(ToVec2(spring.End1.Position));
            vertices.Add(ToVec2(spring.End2.Position));
            colors.Add(GridColor);
            colors.Add(GridColor);
        }

        GodotArray arrays = [];
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices.ToArray();
        arrays[(int)Mesh.ArrayType.Color] = colors.ToArray();

        _arrayMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Lines, arrays);
    }

    private void HandlePointerForces()
    {
        Vector2 mousePosition2D = GetGlobalMousePosition();
        Vector3 mousePosition = new(mousePosition2D.X, mousePosition2D.Y, 0.0f);

        if (_queuedExplosiveClick)
        {
            ApplyExplosiveForce(ClickExplosiveForce, _queuedExplosivePosition, 80.0f);
            _queuedExplosiveClick = false;
        }

        if (Input.IsMouseButtonPressed(MouseButton.Left))
        {
            ApplyContinuousForce(HoldExplosiveForce, mousePosition, 80.0f);
        }

        if (Input.IsMouseButtonPressed(MouseButton.Right))
        {
            ApplyDirectedForce(new Vector3(0.0f, 0.0f, HoldDirectedForce), mousePosition, 60.0f);
        }
    }

    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Middle } mouseButton)
        {
            _queuedExplosiveClick = true;
            _queuedExplosivePosition = new Vector3(mouseButton.Position.X, mouseButton.Position.Y, 0.0f);
        }
    }
}
