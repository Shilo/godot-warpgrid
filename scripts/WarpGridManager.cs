using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Godot;

namespace WarpGrid;

public enum WarpRenderMode { Grid = 0, Texture = 1 }
public enum VibePreset { Classic = 0, Arcade = 1, Heavy = 2, Ghost = 3 }

[GlobalClass, Tool]
public partial class WarpGridManager : Node2D
{
    int _gridW = 180;
    int _gridH = 100;
    Vector2 _gridSizePixels = new(1152, 648);

    [ExportGroup("Grid")]
    [Export] public int GridW
    {
        get => _gridW;
        set { if (_gridW == value) return; _gridW = value; MaybeRebuild(); }
    }

    [Export] public int GridH
    {
        get => _gridH;
        set { if (_gridH == value) return; _gridH = value; MaybeRebuild(); }
    }

    [Export] public Vector2 GridSizePixels
    {
        get => _gridSizePixels;
        set { if (_gridSizePixels == value) return; _gridSizePixels = value; MaybeRebuild(); }
    }

    [ExportGroup("Display")]
    [Export] public Color LineColor = new(0.15f, 0.55f, 1.0f);
    [Export] public Texture2D MainTexture;

    WarpRenderMode _renderMode = WarpRenderMode.Grid;
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

    const int PhysGridW = 36;
    const int PhysGridH = 20;
    int PhysNodesX => PhysGridW + 1;
    int PhysNodesY => PhysGridH + 1;
    int VisualNodesX => _gridW + 1;
    int VisualNodesY => _gridH + 1;

    int _subSteps = 2;
    int _solverIterations = 4;
    float _neighborStiffness = 0.35f;
    float _anchorStiffness = 0.10f;
    float _globalDamping = 0.985f;
    float _friction = 0.12f;
    bool _boundaryPinning = true;
    float _chromaticStrength = 1.0f;
    float _spiralFactor = 0.72f;
    float _persistence = 0.10f;
    float _motionBlurStrength = 0.20f;
    float _stretchTaperStrength = 1.0f;
    float _jitterStrength = 0.05f;
    Gradient _energyGradient;
    Texture2D _physicsMask;
    global::WarpGrid.VibePreset _vibePreset = global::WarpGrid.VibePreset.Arcade;

    [ExportGroup("Physics (PBD)")]
    [Export] public int SubSteps
    {
        get => _subSteps;
        set
        {
            value = Math.Max(1, value);
            if (_subSteps == value) return;
            _subSteps = value;
            RefreshDispatchPlan();
            MaybeUploadParams();
        }
    }

    [Export] public int SolverIterations
    {
        get => _solverIterations;
        set
        {
            value = Math.Max(1, value);
            if (_solverIterations == value) return;
            _solverIterations = value;
            RefreshDispatchPlan();
            MaybeUploadParams();
        }
    }

    [Export] public float NeighborStiffness
    {
        get => _neighborStiffness;
        set { if (Mathf.IsEqualApprox(_neighborStiffness, value)) return; _neighborStiffness = value; MaybeUploadParams(); }
    }

    [Export] public Texture2D PhysicsMask
    {
        get => _physicsMask;
        set { if (_physicsMask == value) return; _physicsMask = value; MaybeRebuild(); }
    }

    [Export] public float AnchorStiffness
    {
        get => _anchorStiffness;
        set { if (Mathf.IsEqualApprox(_anchorStiffness, value)) return; _anchorStiffness = value; MaybeUploadParams(); }
    }

    [Export] public float GlobalDamping
    {
        get => _globalDamping;
        set { if (Mathf.IsEqualApprox(_globalDamping, value)) return; _globalDamping = value; MaybeUploadParams(); }
    }

    [Export] public float Friction
    {
        get => _friction;
        set { if (Mathf.IsEqualApprox(_friction, value)) return; _friction = value; MaybeUploadParams(); }
    }

    [Export] public bool BoundaryPinning
    {
        get => _boundaryPinning;
        set { if (_boundaryPinning == value) return; _boundaryPinning = value; MaybeUploadParams(); }
    }

    [ExportGroup("Visuals")]
    [Export] public float ChromaticStrength
    {
        get => _chromaticStrength;
        set
        {
            if (Mathf.IsEqualApprox(_chromaticStrength, value)) return;
            _chromaticStrength = value;
            if (_material != null)
                _material.SetShaderParameter("chromatic_strength", _chromaticStrength);
        }
    }

    [Export] public Gradient EnergyGradient
    {
        get => _energyGradient;
        set
        {
            _energyGradient = value;
            RebuildEnergyGradientTexture();
            if (_material != null)
                _material.SetShaderParameter("energy_gradient_tex", _energyGradientTexture);
        }
    }

    [Export] public float Persistence
    {
        get => _persistence;
        set
        {
            if (Mathf.IsEqualApprox(_persistence, value)) return;
            _persistence = value;
            if (_material != null)
                _material.SetShaderParameter("persistence", _persistence);
        }
    }

    [Export] public float MotionBlurStrength
    {
        get => _motionBlurStrength;
        set
        {
            if (Mathf.IsEqualApprox(_motionBlurStrength, value)) return;
            _motionBlurStrength = value;
            if (_material != null)
                _material.SetShaderParameter("motion_blur_strength", _motionBlurStrength);
        }
    }

    [Export] public float StretchTaperStrength
    {
        get => _stretchTaperStrength;
        set
        {
            if (Mathf.IsEqualApprox(_stretchTaperStrength, value)) return;
            _stretchTaperStrength = value;
            if (_material != null)
                _material.SetShaderParameter("stretch_taper_strength", _stretchTaperStrength);
        }
    }

    [Export] public float JitterStrength
    {
        get => _jitterStrength;
        set
        {
            if (Mathf.IsEqualApprox(_jitterStrength, value)) return;
            _jitterStrength = value;
            if (_material != null)
                _material.SetShaderParameter("jitter_strength", _jitterStrength);
        }
    }

    [Export] public float SpiralFactor
    {
        get => _spiralFactor;
        set { if (Mathf.IsEqualApprox(_spiralFactor, value)) return; _spiralFactor = value; MaybeUploadParams(); }
    }

    [Export] public global::WarpGrid.VibePreset VibePreset
    {
        get => _vibePreset;
        set
        {
            if (_vibePreset == value) return;
            _vibePreset = value;
            ApplyPreset(value);
        }
    }

    [ExportGroup("Tools")]
    [Export] public bool ForceRebuild
    {
        get => false;
        set
        {
            if (!value) return;
            GD.Print("[WarpGrid] ForceRebuild invoked");
            Rebuild();
        }
    }

    const int MaxEffectors = 128;
    const int StateStride = 16;
    const int RestStride = 16;
    const int EffStride = 32;
    const int ParamSize = 64;
    const int LocalSize = 8;
    const int SpiralFactorOffset = 52;

    RenderingDevice _rd;
    Rid _shader, _pipeline;
    Rid _bufStateA, _bufStateB, _bufRest, _bufEff, _bufParams;
    Rid _imgPositions;
    Rid _uniformSetA, _uniformSetB;
    bool _readIsA = true;
    bool _isInitialized;

    ArrayMesh _mesh;
    MeshInstance2D _meshInstance;
    ShaderMaterial _material;
    Texture2Drd _positionsTexture;
    GradientTexture1D _energyGradientTexture;

    byte[] _stateScratch, _restScratch, _effScratch, _paramScratch;
    uint _effCount;
    WarpGridDispatchPhase[] _dispatchPhases = Array.Empty<WarpGridDispatchPhase>();
    WarpGridDispatchPhase _lastDispatchPhase = new(0, 0, WarpGridDispatchPhaseKind.Prediction, true);

    public override void _Ready()
    {
        if (_rd != null) return;
        System.Diagnostics.Debug.Assert(Marshal.SizeOf<WarpEffectorData>() == 32);

        if (GridW < 1 || GridH < 1)
            throw new Exception($"WarpGridManager: GridW/GridH must be >= 1 cell (got {GridW}x{GridH}).");

        VerifyGpuManifest();
        RebuildEnergyGradientTexture();
        RefreshDispatchPlan();
        BuildMesh();
        InitGpu();
        BuildMaterial();
    }

    void RefreshDispatchPlan()
    {
        _dispatchPhases = WarpGridPbdDispatchPlan.Build(_subSteps, _solverIterations);
    }

    void BuildMesh()
    {
        GD.Print($"[WarpGrid] BuildMesh phys={PhysGridW}x{PhysGridH} visual={VisualNodesX}x{VisualNodesY}");
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

    void InitGpu()
    {
        GD.Print("[WarpGrid] InitGpu");
        _rd = RenderingServer.GetRenderingDevice();
        if (_rd == null)
        {
            GD.PushWarning("WarpGridManager: RenderingDevice unavailable (Compatibility renderer?). Switch the project renderer to Forward+ or Mobile for the PBD simulation.");
            return;
        }

        var shaderFile = GD.Load<RDShaderFile>("res://shaders/WarpGrid.glsl");
        var spirv = shaderFile.GetSpirV();
        _shader = _rd.ShaderCreateFromSpirV(spirv);
        _pipeline = _rd.ComputePipelineCreate(_shader);

        int n = PhysNodesX * PhysNodesY;
        _stateScratch = new byte[n * StateStride];
        _restScratch = new byte[n * RestStride];

        using (var sMs = new MemoryStream(_stateScratch))
        using (var sBw = new BinaryWriter(sMs))
        using (var rMs = new MemoryStream(_restScratch))
        using (var rBw = new BinaryWriter(rMs))
        {
            Image physicsMaskImage = _physicsMask?.GetImage();
            for (int y = 0; y < PhysNodesY; y++)
            {
                for (int x = 0; x < PhysNodesX; x++)
                {
                    float px = (float)x / PhysGridW * GridSizePixels.X;
                    float py = (float)y / PhysGridH * GridSizePixels.Y;

                    // NodeState = { current.xy, prev.xy } in absolute pixel space.
                    sBw.Write(px); sBw.Write(py);
                    sBw.Write(px); sBw.Write(py);

                    rBw.Write(px); rBw.Write(py);
                    rBw.Write(RestAnchorWeight(x, y) * PhysicsMaskWeight(physicsMaskImage, x, y));
                    rBw.Write(0.0f);
                }
            }
        }

        _bufStateA = _rd.StorageBufferCreate((uint)_stateScratch.Length, _stateScratch);
        _bufStateB = _rd.StorageBufferCreate((uint)_stateScratch.Length, _stateScratch);
        _bufRest = _rd.StorageBufferCreate((uint)_restScratch.Length, _restScratch);

        _effScratch = new byte[MaxEffectors * EffStride];
        _bufEff = _rd.StorageBufferCreate((uint)_effScratch.Length, _effScratch);

        _paramScratch = new byte[ParamSize];
        _bufParams = _rd.UniformBufferCreate(ParamSize, _paramScratch);

        var fmt = new RDTextureFormat
        {
            Width = (uint)PhysNodesX,
            Height = (uint)PhysNodesY,
            Format = RenderingDevice.DataFormat.R32G32B32A32Sfloat,
            UsageBits = RenderingDevice.TextureUsageBits.StorageBit
                      | RenderingDevice.TextureUsageBits.SamplingBit
                      | RenderingDevice.TextureUsageBits.CanCopyFromBit,
            TextureType = RenderingDevice.TextureType.Type2D,
        };
        var view = new RDTextureView();
        _imgPositions = _rd.TextureCreate(fmt, view);

        _uniformSetA = CreateUniformSet(_bufStateA, _bufStateB);
        _uniformSetB = CreateUniformSet(_bufStateB, _bufStateA);
    }

    Rid CreateUniformSet(Rid readBuf, Rid writeBuf)
    {
        static RDUniform U(RenderingDevice.UniformType t, int bind, Rid id)
        {
            var u = new RDUniform { UniformType = t, Binding = bind };
            u.AddId(id);
            return u;
        }

        var set = new Godot.Collections.Array<RDUniform>
        {
            U(RenderingDevice.UniformType.StorageBuffer, 0, readBuf),
            U(RenderingDevice.UniformType.StorageBuffer, 1, writeBuf),
            U(RenderingDevice.UniformType.StorageBuffer, 2, _bufRest),
            U(RenderingDevice.UniformType.StorageBuffer, 3, _bufEff),
            U(RenderingDevice.UniformType.UniformBuffer, 4, _bufParams),
            U(RenderingDevice.UniformType.Image,         5, _imgPositions),
        };
        return _rd.UniformSetCreate(set, _shader, 0);
    }

    void BuildMaterial()
    {
        GD.Print($"[WarpGrid] BuildMaterial (gpu_ready={_rd != null})");
        var shader = GD.Load<Shader>("res://shaders/WarpGridDisplay.gdshader");
        _material = new ShaderMaterial { Shader = shader };

        if (_imgPositions.IsValid)
        {
            _positionsTexture = new Texture2Drd { TextureRdRid = _imgPositions };
            _material.SetShaderParameter("positions_tex", _positionsTexture);
        }

        _material.SetShaderParameter("grid_size_pixels", GridSizePixels);
        _material.SetShaderParameter("grid_dims", new Vector2I(PhysGridW, PhysGridH));
        _material.SetShaderParameter("phys_tex_size", new Vector2(PhysNodesX, PhysNodesY));
        _material.SetShaderParameter("line_color", LineColor);
        _material.SetShaderParameter("display_mode", (int)_renderMode);
        _material.SetShaderParameter("chromatic_strength", _chromaticStrength);
        _material.SetShaderParameter("energy_gradient_tex", _energyGradientTexture);
        _material.SetShaderParameter("persistence", _persistence);
        _material.SetShaderParameter("motion_blur_strength", _motionBlurStrength);
        _material.SetShaderParameter("stretch_taper_strength", _stretchTaperStrength);
        _material.SetShaderParameter("jitter_strength", _jitterStrength);
        if (MainTexture != null)
            _material.SetShaderParameter("main_tex", MainTexture);

        _meshInstance.Material = _material;
        _isInitialized = true;
    }

    public override void _PhysicsProcess(double delta)
    {
        if (!_isInitialized || _rd == null || !_pipeline.IsValid) return;

        UploadEffectors();

        foreach (var phase in _dispatchPhases)
        {
            UploadParams(phase);
            Dispatch();
            _readIsA = !_readIsA;
            _lastDispatchPhase = phase;
        }
    }

    void UploadEffectors()
    {
        Array.Clear(_effScratch, 0, _effScratch.Length);
        int count = 0;
        var gridOrigin = GlobalPosition;
        var gridMin = gridOrigin;
        var gridMax = gridOrigin + GridSizePixels;

        using var ms = new MemoryStream(_effScratch);
        using var bw = new BinaryWriter(ms);
        foreach (Node n in GetTree().GetNodesInGroup(WarpEffector.Group))
        {
            if (count >= MaxEffectors) break;
            if (n is not WarpEffector eff || !eff.Visible) continue;

            var p = eff.GlobalPosition;
            float r = eff.Radius;
            if (p.X + r < gridMin.X || p.X - r > gridMax.X) continue;
            if (p.Y + r < gridMin.Y || p.Y - r > gridMax.Y) continue;

            var d = eff.ToData(gridOrigin, GridSizePixels);
            bw.Write(d.StartPoint.X); bw.Write(d.StartPoint.Y);
            bw.Write(d.EndPoint.X); bw.Write(d.EndPoint.Y);
            bw.Write(d.Radius);
            bw.Write(d.Strength);
            bw.Write(d.ShapeType);
            bw.Write(d.BehaviorType);
            count++;
        }

        _effCount = (uint)count;
        _rd.BufferUpdate(_bufEff, 0, (uint)_effScratch.Length, _effScratch);
    }

    void UploadParams(WarpGridDispatchPhase phase)
    {
        Array.Clear(_paramScratch, 0, _paramScratch.Length);
        using var ms = new MemoryStream(_paramScratch);
        using var bw = new BinaryWriter(ms);

        bw.Write((uint)PhysNodesX);
        bw.Write((uint)PhysNodesY);

        float sx = GridSizePixels.X / PhysGridW;
        float sy = GridSizePixels.Y / PhysGridH;
        bw.Write(sx);
        bw.Write(sy);

        bw.Write(Mathf.Clamp(_neighborStiffness, 0.0f, 1.0f));
        bw.Write(Mathf.Clamp(_anchorStiffness, 0.0f, 1.0f));
        bw.Write(Mathf.Clamp(_globalDamping, 0.0f, 1.0f));
        bw.Write(Mathf.Clamp(_friction, 0.0f, 1.0f));
        bw.Write(_effCount);
        bw.Write(1.0f / _subSteps);
        bw.Write((uint)phase.Kind);
        bw.Write(phase.ApplyEffectors ? 1u : 0u);
        bw.Write(_boundaryPinning ? 1u : 0u);
        bw.Write(Mathf.Clamp(_spiralFactor, 0.0f, 1.0f));

        _rd.BufferUpdate(_bufParams, 0, (uint)_paramScratch.Length, _paramScratch);
    }

    void Dispatch()
    {
        var set = _readIsA ? _uniformSetA : _uniformSetB;
        uint gx = (uint)((PhysNodesX + LocalSize - 1) / LocalSize);
        uint gy = (uint)((PhysNodesY + LocalSize - 1) / LocalSize);

        long list = _rd.ComputeListBegin();
        _rd.ComputeListBindComputePipeline(list, _pipeline);
        _rd.ComputeListBindUniformSet(list, set, 0);
        _rd.ComputeListDispatch(list, gx, gy, 1);
        _rd.ComputeListAddBarrier(list);
        _rd.ComputeListEnd();
    }

    void RebuildEnergyGradientTexture()
    {
        if (_energyGradient == null)
            _energyGradient = CreateDefaultEnergyGradient();

        _energyGradientTexture ??= new GradientTexture1D();
        _energyGradientTexture.Width = 256;
        _energyGradientTexture.Gradient = _energyGradient;
    }

    Gradient CreateDefaultEnergyGradient()
    {
        return new Gradient
        {
            Colors = new[] { LineColor, LineColor.Lightened(0.35f), Colors.White },
            Offsets = new[] { 0.0f, 0.65f, 1.0f }
        };
    }

    void ApplyPreset(global::WarpGrid.VibePreset preset)
    {
        switch (preset)
        {
            case global::WarpGrid.VibePreset.Classic:
                SubSteps = 2;
                SolverIterations = 2;
                NeighborStiffness = 0.22f;
                AnchorStiffness = 0.10f;
                GlobalDamping = 0.992f;
                Friction = 0.06f;
                SpiralFactor = 0.35f;
                BoundaryPinning = false;
                break;
            case global::WarpGrid.VibePreset.Arcade:
                SubSteps = 2;
                SolverIterations = 4;
                NeighborStiffness = 0.35f;
                AnchorStiffness = 0.10f;
                GlobalDamping = 0.985f;
                Friction = 0.12f;
                SpiralFactor = 0.72f;
                BoundaryPinning = true;
                break;
            case global::WarpGrid.VibePreset.Heavy:
                SubSteps = 2;
                SolverIterations = 4;
                NeighborStiffness = 0.58f;
                AnchorStiffness = 0.42f;
                GlobalDamping = 0.78f;
                Friction = 0.48f;
                SpiralFactor = 0.50f;
                BoundaryPinning = true;
                break;
            case global::WarpGrid.VibePreset.Ghost:
                SubSteps = 3;
                SolverIterations = 2;
                NeighborStiffness = 0.34f;
                AnchorStiffness = 0.08f;
                GlobalDamping = 0.998f;
                Friction = 0.0f;
                SpiralFactor = 0.88f;
                BoundaryPinning = true;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(preset), preset, "Unknown vibe preset.");
        }
    }

    void VerifyGpuManifest()
    {
        string shaderPath = ProjectSettings.GlobalizePath("res://shaders/WarpGrid.glsl");
        if (!File.Exists(shaderPath))
        {
            string missingMessage = $"WarpGridManager: compute shader manifest file not found at '{shaderPath}'.";
            GD.PushError(missingMessage);
            throw new Exception(missingMessage);
        }

        string source = File.ReadAllText(shaderPath);
        const string prefix = "// GPU_MANIFEST ";
        int start = source.IndexOf(prefix, StringComparison.Ordinal);
        if (start < 0)
        {
            string missingManifest = "WarpGridManager: missing GPU_MANIFEST line in shaders/WarpGrid.glsl.";
            GD.PushError(missingManifest);
            throw new Exception(missingManifest);
        }

        int end = source.IndexOf('\n', start);
        string manifestLine = end >= 0
            ? source.Substring(start + prefix.Length, end - start - prefix.Length).Trim()
            : source[(start + prefix.Length)..].Trim();

        var manifestValues = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string pair in manifestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] kv = pair.Split('=', 2, StringSplitOptions.TrimEntries);
            if (kv.Length != 2 || !int.TryParse(kv[1], out int parsed))
                continue;
            manifestValues[kv[0]] = parsed;
        }

        ValidateManifestValue(manifestValues, "STATE_STRIDE", StateStride);
        ValidateManifestValue(manifestValues, "REST_STRIDE", RestStride);
        ValidateManifestValue(manifestValues, "EFF_STRIDE", EffStride);
        ValidateManifestValue(manifestValues, "PARAM_SIZE", ParamSize);
        ValidateManifestValue(manifestValues, "SPIRAL_FACTOR_OFFSET", SpiralFactorOffset);

        if (!source.Contains("float spiral_factor;", StringComparison.Ordinal))
        {
            string missingSpiral = "WarpGridManager: GLSL manifest is present, but GridParams is missing 'spiral_factor'.";
            GD.PushError(missingSpiral);
            throw new Exception(missingSpiral);
        }
    }

    void ValidateManifestValue(Dictionary<string, int> manifestValues, string key, int actualValue)
    {
        if (!manifestValues.TryGetValue(key, out int expectedValue))
        {
            string missingKey = $"WarpGridManager: GPU manifest missing '{key}' in shaders/WarpGrid.glsl.";
            GD.PushError(missingKey);
            throw new Exception(missingKey);
        }

        if (expectedValue == actualValue)
            return;

        string mismatch = $"WarpGridManager: GPU manifest mismatch for {key}. C#={actualValue}, GLSL={expectedValue}. Refusing to initialize mismatched buffers.";
        GD.PushError(mismatch);
        throw new Exception(mismatch);
    }

    float PhysicsMaskWeight(Image maskImage, int x, int y)
    {
        if (maskImage == null || maskImage.IsEmpty())
            return 1.0f;

        int maxX = Math.Max(maskImage.GetWidth() - 1, 0);
        int maxY = Math.Max(maskImage.GetHeight() - 1, 0);
        int sampleX = Mathf.Clamp(Mathf.RoundToInt((float)x / Math.Max(PhysNodesX - 1, 1) * maxX), 0, maxX);
        int sampleY = Mathf.Clamp(Mathf.RoundToInt((float)y / Math.Max(PhysNodesY - 1, 1) * maxY), 0, maxY);
        Color c = maskImage.GetPixel(sampleX, sampleY);
        float luma = Mathf.Clamp(c.R * 0.299f + c.G * 0.587f + c.B * 0.114f, 0.0f, 1.0f);
        // Darker pixels create slipperier zones, brighter pixels create stiffer/stickier zones.
        return Mathf.Lerp(0.4f, 1.6f, luma);
    }

    protected virtual float RestAnchorWeight(int x, int y) => 1.0f;

    public override void _ExitTree()
    {
        TeardownGpu();
    }

    public void Rebuild()
    {
        if (_meshInstance != null)
        {
            _meshInstance.Free();
            _meshInstance = null;
        }

        _mesh = null;
        _material = null;
        TeardownGpu();
        _readIsA = true;
        RefreshDispatchPlan();
        BuildMesh();
        InitGpu();
        BuildMaterial();
    }

    void MaybeRebuild()
    {
        if (_rd != null)
            Rebuild();
    }

    void MaybeUploadParams()
    {
        if (_rd != null)
            UploadParams(_lastDispatchPhase);
    }

    void TeardownGpu()
    {
        GD.Print("[WarpGrid] TeardownGpu");
        _isInitialized = false;
        if (_rd == null) return;

        if (_positionsTexture != null)
        {
            _positionsTexture.TextureRdRid = default;
            _positionsTexture = null;
        }

        FreeIfValid(_uniformSetA); FreeIfValid(_uniformSetB);
        FreeIfValid(_imgPositions);
        FreeIfValid(_bufStateA); FreeIfValid(_bufStateB);
        FreeIfValid(_bufRest); FreeIfValid(_bufEff);
        FreeIfValid(_bufParams);
        FreeIfValid(_pipeline); FreeIfValid(_shader);

        _uniformSetA = default; _uniformSetB = default;
        _imgPositions = default;
        _bufStateA = default; _bufStateB = default;
        _bufRest = default; _bufEff = default;
        _bufParams = default;
        _pipeline = default; _shader = default;
        _rd = null;
    }

    void FreeIfValid(Rid rid)
    {
        if (rid.IsValid)
            _rd.FreeRid(rid);
    }
}
