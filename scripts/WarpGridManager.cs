using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;

namespace WarpGrid;

public enum WarpRenderMode { Grid = 0, Texture = 1 }
public enum VibePreset { Custom = 0, GeometryWars = 1 }

[GlobalClass, Tool]
public partial class WarpGridManager : Node2D
{
    const float FixedDt = 1.0f / 120.0f;
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

    float _accumulator;
    float _physicsSpacingX;
    float _physicsSpacingY;

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
    int PointCount => PhysNodesX * PhysNodesY;

    ArrayMesh _mesh;
    MeshInstance2D _meshInstance;
    ShaderMaterial _material;
    Image _positionsImage;
    ImageTexture _positionsTexture;

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
        if (_posX.Length == 0 || _positionsTexture == null)
            return;

        _accumulator += (float)delta;
        while (_accumulator >= FixedDt)
        {
            SimulatePhysics(FixedDt);
            _accumulator -= FixedDt;
        }

        UploadPositionsTexture();
    }

    void SimulatePhysics(float dt)
    {
        _ = dt;

        float invMass = 1.0f / _mass;
        float sleepSq = SleepThreshold * SleepThreshold;

        Parallel.For(0, PointCount, i =>
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
        });

        var effectors = GatherActiveEffectors();
        ApplyEffectors(effectors);
        ApplySpringForces();

        Parallel.For(0, PointCount, i =>
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
                return;
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
                return;
            }

            float velX = _velX[i] + (accX * invMass);
            float velY = _velY[i] + (accY * invMass);
            velX *= _globalDamping;
            velY *= _globalDamping;

            float posX = _posX[i] + velX;
            float posY = _posY[i] + velY;

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
        });
    }

    EffectorRuntime[] GatherActiveEffectors()
    {
        float gridOriginX = GlobalPosition.X;
        float gridOriginY = GlobalPosition.Y;
        float gridMaxX = gridOriginX + GridSizePixels.X;
        float gridMaxY = gridOriginY + GridSizePixels.Y;

        var active = new List<EffectorRuntime>();
        foreach (Node node in GetTree().GetNodesInGroup(WarpEffector.Group))
        {
            if (node is not WarpEffector eff || !eff.Visible)
                continue;

            WarpEffectorData data = eff.ToData(GlobalPosition, GridSizePixels);
            float globalX = eff.GlobalPosition.X;
            float globalY = eff.GlobalPosition.Y;
            float effectiveRadius = MathF.Max(data.Radius, data.AnimatedRadius + data.RingWidth);
            if (globalX + effectiveRadius < gridOriginX || globalX - effectiveRadius > gridMaxX)
                continue;
            if (globalY + effectiveRadius < gridOriginY || globalY - effectiveRadius > gridMaxY)
                continue;

            active.Add(new EffectorRuntime(data));
        }

        return active.ToArray();
    }

    void ApplyEffectors(EffectorRuntime[] effectors)
    {
        if (effectors.Length == 0)
            return;

        Parallel.For(0, PointCount, i =>
        {
            float px = _posX[i];
            float py = _posY[i];
            float totalX = 0.0f;
            float totalY = 0.0f;

            foreach (var effector in effectors)
            {
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
        });
    }

    void ApplySpringForces()
    {
        float sleepSq = SleepThreshold * SleepThreshold;
        float restX = _physicsSpacingX;
        float restY = _physicsSpacingY;

        Parallel.For(0, PointCount, i =>
        {
            float totalX = _accX[i];
            float totalY = _accY[i];

            if (!(_sleeping[i] && ((_accX[i] * _accX[i]) + (_accY[i] * _accY[i]) <= sleepSq)))
            {
                float anchorOffsetX = _anchorX[i] - _posX[i];
                float anchorOffsetY = _anchorY[i] - _posY[i];
                if ((anchorOffsetX * anchorOffsetX) + (anchorOffsetY * anchorOffsetY) > sleepSq ||
                    (_velX[i] * _velX[i]) + (_velY[i] * _velY[i]) > sleepSq)
                {
                    totalX += (anchorOffsetX * _anchorPull) - (_velX[i] * _springDamping);
                    totalY += (anchorOffsetY * _anchorPull) - (_velY[i] * _springDamping);
                }
            }

            int x = i % PhysNodesX;

            if (x > 0)
                AddNeighborForce(i, i - 1, restX, ref totalX, ref totalY);
            if (x + 1 < PhysNodesX)
                AddNeighborForce(i, i + 1, restX, ref totalX, ref totalY);
            if (i >= PhysNodesX)
                AddNeighborForce(i, i - PhysNodesX, restY, ref totalX, ref totalY);
            if (i + PhysNodesX < PointCount)
                AddNeighborForce(i, i + PhysNodesX, restY, ref totalX, ref totalY);

            _accX[i] = totalX;
            _accY[i] = totalY;
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
        if (_positionsImage == null || _positionsTexture == null)
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

    void VerifyTextureManifest()
    {
        using var shaderFile = FileAccess.Open("res://shaders/WarpGridDisplay.gdshader", FileAccess.ModeFlags.Read);
        if (shaderFile == null)
            throw new InvalidOperationException("Unable to open WarpGridDisplay.gdshader for manifest verification.");

        string shaderSource = shaderFile.GetAsText();
        WarpGridGpuManifest.VerifyPositionsTextureManifest(shaderSource);
    }
}
