using System;
using System.IO;
using System.Runtime.InteropServices;
using Godot;

namespace WarpGrid;

public enum WarpRenderMode { Grid = 0, Texture = 1 }

[GlobalClass]
public partial class WarpGridManager : Node2D
{
    // v5: GridW/GridH/GridSizePixels are properties so runtime changes trigger Rebuild().
    // Field initializers set the backing fields directly — no setter invocation during
    // C# construction. Godot's scene-load sets these via the setter BEFORE _Ready fires,
    // so the `_rd != null` guard in MaybeRebuild() keeps that path a no-op.
    int _gridW = 180; // Phase 6.9: 144 -> 180 (visual). 5 visual vertices per physics cell (36 phys × 5 = 180).
    int _gridH = 100; // Phase 6.9: 80 -> 100 (visual). 20 phys × 5 = 100. ~6 px per segment @ 1152×648.
    Vector2 _gridSizePixels = new(1152, 648);

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

    // Effectors self-register into the "warp_effectors" group (see WarpEffector.Group).
    // The manager fetches them each physics tick; no manual wiring via Inspector.
    [Export] public Color LineColor = new(0.15f, 0.55f, 1.0f);

    // Phase 6: textured quad-plane — MainTexture samples into fragment via UV. Null = white fallback.
    [Export] public Texture2D MainTexture;

    // Phase 6.1: mutually exclusive display modes. Grid = procedural UV lines; Texture = sampled sprite.
    // Live-updates the display shader uniform on setter invocation.
    WarpRenderMode _renderMode = WarpRenderMode.Grid;
    [Export] public WarpRenderMode RenderMode
    {
        get => _renderMode;
        set
        {
            _renderMode = value;
            if (_material != null) _material.SetShaderParameter("display_mode", (int)value);
        }
    }

    // Phase 6.7: DECOUPLED physics vs visual resolution.
    //   Physics grid — hard-coded 36×20 cells for stability at sub-stepped 240 Hz.
    //   Visual mesh  — GridW × GridH cells (Inspector-driven, default 108×60) for smooth curves.
    //   Display shader samples the low-res physics texture with bilinear filtering,
    //   so the high-res mesh gets perfectly interpolated displacement.
    const int PhysGridW = 36;
    const int PhysGridH = 20;
    int PhysNodesX => PhysGridW + 1;   // 37
    int PhysNodesY => PhysGridH + 1;   // 21

    // Visual mesh node count — derived from user-facing GridW/GridH cell count.
    int VisualNodesX => _gridW + 1;
    int VisualNodesY => _gridH + 1;

    // Phase 7 "pixel-space" — forces are pixel-displacements-per-step (no dt scaling in shader).
    // Constants match VectorGrid Unity source values directly; structural shield (45% cell) in
    // the kernel makes shatters impossible regardless of impulse magnitude.
    const float Stiffness     = 0.28f;   // Phase 7: 2.8 -> 0.28 — Unity pixel-unit neighbor spring
    const float Damping       = 0.45f;   // neighbor axial damping — absorbs pair-wise oscillation
    const float RestStiffness = 0.04f;   // Phase 7: 0.15 -> 0.04 — Unity pixel-unit anchor pull
    const float RestDamping   = 0.06f;   // Phase 7: 0.08 -> 0.06 — mirrors Unity anchor damping
    const float VelDamp       = 0.98f;   // matches Unity m_VelocityDamping
    // Phase 6.7: internal step = 1/240 s. _PhysicsProcess dispatches 4 sub-steps per engine frame.
    const int   SubSteps      = 4;
    const float Dt            = 1.0f / 240.0f;
    const float RestLenScale  = 0.95f;
    const float ImpulseCap    = 0.5f;
    const float FalloffScale  = 500.0f;  // Legacy — retained in UBO; Gaussian falloff path ignores it.
    const int   MaxEffectors  = 128;

    const int StateStride = 16;
    const int RestStride  = 16; // v5: vec2 anchor + float weight + float _pad (std430 stride)
    const int EffStride   = 32;
    const int ParamSize   = 64;
    const int LocalSize   = 8; // Must match layout(local_size_x/y = 8) in WarpGrid.glsl

    RenderingDevice _rd;
    Rid _shader, _pipeline;
    Rid _bufStateA, _bufStateB, _bufRest, _bufEff, _bufParams;
    Rid _imgPositions;
    Rid _uniformSetA, _uniformSetB;
    bool _readIsA = true;

    ArrayMesh _mesh;
    MeshInstance2D _meshInstance;
    ShaderMaterial _material;
    Texture2Drd _positionsTexture;

    byte[] _stateScratch, _restScratch, _effScratch, _paramScratch;
    uint _effCount;

    public override void _Ready()
    {
        if (_rd != null) return; // Idempotency: guard against double-_Ready on reparenting / tool-mode
        System.Diagnostics.Debug.Assert(Marshal.SizeOf<WarpEffectorData>() == 32);

        if (GridW < 1 || GridH < 1)
            throw new Exception($"WarpGridManager: GridW/GridH must be >= 1 cell (got {GridW}x{GridH}).");

        BuildMesh();
        InitGpu();
        BuildMaterial();
    }

    void BuildMesh()
    {
        _mesh = new ArrayMesh();

        var verts   = MeshHelper.BuildGridVertices(VisualNodesX, VisualNodesY, GridSizePixels);
        var uvs     = MeshHelper.BuildGridUVs(VisualNodesX, VisualNodesY);
        var indices = MeshHelper.BuildQuadGridIndices(VisualNodesX, VisualNodesY);

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts;
        arrays[(int)Mesh.ArrayType.TexUV]  = uvs;
        arrays[(int)Mesh.ArrayType.Index]  = indices;
        _mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);

        _meshInstance = new MeshInstance2D { Mesh = _mesh, Texture = MainTexture };
        AddChild(_meshInstance);
    }

    void InitGpu()
    {
        _rd = RenderingServer.GetRenderingDevice();
        if (_rd == null)
            throw new Exception("Main RenderingDevice unavailable — use Forward+ or Mobile renderer.");

        var shaderFile = GD.Load<RDShaderFile>("res://shaders/WarpGrid.glsl");
        var spirv = shaderFile.GetSpirV();
        _shader = _rd.ShaderCreateFromSpirV(spirv);
        _pipeline = _rd.ComputePipelineCreate(_shader);

        int n = PhysNodesX * PhysNodesY;
        _stateScratch = new byte[n * StateStride];
        _restScratch  = new byte[n * RestStride];
        using (var sMs = new MemoryStream(_stateScratch))
        using (var sBw = new BinaryWriter(sMs))
        using (var rMs = new MemoryStream(_restScratch))
        using (var rBw = new BinaryWriter(rMs))
        {
            // Phase 7: positions & anchors stored in ABSOLUTE PIXEL COORDS (0..GridSizePixels).
            // Shader math is "displacement-per-step" in pixels — no normalization, no dt scaling.
            for (int y = 0; y < PhysNodesY; y++)
                for (int x = 0; x < PhysNodesX; x++)
                {
                    float px = (float)x / PhysGridW * GridSizePixels.X;
                    float py = (float)y / PhysGridH * GridSizePixels.Y;
                    sBw.Write(px); sBw.Write(py);
                    sBw.Write(0.0f); sBw.Write(0.0f);
                    rBw.Write(px); rBw.Write(py);
                    rBw.Write(RestAnchorWeight(x, y));        // per-cell weight
                    rBw.Write(0.0f);                          // std430 trailing pad
                }
        }

        _bufStateA = _rd.StorageBufferCreate((uint)_stateScratch.Length, _stateScratch);
        _bufStateB = _rd.StorageBufferCreate((uint)_stateScratch.Length, _stateScratch);
        _bufRest   = _rd.StorageBufferCreate((uint)_restScratch.Length,  _restScratch);

        _effScratch = new byte[MaxEffectors * EffStride];
        _bufEff     = _rd.StorageBufferCreate((uint)_effScratch.Length, _effScratch);

        _paramScratch = new byte[ParamSize];
        _bufParams    = _rd.UniformBufferCreate(ParamSize, _paramScratch);

        var fmt = new RDTextureFormat
        {
            Width       = (uint)PhysNodesX,
            Height      = (uint)PhysNodesY,
            Format      = RenderingDevice.DataFormat.R32G32B32A32Sfloat,
            UsageBits   = RenderingDevice.TextureUsageBits.StorageBit
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
        _positionsTexture = new Texture2Drd { TextureRdRid = _imgPositions };

        var shader = GD.Load<Shader>("res://shaders/WarpGridDisplay.gdshader");
        _material = new ShaderMaterial { Shader = shader };
        _material.SetShaderParameter("positions_tex",    _positionsTexture);
        _material.SetShaderParameter("grid_size_pixels", GridSizePixels);
        // Phase 6.7: grid_dims mirrors PHYS cell count — procedural lines in Grid mode
        // match the "actual" physics grid, not the visual tessellation density.
        _material.SetShaderParameter("grid_dims",        new Vector2I(PhysGridW, PhysGridH));
        // phys_tex_size lets the vertex shader bilinear-sample positions_tex with texel-center
        // offset, so visual-mesh UV=0/1 lands exactly on physics texel(0) / texel(last).
        _material.SetShaderParameter("phys_tex_size",    new Vector2(PhysNodesX, PhysNodesY));
        _material.SetShaderParameter("line_color",       LineColor);
        _material.SetShaderParameter("display_mode",      (int)_renderMode);
        if (MainTexture != null)
            _material.SetShaderParameter("main_tex",     MainTexture);
        _meshInstance.Material = _material;
    }

    // Phase 6.7: 4 sub-steps per engine _PhysicsProcess, shader sees Dt = 1/240 s.
    // At engine PhysicsTicksPerSecond=60 this gives 240 Hz internal rate. Effectors + UBO
    // are uploaded once per engine frame (positions don't change mid-frame) while the compute
    // kernel is dispatched 4× with ping-pong to let wavefronts propagate 4 cells per visible tick.
    public override void _PhysicsProcess(double delta)
    {
        UploadEffectors();
        UploadParams();
        for (int i = 0; i < SubSteps; i++)
        {
            Dispatch();
            _readIsA = !_readIsA;
        }
    }

    void UploadEffectors()
    {
        // std430 SSBO layout — order-critical, must match WarpEffectorData struct in WarpGrid.glsl.
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
            if (n is not WarpEffector eff || !eff.Visible) continue; // Phase 5.1: hidden = no warp

            // AABB cull: skip effectors whose influence box can't touch the grid.
            var p = eff.GlobalPosition;
            float r = eff.Radius;
            if (p.X + r < gridMin.X || p.X - r > gridMax.X) continue;
            if (p.Y + r < gridMin.Y || p.Y - r > gridMax.Y) continue;

            var d = eff.ToData(gridOrigin, GridSizePixels);
            bw.Write(d.StartPoint.X);  bw.Write(d.StartPoint.Y);
            bw.Write(d.EndPoint.X);    bw.Write(d.EndPoint.Y);
            bw.Write(d.Radius);
            bw.Write(d.Strength);
            bw.Write(d.ShapeType);
            bw.Write(d.BehaviorType);
            count++;
        }
        _effCount = (uint)count;
        _rd.BufferUpdate(_bufEff, 0, (uint)_effScratch.Length, _effScratch);
    }

    void UploadParams()
    {
        // std140 UBO layout — order-critical, must match GridParams block in WarpGrid.glsl.
        // Block is 64 bytes; 60 meaningful bytes written, remaining 4 left zeroed as padding.
        Array.Clear(_paramScratch, 0, _paramScratch.Length);
        using var ms = new MemoryStream(_paramScratch);
        using var bw = new BinaryWriter(ms);
        bw.Write((uint)PhysNodesX); bw.Write((uint)PhysNodesY);
        // Phase 7: grid_spacing now in ABSOLUTE PIXELS (cell width/height).
        float sx = GridSizePixels.X / PhysGridW; // e.g. 32 px at 1152 / 36
        float sy = GridSizePixels.Y / PhysGridH; // e.g. 32.4 px at 648 / 20
        bw.Write(sx); bw.Write(sy);
        bw.Write(Dt);
        bw.Write(Stiffness);
        bw.Write(Damping);
        bw.Write(RestStiffness);
        bw.Write(RestDamping);
        bw.Write(VelDamp);
        bw.Write(_effCount);
        bw.Write(RestLenScale);
        bw.Write(ImpulseCap);
        bw.Write(FalloffScale); // offset 52 — sharpens effector near-field (was _pad0 in Phase 4)
        float minDim = Mathf.Min(GridSizePixels.X, GridSizePixels.Y);
        bw.Write(GridSizePixels.X / minDim); // grid_aspect.x at offset 56
        bw.Write(GridSizePixels.Y / minDim); // grid_aspect.y at offset 60
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

    /// <summary>
    /// Per-cell anchor weight (multiplies <c>RestStiffness</c> and <c>RestDamping</c>).
    /// Default 1.0 everywhere. Override for softness maps — e.g., 2.0 near edges, 0.5 in
    /// the interior — to get non-uniform "stiffness fields". Only read once at init;
    /// changing the map at runtime requires <c>Rebuild()</c>.
    /// </summary>
    protected virtual float RestAnchorWeight(int x, int y) => 1.0f;

    public override void _ExitTree()
    {
        TeardownGpu();
    }

    /// <summary>
    /// v5: tear down all RD resources + the mesh child, then re-run BuildMesh + InitGpu
    /// + BuildMaterial from scratch. Called automatically when GridW / GridH / GridSizePixels
    /// change at runtime; can also be called manually after overriding RestAnchorWeight to
    /// re-bake a new softness map. Resets ping-pong state.
    /// </summary>
    public void Rebuild()
    {
        TeardownGpu();
        if (_meshInstance != null) { _meshInstance.Free(); _meshInstance = null; }
        _mesh = null;
        _material = null;
        _positionsTexture = null;
        _readIsA = true;
        BuildMesh();
        InitGpu();
        BuildMaterial();
    }

    void MaybeRebuild()
    {
        // Guard against setter invocations during Godot's scene deserialization (pre-_Ready,
        // _rd is still null). Also no-op while we're in the middle of TeardownGpu.
        if (_rd != null) Rebuild();
    }

    void TeardownGpu()
    {
        if (_rd == null) return;
        // Crash-safety: guard each RID so partial InitGpu failure (mid-construction) cleans up safely.
        FreeIfValid(_uniformSetA); FreeIfValid(_uniformSetB);
        FreeIfValid(_imgPositions);
        FreeIfValid(_bufStateA);   FreeIfValid(_bufStateB);
        FreeIfValid(_bufRest);     FreeIfValid(_bufEff);
        FreeIfValid(_bufParams);
        FreeIfValid(_pipeline);    FreeIfValid(_shader);
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
        if (rid.IsValid) _rd.FreeRid(rid);
    }
}
