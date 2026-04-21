using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Godot;

namespace WarpGrid;

public enum WarpRenderMode { Grid = 0, Texture = 1 }
public enum VibePreset { Custom = 0, GeometryWars = 1, ElasticSilk = 2, ArcadeRigid = 3 }

[GlobalClass, Tool]
public partial class WarpGridManager : Node2D
{
    const float FixedDt = 1.0f / 120.0f;
    const int MaxPhysicsStepsPerFrame = 8;
    const int MaxSupportedSubSteps = 8;
    const int EffectorPartitionThreshold = 32;
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
    int _subSteps = 2;
    float _ambientTurbulence = 0.0f;

    float _accumulator;
    float _physicsSpacingX;
    float _physicsSpacingY;
    int _parallelBatchSize;
    bool _positionsDirty = true;
    float _lastLoadMetric;
    float _ambientPhase;

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

    [Export] public int SubSteps
    {
        get => _subSteps;
        set => _subSteps = Math.Clamp(value, 1, MaxSupportedSubSteps);
    }

    [Export] public float AmbientTurbulence
    {
        get => _ambientTurbulence;
        set => _ambientTurbulence = Math.Max(0.0f, value);
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
    int PointCount => PhysNodesX * PhysNodesY;

    ArrayMesh _mesh;
    MeshInstance2D _meshInstance;
    ShaderMaterial _material;
    Image _positionsImage;
    ImageTexture _positionsTexture;
    Image _velocityImage;
    ImageTexture _velocityTexture;

    float[] _posX = Array.Empty<float>();
    float[] _posY = Array.Empty<float>();
    float[] _velX = Array.Empty<float>();
    float[] _velY = Array.Empty<float>();
    float[] _accX = Array.Empty<float>();
    float[] _accY = Array.Empty<float>();
    float[] _anchorX = Array.Empty<float>();
    float[] _anchorY = Array.Empty<float>();
    bool[] _edgeMask = Array.Empty<bool>();
    bool[] _sleeping = Array.Empty<bool>();
    byte[] _positionsScratch = Array.Empty<byte>();
    byte[] _velocityScratch = Array.Empty<byte>();
    EffectorRuntime[] _effectorScratch = Array.Empty<EffectorRuntime>();
    OrderablePartitioner<Tuple<int, int>> _pointRanges;
    int[] _effectorStripStarts = Array.Empty<int>();
    int[] _effectorStripCursor = Array.Empty<int>();
    int[] _effectorStripItems = Array.Empty<int>();
    int _effectorStripCount;
    float _effectorStripWidth;
    bool _useEffectorPartition;

    readonly struct EffectorRuntime
    {
        public readonly float StartX;
        public readonly float StartY;
        public readonly float EndX;
        public readonly float EndY;
        public readonly float DirectedX;
        public readonly float DirectedY;
        public readonly float RadiusSq;
        public readonly float SigmaDenom;
        public readonly float Strength;
        public readonly float AnimatedRadius;
        public readonly float RingWidth;
        public readonly float EffectiveRadius;
        public readonly float EffectiveRadiusSq;
        public readonly uint ShapeType;
        public readonly WarpBehaviorType BehaviorType;

        public EffectorRuntime(WarpEffectorData data)
        {
            StartX = data.StartPoint.X;
            StartY = data.StartPoint.Y;
            EndX = data.EndPoint.X;
            EndY = data.EndPoint.Y;

            float directedX = EndX - StartX;
            float directedY = EndY - StartY;
            float directedLenSq = (directedX * directedX) + (directedY * directedY);
            if (directedLenSq > SpringEpsilon)
            {
                float invLen = 1.0f / MathF.Sqrt(directedLenSq);
                DirectedX = directedX * invLen;
                DirectedY = directedY * invLen;
            }
            else
            {
                DirectedX = 0.0f;
                DirectedY = 0.0f;
            }

            RadiusSq = data.Radius * data.Radius;
            float sigma = MathF.Max(data.Radius * 0.5f, 1e-4f);
            SigmaDenom = 2.0f * sigma * sigma;
            Strength = data.Strength;
            AnimatedRadius = data.AnimatedRadius;
            RingWidth = MathF.Max(data.RingWidth, 1.0f);
            EffectiveRadius = MathF.Max(data.Radius, AnimatedRadius + RingWidth);
            EffectiveRadiusSq = EffectiveRadius * EffectiveRadius;
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
        switch (preset)
        {
            case VibePreset.GeometryWars:
                _springStiffness = 0.28f;
                _springDamping = 0.06f;
                _globalDamping = 0.98f;
                _anchorPull = 0.02f;
                _mass = 1.0f;
                break;
            case VibePreset.ElasticSilk:
                _springStiffness = 0.12f;
                _springDamping = 0.10f;
                _globalDamping = 0.992f;
                _anchorPull = 0.008f;
                _mass = 1.15f;
                break;
            case VibePreset.ArcadeRigid:
                _springStiffness = 0.56f;
                _springDamping = 0.11f;
                _globalDamping = 0.93f;
                _anchorPull = 0.075f;
                _mass = 0.9f;
                break;
        }
    }

    public void Rebuild()
    {
        _accumulator = 0.0f;
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
        int pointCount = PointCount;
        _posX = new float[pointCount];
        _posY = new float[pointCount];
        _velX = new float[pointCount];
        _velY = new float[pointCount];
        _accX = new float[pointCount];
        _accY = new float[pointCount];
        _anchorX = new float[pointCount];
        _anchorY = new float[pointCount];
        _edgeMask = new bool[pointCount];
        _sleeping = new bool[pointCount];
        _positionsScratch = new byte[pointCount * WarpGridGpuManifest.PositionTexelStride];
        _velocityScratch = new byte[pointCount * WarpGridGpuManifest.VelocityTexelStride];
        _parallelBatchSize = ComputeBatchSize(pointCount);
        _pointRanges = Partitioner.Create(0, pointCount, _parallelBatchSize);

        _physicsSpacingX = GridSizePixels.X / Math.Max(1, _physicsGridW);
        _physicsSpacingY = GridSizePixels.Y / Math.Max(1, _physicsGridH);

        for (int y = 0; y < PhysNodesY; y++)
        {
            for (int x = 0; x < PhysNodesX; x++)
            {
                int index = IndexOf(x, y);
                float anchorX = x * _physicsSpacingX;
                float anchorY = y * _physicsSpacingY;
                _anchorX[index] = anchorX;
                _anchorY[index] = anchorY;
                _posX[index] = anchorX;
                _posY[index] = anchorY;
                _edgeMask[index] = x == 0 || y == 0 || x == PhysNodesX - 1 || y == PhysNodesY - 1;
            }
        }

        _positionsDirty = true;
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
        _velocityImage = Image.CreateEmpty(
            PhysNodesX,
            PhysNodesY,
            false,
            WarpGridGpuManifest.VelocityImageFormat);
        _velocityTexture = ImageTexture.CreateFromImage(_velocityImage);

        _material.SetShaderParameter("positions_tex", _positionsTexture);
        _material.SetShaderParameter("velocity_tex", _velocityTexture);
        _material.SetShaderParameter("grid_size_pixels", GridSizePixels);
        _material.SetShaderParameter("display_grid_dims", new Vector2I(_gridW, _gridH));
        _material.SetShaderParameter("phys_tex_size", new Vector2(PhysNodesX, PhysNodesY));
        _material.SetShaderParameter("physics_min_spacing", MathF.Max(MathF.Min(_physicsSpacingX, _physicsSpacingY), 1.0f));
        _material.SetShaderParameter("line_color", LineColor);
        _material.SetShaderParameter("display_mode", (int)_renderMode);
        if (MainTexture != null)
            _material.SetShaderParameter("main_tex", MainTexture);

        _meshInstance.Material = _material;
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_posX.Length == 0 || _positionsTexture == null)
            return;

        _accumulator = MathF.Min(_accumulator + (float)delta, FixedDt * MaxPhysicsStepsPerFrame);

        bool stepped = false;
        int steps = 0;
        while (_accumulator >= FixedDt && steps < MaxPhysicsStepsPerFrame)
        {
            int effectorCount = GatherActiveEffectors();
            BuildEffectorPartition(effectorCount);
            _lastLoadMetric = EstimateLoadMetric();
            int subStepCount = DetermineSubStepCount(effectorCount, _lastLoadMetric);
            float stepScale = 1.0f / subStepCount;

            for (int subStep = 0; subStep < subStepCount; subStep++)
            {
                _ambientPhase += FixedDt * stepScale;
                SimulatePhysics(stepScale, effectorCount);
            }

            _accumulator -= FixedDt;
            stepped = true;
            steps++;
        }

        if (stepped)
            _positionsDirty = true;

        if (_positionsDirty)
            UploadPositionsTexture();
    }

    void SimulatePhysics(float stepScale, int effectorCount)
    {
        // The hot loops below walk the SoA buffers linearly so the CPU gets tight,
        // cache-friendly access patterns and the JIT has the best chance to elide
        // bounds checks and auto-vectorize the branch-light arithmetic.
        float invMass = 1.0f / _mass;
        float sleepSq = SleepThreshold * SleepThreshold;
        float damping = MathF.Pow(_globalDamping, stepScale);

        ForEachPointRange((start, end) =>
        {
            for (int i = start; i < end; i++)
            {
                _accX[i] = 0.0f;
                _accY[i] = 0.0f;

                float displacementX = _posX[i] - _anchorX[i];
                float displacementY = _posY[i] - _anchorY[i];
                float velocityX = _velX[i];
                float velocityY = _velY[i];
                _sleeping[i] =
                    (displacementX * displacementX) + (displacementY * displacementY) <= sleepSq &&
                    (velocityX * velocityX) + (velocityY * velocityY) <= sleepSq;
            }
        });

        ApplyEffectors(effectorCount);
        ApplySpringForces();

        ForEachPointRange((start, end) =>
        {
            for (int i = start; i < end; i++)
            {
                if (_clampEdges && _edgeMask[i])
                {
                    _posX[i] = _anchorX[i];
                    _posY[i] = _anchorY[i];
                    _velX[i] = 0.0f;
                    _velY[i] = 0.0f;
                    _accX[i] = 0.0f;
                    _accY[i] = 0.0f;
                    _sleeping[i] = true;
                    continue;
                }

                float accX = _accX[i];
                float accY = _accY[i];
                if (_sleeping[i] && ((accX * accX) + (accY * accY) <= sleepSq))
                {
                    _posX[i] = _anchorX[i];
                    _posY[i] = _anchorY[i];
                    _velX[i] = 0.0f;
                    _velY[i] = 0.0f;
                    _accX[i] = 0.0f;
                    _accY[i] = 0.0f;
                    continue;
                }

                float velX = _velX[i] + (accX * invMass * stepScale);
                float velY = _velY[i] + (accY * invMass * stepScale);
                velX *= damping;
                velY *= damping;

                float posX = _posX[i] + (velX * stepScale);
                float posY = _posY[i] + (velY * stepScale);

                _velX[i] = velX;
                _velY[i] = velY;
                _posX[i] = posX;
                _posY[i] = posY;
                _accX[i] = 0.0f;
                _accY[i] = 0.0f;

                float displacementX = posX - _anchorX[i];
                float displacementY = posY - _anchorY[i];
                if ((displacementX * displacementX) + (displacementY * displacementY) <= sleepSq &&
                    (velX * velX) + (velY * velY) <= sleepSq)
                {
                    _posX[i] = _anchorX[i];
                    _posY[i] = _anchorY[i];
                    _velX[i] = 0.0f;
                    _velY[i] = 0.0f;
                }
            }
        });
    }

    int GatherActiveEffectors()
    {
        float gridOriginX = GlobalPosition.X;
        float gridOriginY = GlobalPosition.Y;
        float gridMaxX = gridOriginX + GridSizePixels.X;
        float gridMaxY = gridOriginY + GridSizePixels.Y;

        var nodes = GetTree().GetNodesInGroup(WarpEffector.Group);
        EnsureEffectorCapacity(nodes.Count);

        int activeCount = 0;
        foreach (Node node in nodes)
        {
            if (node is not WarpEffector eff || !eff.Visible)
                continue;

            WarpEffectorData data = eff.ToData(GlobalPosition);
            float globalX = eff.GlobalPosition.X;
            float globalY = eff.GlobalPosition.Y;
            float effectiveRadius = MathF.Max(data.Radius, data.AnimatedRadius + data.RingWidth);
            if (globalX + effectiveRadius < gridOriginX || globalX - effectiveRadius > gridMaxX)
                continue;
            if (globalY + effectiveRadius < gridOriginY || globalY - effectiveRadius > gridMaxY)
                continue;

            _effectorScratch[activeCount++] = new EffectorRuntime(data);
        }

        return activeCount;
    }

    void ApplyEffectors(int effectorCount)
    {
        if (effectorCount == 0)
            return;

        ForEachPointRange((start, end) =>
        {
            for (int i = start; i < end; i++)
            {
                float px = _posX[i];
                float py = _posY[i];
                float totalX = 0.0f;
                float totalY = 0.0f;
                int effectorStart = 0;
                int effectorEnd = effectorCount;
                if (_useEffectorPartition)
                {
                    int stripIndex = GetEffectorStripIndex(px);
                    effectorStart = _effectorStripStarts[stripIndex];
                    effectorEnd = _effectorStripStarts[stripIndex + 1];
                }

                for (int effectorRefIndex = effectorStart; effectorRefIndex < effectorEnd; effectorRefIndex++)
                {
                    int effectorIndex = _useEffectorPartition ? _effectorStripItems[effectorRefIndex] : effectorRefIndex;
                    ref readonly EffectorRuntime effector = ref _effectorScratch[effectorIndex];

                    float centerX = effector.StartX;
                    float centerY = effector.StartY;
                    if (effector.ShapeType == (uint)WarpShapeType.Line)
                    {
                        ClosestPointOnSegment(
                            effector.StartX,
                            effector.StartY,
                            effector.EndX,
                            effector.EndY,
                            px,
                            py,
                            out centerX,
                            out centerY);
                    }

                    float deltaX = px - centerX;
                    float deltaY = py - centerY;
                    float distanceSq = (deltaX * deltaX) + (deltaY * deltaY);
                    float maxDistanceSq = effector.BehaviorType == WarpBehaviorType.Shockwave
                        ? effector.EffectiveRadiusSq
                        : effector.RadiusSq;
                    if (distanceSq > maxDistanceSq)
                        continue;

                    float distance = distanceSq > SpringEpsilon ? MathF.Sqrt(distanceSq) : 0.0f;
                    float outwardX = 0.0f;
                    float outwardY = 0.0f;
                    if (distanceSq > SpringEpsilon)
                    {
                        float invDistance = 1.0f / distance;
                        outwardX = deltaX * invDistance;
                        outwardY = deltaY * invDistance;
                    }

                    float directionX = effector.ShapeType == (uint)WarpShapeType.Radial &&
                                       ((effector.DirectedX * effector.DirectedX) + (effector.DirectedY * effector.DirectedY) > SpringEpsilon)
                        ? effector.DirectedX
                        : outwardX;
                    float directionY = effector.ShapeType == (uint)WarpShapeType.Radial &&
                                       ((effector.DirectedX * effector.DirectedX) + (effector.DirectedY * effector.DirectedY) > SpringEpsilon)
                        ? effector.DirectedY
                        : outwardY;

                    float magnitude = effector.Strength * MathF.Exp(-distanceSq / effector.SigmaDenom);
                    switch (effector.BehaviorType)
                    {
                        case WarpBehaviorType.Impulse:
                            magnitude *= 2.0f;
                            break;
                        case WarpBehaviorType.Vortex:
                            NormalizeOrZero(-deltaY, deltaX, out directionX, out directionY);
                            magnitude *= 1.35f;
                            break;
                        case WarpBehaviorType.GravityWell:
                            directionX = -outwardX;
                            directionY = -outwardY;
                            magnitude *= 1.25f;
                            break;
                        case WarpBehaviorType.Shockwave:
                        {
                            float ringDistance = MathF.Abs(distance - effector.AnimatedRadius);
                            if (ringDistance > effector.RingWidth)
                                continue;

                            float ringFalloff = 1.0f - (ringDistance / effector.RingWidth);
                            magnitude = effector.Strength * ringFalloff * ringFalloff;
                            directionX = outwardX;
                            directionY = outwardY;
                            break;
                        }
                    }

                    totalX += directionX * magnitude;
                    totalY += directionY * magnitude;
                }

                _accX[i] = totalX;
                _accY[i] = totalY;
            }
        });
    }

    void ApplySpringForces()
    {
        float sleepSq = SleepThreshold * SleepThreshold;
        float restX = _physicsSpacingX;
        float restY = _physicsSpacingY;
        bool useAmbient = _ambientTurbulence > 0.0f;
        float ambientStrength = _ambientTurbulence;
        float phase = _ambientPhase;
        float invGridWidth = GridSizePixels.X > 0.0f ? 1.0f / GridSizePixels.X : 0.0f;
        float invGridHeight = GridSizePixels.Y > 0.0f ? 1.0f / GridSizePixels.Y : 0.0f;

        ForEachPointRange((start, end) =>
        {
            for (int i = start; i < end; i++)
            {
                float totalX = _accX[i];
                float totalY = _accY[i];
                int x = i % PhysNodesX;

                if (_sleeping[i] &&
                    ((totalX * totalX) + (totalY * totalY) <= sleepSq) &&
                    !useAmbient &&
                    AreNeighboringNodesSleeping(i, x))
                {
                    _accX[i] = totalX;
                    _accY[i] = totalY;
                    continue;
                }

                float anchorOffsetX = _anchorX[i] - _posX[i];
                float anchorOffsetY = _anchorY[i] - _posY[i];
                if ((anchorOffsetX * anchorOffsetX) + (anchorOffsetY * anchorOffsetY) > sleepSq ||
                    (_velX[i] * _velX[i]) + (_velY[i] * _velY[i]) > sleepSq)
                {
                    totalX += (anchorOffsetX * _anchorPull) - (_velX[i] * _springDamping);
                    totalY += (anchorOffsetY * _anchorPull) - (_velY[i] * _springDamping);
                }

                if (x > 0)
                    AddNeighborForce(i, i - 1, restX, ref totalX, ref totalY);
                if (x + 1 < PhysNodesX)
                    AddNeighborForce(i, i + 1, restX, ref totalX, ref totalY);
                if (i >= PhysNodesX)
                    AddNeighborForce(i, i - PhysNodesX, restY, ref totalX, ref totalY);
                if (i + PhysNodesX < PointCount)
                    AddNeighborForce(i, i + PhysNodesX, restY, ref totalX, ref totalY);

                if (useAmbient && !(_clampEdges && _edgeMask[i]))
                {
                    float anchorU = _anchorX[i] * invGridWidth;
                    float anchorV = _anchorY[i] * invGridHeight;
                    float gustX = MathF.Sin((anchorV * 10.0f) + (phase * 0.83f)) +
                                  MathF.Cos((anchorU * 14.0f) - (phase * 0.57f));
                    float gustY = MathF.Cos((anchorU * 12.5f) + (phase * 0.71f)) -
                                  MathF.Sin((anchorV * 8.5f) - (phase * 0.49f));
                    totalX += gustX * ambientStrength * 0.35f;
                    totalY += gustY * ambientStrength * 0.24f;
                }

                _accX[i] = totalX;
                _accY[i] = totalY;
            }
        });
    }

    void AddNeighborForce(int pointIndex, int neighborIndex, float restLength, ref float totalX, ref float totalY)
    {
        float offsetX = _posX[neighborIndex] - _posX[pointIndex];
        float offsetY = _posY[neighborIndex] - _posY[pointIndex];
        float distanceSq = (offsetX * offsetX) + (offsetY * offsetY);
        float restLengthSq = restLength * restLength;
        if (distanceSq <= restLengthSq)
            return;

        float distance = MathF.Sqrt(distanceSq);
        if (distance <= SpringEpsilon)
            return;

        float springScale = (distance - restLength) / distance;
        float springOffsetX = offsetX * springScale;
        float springOffsetY = offsetY * springScale;
        float velocityDiffX = _velX[neighborIndex] - _velX[pointIndex];
        float velocityDiffY = _velY[neighborIndex] - _velY[pointIndex];

        totalX += (springOffsetX * _springStiffness) - (velocityDiffX * _springDamping);
        totalY += (springOffsetY * _springStiffness) - (velocityDiffY * _springDamping);
    }

    void UploadPositionsTexture()
    {
        if (_positionsImage == null || _positionsTexture == null || _velocityImage == null || _velocityTexture == null)
            return;

        for (int i = 0; i < PointCount; i++)
        {
            int byteOffset = i * WarpGridGpuManifest.PositionTexelStride;
            WarpGridGpuManifest.WriteTexel(
                _positionsScratch.AsSpan(byteOffset, WarpGridGpuManifest.PositionTexelStride),
                new WarpGridGpuManifest.PackedTexelData(
                    _posX[i],
                    _posY[i],
                    _anchorX[i],
                    _anchorY[i]));

            int velocityByteOffset = i * WarpGridGpuManifest.VelocityTexelStride;
            float velocityMagnitude = MathF.Sqrt((_velX[i] * _velX[i]) + (_velY[i] * _velY[i]));
            WarpGridGpuManifest.WriteVelocityTexel(
                _velocityScratch.AsSpan(velocityByteOffset, WarpGridGpuManifest.VelocityTexelStride),
                velocityMagnitude);
        }

        _positionsImage.SetData(
            PhysNodesX,
            PhysNodesY,
            false,
            WarpGridGpuManifest.PositionImageFormat,
            _positionsScratch);
        _positionsTexture.Update(_positionsImage);
        _velocityImage.SetData(
            PhysNodesX,
            PhysNodesY,
            false,
            WarpGridGpuManifest.VelocityImageFormat,
            _velocityScratch);
        _velocityTexture.Update(_velocityImage);
        _positionsDirty = false;
    }

    int IndexOf(int x, int y) => y * PhysNodesX + x;

    void MaybeRebuild()
    {
        if (IsInsideTree())
            Rebuild();
    }

    static void ClosestPointOnSegment(
        float startX,
        float startY,
        float endX,
        float endY,
        float pointX,
        float pointY,
        out float closestX,
        out float closestY)
    {
        float segmentX = endX - startX;
        float segmentY = endY - startY;
        float denom = (segmentX * segmentX) + (segmentY * segmentY);
        if (denom <= SpringEpsilon)
        {
            closestX = startX;
            closestY = startY;
            return;
        }

        float t = Math.Clamp(
            (((pointX - startX) * segmentX) + ((pointY - startY) * segmentY)) / denom,
            0.0f,
            1.0f);
        closestX = startX + (segmentX * t);
        closestY = startY + (segmentY * t);
    }

    static void NormalizeOrZero(float x, float y, out float nx, out float ny)
    {
        float lengthSq = (x * x) + (y * y);
        if (lengthSq <= SpringEpsilon)
        {
            nx = 0.0f;
            ny = 0.0f;
            return;
        }

        float invLength = 1.0f / MathF.Sqrt(lengthSq);
        nx = x * invLength;
        ny = y * invLength;
    }

    void ForEachPointRange(Action<int, int> action)
    {
        Parallel.ForEach(_pointRanges, range => action(range.Item1, range.Item2));
    }

    void EnsureEffectorCapacity(int requiredCapacity)
    {
        if (_effectorScratch.Length >= requiredCapacity)
            return;

        _effectorScratch = new EffectorRuntime[Math.Max(requiredCapacity, 4)];
    }

    void BuildEffectorPartition(int effectorCount)
    {
        _useEffectorPartition = effectorCount > EffectorPartitionThreshold;
        if (!_useEffectorPartition)
            return;

        _effectorStripCount = Math.Clamp(PhysNodesX, 8, 64);
        _effectorStripWidth = MathF.Max(GridSizePixels.X / _effectorStripCount, 1.0f);
        EnsureEffectorStripCapacity(_effectorStripCount);

        Array.Clear(_effectorStripStarts, 0, _effectorStripCount + 1);

        for (int effectorIndex = 0; effectorIndex < effectorCount; effectorIndex++)
        {
            GetEffectorStripRange(in _effectorScratch[effectorIndex], out int startStrip, out int endStrip);
            for (int strip = startStrip; strip <= endStrip; strip++)
                _effectorStripStarts[strip + 1]++;
        }

        for (int strip = 1; strip <= _effectorStripCount; strip++)
            _effectorStripStarts[strip] += _effectorStripStarts[strip - 1];

        int totalAssignments = _effectorStripStarts[_effectorStripCount];
        if (totalAssignments <= 0)
        {
            _useEffectorPartition = false;
            return;
        }

        EnsureEffectorStripItemCapacity(totalAssignments);
        Array.Copy(_effectorStripStarts, _effectorStripCursor, _effectorStripCount);

        for (int effectorIndex = 0; effectorIndex < effectorCount; effectorIndex++)
        {
            GetEffectorStripRange(in _effectorScratch[effectorIndex], out int startStrip, out int endStrip);
            for (int strip = startStrip; strip <= endStrip; strip++)
            {
                int writeIndex = _effectorStripCursor[strip]++;
                _effectorStripItems[writeIndex] = effectorIndex;
            }
        }
    }

    void EnsureEffectorStripCapacity(int stripCount)
    {
        if (_effectorStripStarts.Length < stripCount + 1)
            _effectorStripStarts = new int[stripCount + 1];
        if (_effectorStripCursor.Length < stripCount)
            _effectorStripCursor = new int[stripCount];
    }

    void EnsureEffectorStripItemCapacity(int itemCount)
    {
        if (_effectorStripItems.Length >= itemCount)
            return;

        _effectorStripItems = new int[itemCount];
    }

    int GetEffectorStripIndex(float positionX)
    {
        int strip = (int)(positionX / _effectorStripWidth);
        return Math.Clamp(strip, 0, _effectorStripCount - 1);
    }

    void GetEffectorStripRange(in EffectorRuntime effector, out int startStrip, out int endStrip)
    {
        float minX = MathF.Min(effector.StartX, effector.EndX) - effector.EffectiveRadius;
        float maxX = MathF.Max(effector.StartX, effector.EndX) + effector.EffectiveRadius;
        startStrip = GetEffectorStripIndex(minX);
        endStrip = GetEffectorStripIndex(maxX);
    }

    float EstimateLoadMetric()
    {
        float minSpacing = MathF.Max(MathF.Min(_physicsSpacingX, _physicsSpacingY), 1.0f);
        float invSpacingSq = 1.0f / (minSpacing * minSpacing);
        float maxMetricSq = 0.0f;
        object sync = new();

        ForEachPointRange((start, end) =>
        {
            float localMaxSq = 0.0f;
            for (int i = start; i < end; i++)
            {
                float displacementX = _posX[i] - _anchorX[i];
                float displacementY = _posY[i] - _anchorY[i];
                float displacementSq = (displacementX * displacementX) + (displacementY * displacementY);
                float velocitySq = (_velX[i] * _velX[i]) + (_velY[i] * _velY[i]);
                float metricSq = MathF.Max(displacementSq, velocitySq) * invSpacingSq;
                if (metricSq > localMaxSq)
                    localMaxSq = metricSq;
            }

            if (localMaxSq <= maxMetricSq)
                return;

            lock (sync)
            {
                if (localMaxSq > maxMetricSq)
                    maxMetricSq = localMaxSq;
            }
        });

        return MathF.Sqrt(maxMetricSq);
    }

    int DetermineSubStepCount(int effectorCount, float loadMetric)
    {
        if (_subSteps <= 1)
            return 1;
        if (loadMetric > 0.85f || effectorCount > 6)
            return _subSteps;
        if (loadMetric > 0.45f || effectorCount > 0)
            return Math.Min(_subSteps, 2);
        return 1;
    }

    bool AreNeighboringNodesSleeping(int index, int x)
    {
        if (x > 0 && !_sleeping[index - 1])
            return false;
        if (x + 1 < PhysNodesX && !_sleeping[index + 1])
            return false;
        if (index >= PhysNodesX && !_sleeping[index - PhysNodesX])
            return false;
        if (index + PhysNodesX < PointCount && !_sleeping[index + PhysNodesX])
            return false;
        return true;
    }

    static int ComputeBatchSize(int pointCount)
    {
        int workerCount = Math.Max(Environment.ProcessorCount, 1);
        int targetChunks = workerCount * 4;
        int chunkSize = Math.Max(pointCount / Math.Max(targetChunks, 1), 64);
        return Math.Min(pointCount, chunkSize);
    }

    void VerifyTextureManifest()
    {
        using var shaderFile = FileAccess.Open("res://shaders/WarpGridDisplay.gdshader", FileAccess.ModeFlags.Read);
        if (shaderFile == null)
            throw new InvalidOperationException("Unable to open WarpGridDisplay.gdshader for manifest verification.");

        string shaderSource = shaderFile.GetAsText();
        WarpGridGpuManifest.VerifyPositionsTextureManifest(shaderSource);
    }
}
