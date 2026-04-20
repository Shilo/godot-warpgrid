using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using SimVector2 = System.Numerics.Vector2;

namespace WarpGrid;

public enum WarpRenderMode { Grid = 0, Texture = 1 }
public enum VibePreset { Custom = 0, GeometryWars = 1 }

[GlobalClass, Tool]
public partial class WarpGridManager : Node2D
{
    const float SleepThreshold = 1e-4f;
    const float SpringEpsilon = 1e-6f;

    int _gridW = 180;
    int _gridH = 100;
    int _physicsGridW = 36;
    int _physicsGridH = 20;
    Vector2 _gridSizePixels = new(1152, 648);

    float _springStiffness = 0.28f;
    float _springDamping = 0.06f;
    float _globalDamping = 0.98f;
    float _anchorPull = 0.02f;
    float _mass = 1.0f;
    bool _clampEdges = true;

    WarpRenderMode _renderMode = WarpRenderMode.Grid;
    VibePreset _vibePreset = VibePreset.GeometryWars;

    [Export] public int GridW
    {
        get => _gridW;
        set
        {
            int clamped = Math.Max(1, value);
            if (_gridW == clamped) return;
            _gridW = clamped;
            MaybeRebuild();
        }
    }

    [Export] public int GridH
    {
        get => _gridH;
        set
        {
            int clamped = Math.Max(1, value);
            if (_gridH == clamped) return;
            _gridH = clamped;
            MaybeRebuild();
        }
    }

    [Export] public int PhysicsGridW
    {
        get => _physicsGridW;
        set
        {
            int clamped = Math.Max(1, value);
            if (_physicsGridW == clamped) return;
            _physicsGridW = clamped;
            MaybeRebuild();
        }
    }

    [Export] public int PhysicsGridH
    {
        get => _physicsGridH;
        set
        {
            int clamped = Math.Max(1, value);
            if (_physicsGridH == clamped) return;
            _physicsGridH = clamped;
            MaybeRebuild();
        }
    }

    [Export] public Vector2 GridSizePixels
    {
        get => _gridSizePixels;
        set
        {
            if (_gridSizePixels == value) return;
            _gridSizePixels = value;
            MaybeRebuild();
        }
    }

    [Export] public Color LineColor = new(0.15f, 0.55f, 1.0f);
    [Export] public Texture2D MainTexture;

    [Export] public WarpRenderMode RenderMode
    {
        get => _renderMode;
        set
        {
            _renderMode = value;
            if (_material != null)
                _material.SetShaderParameter("display_mode", (int)value);
        }
    }

    [Export] public VibePreset Preset
    {
        get => _vibePreset;
        set
        {
            _vibePreset = value;
            ApplyPreset(value);
        }
    }

    [Export] public float SpringStiffness
    {
        get => _springStiffness;
        set => _springStiffness = Math.Max(0.0f, value);
    }

    [Export] public float SpringDamping
    {
        get => _springDamping;
        set => _springDamping = Math.Max(0.0f, value);
    }

    [Export] public float GlobalDamping
    {
        get => _globalDamping;
        set => _globalDamping = Math.Clamp(value, 0.0f, 1.0f);
    }

    [Export] public float AnchorPull
    {
        get => _anchorPull;
        set => _anchorPull = Math.Max(0.0f, value);
    }

    [Export] public float Mass
    {
        get => _mass;
        set => _mass = Math.Max(0.0001f, value);
    }

    [Export] public bool ClampEdges
    {
        get => _clampEdges;
        set => _clampEdges = value;
    }

    [Export] public bool ForceRebuild
    {
        get => false;
        set
        {
            if (!value) return;
            Rebuild();
        }
    }

    int PhysNodesX => _physicsGridW + 1;
    int PhysNodesY => _physicsGridH + 1;
    int VisualNodesX => _gridW + 1;
    int VisualNodesY => _gridH + 1;

    ArrayMesh _mesh;
    MeshInstance2D _meshInstance;
    ShaderMaterial _material;
    Image _positionsImage;
    ImageTexture _positionsTexture;

    WarpGridPoint[] _points = Array.Empty<WarpGridPoint>();
    WarpGridSpring[] _springs = Array.Empty<WarpGridSpring>();
    bool[] _edgeMask = Array.Empty<bool>();
    bool[] _sleeping = Array.Empty<bool>();
    byte[] _positionsScratch = Array.Empty<byte>();

    public override void _Ready()
    {
        ApplyPreset(_vibePreset);
        Rebuild();
    }

    void ApplyPreset(VibePreset preset)
    {
        if (preset != VibePreset.GeometryWars)
            return;

        _springStiffness = 0.28f;
        _springDamping = 0.06f;
        _globalDamping = 0.98f;
        _anchorPull = 0.02f;
        _mass = 1.0f;
    }

    public void Rebuild()
    {
        VerifyTextureManifest();
        BuildMesh();
        BuildPhysicsGrid();
        BuildMaterial();
        UploadPositionsTexture();
    }

    void BuildMesh()
    {
        if (_meshInstance != null)
        {
            _meshInstance.Free();
            _meshInstance = null;
        }

        _mesh = new ArrayMesh();
        var verts = MeshHelper.BuildGridVertices(VisualNodesX, VisualNodesY, GridSizePixels);
        var uvs = MeshHelper.BuildGridUVs(VisualNodesX, VisualNodesY);
        var indices = MeshHelper.BuildQuadGridIndices(VisualNodesX, VisualNodesY);

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts;
        arrays[(int)Mesh.ArrayType.TexUV] = uvs;
        arrays[(int)Mesh.ArrayType.Index] = indices;
        _mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);

        _meshInstance = new MeshInstance2D { Mesh = _mesh, Texture = MainTexture };
        AddChild(_meshInstance, false, InternalMode.Front);
    }

    void BuildPhysicsGrid()
    {
        int pointCount = PhysNodesX * PhysNodesY;
        _points = new WarpGridPoint[pointCount];
        _edgeMask = new bool[pointCount];
        _sleeping = new bool[pointCount];
        _positionsScratch = new byte[pointCount * WarpGridGpuManifest.PositionTexelStride];

        float spacingX = GridSizePixels.X / Math.Max(1, _physicsGridW);
        float spacingY = GridSizePixels.Y / Math.Max(1, _physicsGridH);

        for (int y = 0; y < PhysNodesY; y++)
        {
            for (int x = 0; x < PhysNodesX; x++)
            {
                int index = IndexOf(x, y);
                var anchor = new SimVector2(x * spacingX, y * spacingY);
                _points[index] = new WarpGridPoint(anchor);
                _edgeMask[index] = x == 0 || y == 0 || x == PhysNodesX - 1 || y == PhysNodesY - 1;
            }
        }

        var springs = new List<WarpGridSpring>(pointCount * 3);
        for (int y = 0; y < PhysNodesY; y++)
        {
            for (int x = 0; x < PhysNodesX; x++)
            {
                int index = IndexOf(x, y);
                if (x + 1 < PhysNodesX)
                    springs.Add(new WarpGridSpring(index, IndexOf(x + 1, y), GridSpringType.Right, spacingX));
                if (y + 1 < PhysNodesY)
                    springs.Add(new WarpGridSpring(index, IndexOf(x, y + 1), GridSpringType.Up, spacingY));
                springs.Add(new WarpGridSpring(index, index, GridSpringType.OriginalPosition, 0.0f));
            }
        }

        _springs = springs.ToArray();
    }

    void BuildMaterial()
    {
        var shader = GD.Load<Shader>("res://shaders/WarpGridDisplay.gdshader");
        _material = new ShaderMaterial { Shader = shader };

        _positionsImage = Image.CreateEmpty(
            PhysNodesX,
            PhysNodesY,
            false,
            WarpGridGpuManifest.PositionImageFormat);
        _positionsTexture = ImageTexture.CreateFromImage(_positionsImage);

        _material.SetShaderParameter("positions_tex", _positionsTexture);
        _material.SetShaderParameter("grid_size_pixels", GridSizePixels);
        _material.SetShaderParameter("grid_dims", new Vector2I(_physicsGridW, _physicsGridH));
        _material.SetShaderParameter("phys_tex_size", new Vector2(PhysNodesX, PhysNodesY));
        _material.SetShaderParameter("line_color", LineColor);
        _material.SetShaderParameter("display_mode", (int)_renderMode);
        if (MainTexture != null)
            _material.SetShaderParameter("main_tex", MainTexture);

        _meshInstance.Material = _material;
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_points.Length == 0 || _positionsTexture == null)
            return;

        SimulatePhysics();
        UploadPositionsTexture();
    }

    void SimulatePhysics()
    {
        float invMass = 1.0f / _mass;
        float sleepSq = SleepThreshold * SleepThreshold;

        for (int i = 0; i < _points.Length; i++)
        {
            _points[i].Acceleration = SimVector2.Zero;
            var displacement = _points[i].Position - _points[i].Anchor;
            _sleeping[i] = displacement.LengthSquared() <= sleepSq && _points[i].Velocity.LengthSquared() <= sleepSq;
        }

        ApplyEffectors();
        ApplySpringForces();

        for (int i = 0; i < _points.Length; i++)
        {
            ref var point = ref _points[i];

            if (_clampEdges && _edgeMask[i])
            {
                point.Position = point.Anchor;
                point.Velocity = SimVector2.Zero;
                point.Acceleration = SimVector2.Zero;
                _sleeping[i] = true;
                continue;
            }

            if (_sleeping[i] && point.Acceleration.LengthSquared() <= sleepSq)
            {
                point.Position = point.Anchor;
                point.Velocity = SimVector2.Zero;
                point.Acceleration = SimVector2.Zero;
                continue;
            }

            point.Velocity += point.Acceleration * invMass;
            point.Velocity *= _globalDamping;
            point.Position += point.Velocity;
            point.Acceleration = SimVector2.Zero;

            var displacement = point.Position - point.Anchor;
            if (displacement.LengthSquared() <= sleepSq && point.Velocity.LengthSquared() <= sleepSq)
            {
                point.Position = point.Anchor;
                point.Velocity = SimVector2.Zero;
            }
        }
    }

    void ApplyEffectors()
    {
        var gridOrigin = ToSimVector(GlobalPosition);
        var gridMin = gridOrigin;
        var gridMax = gridOrigin + ToSimVector(GridSizePixels);

        foreach (Node node in GetTree().GetNodesInGroup(WarpEffector.Group))
        {
            if (node is not WarpEffector eff || !eff.Visible)
                continue;

            var globalPos = ToSimVector(eff.GlobalPosition);
            float radius = eff.Radius;
            if (globalPos.X + radius < gridMin.X || globalPos.X - radius > gridMax.X)
                continue;
            if (globalPos.Y + radius < gridMin.Y || globalPos.Y - radius > gridMax.Y)
                continue;

            WarpEffectorData data = eff.ToData(GlobalPosition, GridSizePixels);
            var start = ToSimVector(data.StartPoint);
            var end = ToSimVector(data.EndPoint);
            float radiusSq = data.Radius * data.Radius;
            float sigma = MathF.Max(data.Radius * 0.5f, 1e-4f);
            float denom = 2.0f * sigma * sigma;
            float behaviorScale = data.BehaviorType == (uint)WarpBehaviorType.Impulse ? 2.0f : 1.0f;

            for (int i = 0; i < _points.Length; i++)
            {
                ref var point = ref _points[i];
                SimVector2 center = data.ShapeType == (uint)WarpShapeType.Line
                    ? ClosestPointOnSegment(start, end, point.Position)
                    : start;
                SimVector2 delta = point.Position - center;
                float distanceSq = delta.LengthSquared();
                if (distanceSq > radiusSq)
                    continue;

                SimVector2 direction;
                if (data.ShapeType == (uint)WarpShapeType.Radial)
                {
                    SimVector2 segmentDir = end - start;
                    direction = segmentDir.LengthSquared() > SpringEpsilon
                        ? SimVector2.Normalize(segmentDir)
                        : NormalizeOrZero(delta);
                }
                else
                {
                    direction = NormalizeOrZero(delta);
                }

                float falloff = MathF.Exp(-distanceSq / denom);
                point.Acceleration += direction * (data.Strength * falloff * behaviorScale);
            }
        }
    }

    void ApplySpringForces()
    {
        float sleepSq = SleepThreshold * SleepThreshold;

        foreach (var spring in _springs)
        {
            switch (spring.Type)
            {
                case GridSpringType.OriginalPosition:
                    ApplyAnchorSpring(spring.PointA, sleepSq);
                    break;
                default:
                    ApplyNeighborSpring(spring, sleepSq);
                    break;
            }
        }
    }

    void ApplyAnchorSpring(int pointIndex, float sleepSq)
    {
        ref var point = ref _points[pointIndex];
        if (_sleeping[pointIndex] && point.Acceleration.LengthSquared() <= sleepSq)
            return;

        SimVector2 offset = point.Anchor - point.Position;
        if (offset.LengthSquared() <= sleepSq && point.Velocity.LengthSquared() <= sleepSq)
            return;

        SimVector2 force = (offset * _anchorPull) - (point.Velocity * _springDamping);
        point.Acceleration += force;
    }

    void ApplyNeighborSpring(in WarpGridSpring spring, float sleepSq)
    {
        ref var pointA = ref _points[spring.PointA];
        ref var pointB = ref _points[spring.PointB];

        SimVector2 offset = pointA.Position - pointB.Position;
        float distanceSq = offset.LengthSquared();
        float restLengthSq = spring.RestLength * spring.RestLength;
        if (_sleeping[spring.PointA] && _sleeping[spring.PointB] && distanceSq <= restLengthSq + sleepSq)
            return;

        if (distanceSq <= restLengthSq)
            return;

        float distance = MathF.Sqrt(distanceSq);
        if (distance <= SpringEpsilon)
            return;

        SimVector2 springOffset = offset * ((distance - spring.RestLength) / distance);
        SimVector2 velocityDiff = pointB.Velocity - pointA.Velocity;
        SimVector2 force = (springOffset * _springStiffness) - (velocityDiff * _springDamping);

        pointA.Acceleration -= force;
        pointB.Acceleration += force;
    }

    void UploadPositionsTexture()
    {
        if (_positionsImage == null || _positionsTexture == null)
            return;

        for (int i = 0; i < _points.Length; i++)
        {
            int byteOffset = i * WarpGridGpuManifest.PositionTexelStride;
            ref readonly var point = ref _points[i];
            WarpGridGpuManifest.WriteTexel(
                _positionsScratch.AsSpan(byteOffset, WarpGridGpuManifest.PositionTexelStride),
                new WarpGridGpuManifest.PackedTexelData(
                    point.Position.X,
                    point.Position.Y,
                    point.Anchor.X,
                    point.Anchor.Y));
        }

        _positionsImage.SetData(
            PhysNodesX,
            PhysNodesY,
            false,
            WarpGridGpuManifest.PositionImageFormat,
            _positionsScratch);
        _positionsTexture.Update(_positionsImage);
    }

    int IndexOf(int x, int y) => y * PhysNodesX + x;

    void MaybeRebuild()
    {
        if (IsInsideTree())
            Rebuild();
    }

    static SimVector2 ClosestPointOnSegment(SimVector2 start, SimVector2 end, SimVector2 point)
    {
        SimVector2 segment = end - start;
        float denom = segment.LengthSquared();
        if (denom <= SpringEpsilon)
            return start;

        float t = Math.Clamp(SimVector2.Dot(point - start, segment) / denom, 0.0f, 1.0f);
        return start + (segment * t);
    }

    static SimVector2 NormalizeOrZero(SimVector2 value)
    {
        float lengthSq = value.LengthSquared();
        return lengthSq <= SpringEpsilon ? SimVector2.Zero : SimVector2.Normalize(value);
    }

    static SimVector2 ToSimVector(Vector2 value) => new(value.X, value.Y);

    void VerifyTextureManifest()
    {
        string shaderPath = ProjectSettings.GlobalizePath("res://shaders/WarpGridDisplay.gdshader");
        string shaderSource = File.ReadAllText(shaderPath);
        WarpGridGpuManifest.VerifyPositionsTextureManifest(shaderSource);
    }
}
