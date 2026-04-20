using System;
using System.Collections.Generic;
using System.Threading.Tasks;
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
    bool[] _edgeMask = Array.Empty<bool>();
    bool[] _sleeping = Array.Empty<bool>();
    SimVector2[] _springForces = Array.Empty<SimVector2>();
    SimVector2[] _effectorForces = Array.Empty<SimVector2>();
    byte[] _positionsScratch = Array.Empty<byte>();

    readonly struct EffectorRuntime
    {
        public readonly SimVector2 Start;
        public readonly SimVector2 End;
        public readonly SimVector2 DirectedUnit;
        public readonly float RadiusSq;
        public readonly float SigmaDenom;
        public readonly float Strength;
        public readonly float AnimatedRadius;
        public readonly float RingWidth;
        public readonly uint ShapeType;
        public readonly WarpBehaviorType BehaviorType;

        public EffectorRuntime(WarpEffectorData data)
        {
            Start = ToSimVector(data.StartPoint);
            End = ToSimVector(data.EndPoint);
            var directed = End - Start;
            DirectedUnit = directed.LengthSquared() > SpringEpsilon
                ? SimVector2.Normalize(directed)
                : SimVector2.Zero;
            RadiusSq = data.Radius * data.Radius;
            float sigma = MathF.Max(data.Radius * 0.5f, 1e-4f);
            SigmaDenom = 2.0f * sigma * sigma;
            Strength = data.Strength;
            AnimatedRadius = data.AnimatedRadius;
            RingWidth = MathF.Max(data.RingWidth, 1.0f);
            ShapeType = data.ShapeType;
            BehaviorType = (WarpBehaviorType)data.BehaviorType;
        }
    }

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
        _springForces = new SimVector2[pointCount];
        _effectorForces = new SimVector2[pointCount];
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

        Parallel.For(0, _points.Length, i =>
        {
            ref var point = ref _points[i];
            point.Acceleration = SimVector2.Zero;
            var displacement = point.Position - point.Anchor;
            _sleeping[i] = displacement.LengthSquared() <= sleepSq && _points[i].Velocity.LengthSquared() <= sleepSq;
            _springForces[i] = SimVector2.Zero;
            _effectorForces[i] = SimVector2.Zero;
        });

        EffectorRuntime[] effectors = GatherActiveEffectors();
        ApplyEffectors(effectors);
        ApplySpringForces();

        Parallel.For(0, _points.Length, i =>
        {
            ref var point = ref _points[i];
            point.Acceleration = _springForces[i] + _effectorForces[i];

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
        });
    }

    EffectorRuntime[] GatherActiveEffectors()
    {
        var gridOrigin = ToSimVector(GlobalPosition);
        var gridMin = gridOrigin;
        var gridMax = gridOrigin + ToSimVector(GridSizePixels);
        var active = new List<EffectorRuntime>();

        foreach (Node node in GetTree().GetNodesInGroup(WarpEffector.Group))
        {
            if (node is not WarpEffector eff || !eff.Visible)
                continue;

            WarpEffectorData data = eff.ToData(GlobalPosition, GridSizePixels);
            var globalPos = ToSimVector(eff.GlobalPosition);
            float effectiveRadius = MathF.Max(data.Radius, data.AnimatedRadius + data.RingWidth);
            if (globalPos.X + effectiveRadius < gridMin.X || globalPos.X - effectiveRadius > gridMax.X)
                continue;
            if (globalPos.Y + effectiveRadius < gridMin.Y || globalPos.Y - effectiveRadius > gridMax.Y)
                continue;

            active.Add(new EffectorRuntime(data));
        }

        return active.ToArray();
    }

    void ApplyEffectors(EffectorRuntime[] effectors)
    {
        if (effectors.Length == 0)
            return;

        Parallel.For(0, _points.Length, i =>
        {
            ref readonly var point = ref _points[i];
            SimVector2 totalForce = SimVector2.Zero;

            foreach (var effector in effectors)
            {
                SimVector2 center = effector.ShapeType == (uint)WarpShapeType.Line
                    ? ClosestPointOnSegment(effector.Start, effector.End, point.Position)
                    : effector.Start;

                SimVector2 delta = point.Position - center;
                float distanceSq = delta.LengthSquared();
                if (distanceSq > effector.RadiusSq)
                    continue;

                float distance = distanceSq > SpringEpsilon ? MathF.Sqrt(distanceSq) : 0.0f;
                SimVector2 outward = distanceSq > SpringEpsilon ? delta / distance : SimVector2.Zero;
                float gaussianFalloff = MathF.Exp(-distanceSq / effector.SigmaDenom);

                SimVector2 direction = effector.ShapeType == (uint)WarpShapeType.Radial &&
                                       effector.DirectedUnit.LengthSquared() > SpringEpsilon
                    ? effector.DirectedUnit
                    : outward;

                float magnitude = effector.Strength * gaussianFalloff;

                switch (effector.BehaviorType)
                {
                    case WarpBehaviorType.Impulse:
                        magnitude *= 2.0f;
                        break;
                    case WarpBehaviorType.Vortex:
                        direction = NormalizeOrZero(new SimVector2(-delta.Y, delta.X));
                        magnitude *= 1.35f;
                        break;
                    case WarpBehaviorType.GravityWell:
                        direction = -outward;
                        magnitude *= 1.25f;
                        break;
                    case WarpBehaviorType.Shockwave:
                    {
                        float ringDistance = MathF.Abs(distance - effector.AnimatedRadius);
                        if (ringDistance > effector.RingWidth)
                            continue;

                        float ringFalloff = 1.0f - (ringDistance / effector.RingWidth);
                        magnitude = effector.Strength * ringFalloff * ringFalloff;
                        direction = outward;
                        break;
                    }
                }

                totalForce += direction * magnitude;
            }

            _effectorForces[i] = totalForce;
        });
    }

    void ApplySpringForces()
    {
        float sleepSq = SleepThreshold * SleepThreshold;
        float restX = GridSizePixels.X / Math.Max(1, _physicsGridW);
        float restY = GridSizePixels.Y / Math.Max(1, _physicsGridH);

        Parallel.For(0, _points.Length, i =>
        {
            ref readonly var point = ref _points[i];
            SimVector2 totalForce = SimVector2.Zero;

            if (!(_sleeping[i] && _effectorForces[i].LengthSquared() <= sleepSq))
            {
                SimVector2 anchorOffset = point.Anchor - point.Position;
                if (anchorOffset.LengthSquared() > sleepSq || point.Velocity.LengthSquared() > sleepSq)
                    totalForce += (anchorOffset * _anchorPull) - (point.Velocity * _springDamping);
            }

            int x = i % PhysNodesX;
            int y = i / PhysNodesX;

            if (x > 0)
                totalForce += ComputeNeighborForce(i, IndexOf(x - 1, y), restX);
            if (x + 1 < PhysNodesX)
                totalForce += ComputeNeighborForce(i, IndexOf(x + 1, y), restX);
            if (y > 0)
                totalForce += ComputeNeighborForce(i, IndexOf(x, y - 1), restY);
            if (y + 1 < PhysNodesY)
                totalForce += ComputeNeighborForce(i, IndexOf(x, y + 1), restY);

            _springForces[i] = totalForce;
        });
    }

    SimVector2 ComputeNeighborForce(int pointIndex, int neighborIndex, float restLength)
    {
        ref readonly var point = ref _points[pointIndex];
        ref readonly var neighbor = ref _points[neighborIndex];

        SimVector2 offset = neighbor.Position - point.Position;
        float distanceSq = offset.LengthSquared();
        float restLengthSq = restLength * restLength;
        if (distanceSq <= restLengthSq)
            return SimVector2.Zero;

        float distance = MathF.Sqrt(distanceSq);
        if (distance <= SpringEpsilon)
            return SimVector2.Zero;

        SimVector2 springOffset = offset * ((distance - restLength) / distance);
        SimVector2 velocityDelta = neighbor.Velocity - point.Velocity;
        return (springOffset * _springStiffness) + (velocityDelta * _springDamping);
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
        using var shaderFile = FileAccess.Open("res://shaders/WarpGridDisplay.gdshader", FileAccess.ModeFlags.Read);
        if (shaderFile == null)
            throw new InvalidOperationException("Unable to open WarpGridDisplay.gdshader for manifest verification.");

        string shaderSource = shaderFile.GetAsText();
        WarpGridGpuManifest.VerifyPositionsTextureManifest(shaderSource);
    }
}
