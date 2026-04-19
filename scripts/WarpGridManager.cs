using System;
using System.IO;
using System.Runtime.InteropServices;
using Godot;

namespace WarpGrid;

[GlobalClass]
public partial class WarpGridManager : Node2D
{
    [Export] public int GridW = 100;
    [Export] public int GridH = 100;
    [Export] public Vector2 GridSizePixels = new(1000, 1000);
    [Export] public Godot.Collections.Array<NodePath> Effectors = new();
    [Export] public Color LineColor = new(0.15f, 0.55f, 1.0f);

    const float Stiffness     = 0.28f;
    const float Damping       = 0.06f;
    const float RestStiffness = 0.10f;
    const float RestDamping   = 0.10f;
    const float VelDamp       = 0.98f;
    const float Dt            = 1.0f / 60.0f;
    const float RestLenScale  = 0.95f;
    const float ImpulseCap    = 0.5f;
    const int   MaxEffectors  = 32;

    const int StateStride = 16;
    const int RestStride  = 8;
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

        if (GridW < 2 || GridH < 2)
            throw new Exception($"WarpGridManager: GridW/GridH must be >= 2 (got {GridW}x{GridH}).");
        if (GridW != GridH || Mathf.Abs(GridSizePixels.X - GridSizePixels.Y) > 0.001f)
            GD.PushWarning($"WarpGridManager: non-square grid ({GridW}x{GridH} @ {GridSizePixels}) " +
                           "will produce anisotropic effector radius + spring rest_len. " +
                           "Use square grid until Phase 4 adds per-axis normalization.");

        BuildMesh();
        InitGpu();
        BuildMaterial();
    }

    void BuildMesh()
    {
        _mesh = new ArrayMesh();

        var verts   = MeshHelper.BuildGridVertices(GridW, GridH, GridSizePixels);
        var indices = MeshHelper.BuildLineGridIndices(GridW, GridH);

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts;
        arrays[(int)Mesh.ArrayType.Index]  = indices;
        _mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Lines, arrays);

        _meshInstance = new MeshInstance2D { Mesh = _mesh };
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

        int n = GridW * GridH;
        _stateScratch = new byte[n * StateStride];
        _restScratch  = new byte[n * RestStride];
        using (var sMs = new MemoryStream(_stateScratch))
        using (var sBw = new BinaryWriter(sMs))
        using (var rMs = new MemoryStream(_restScratch))
        using (var rBw = new BinaryWriter(rMs))
        {
            for (int y = 0; y < GridH; y++)
                for (int x = 0; x < GridW; x++)
                {
                    float nx = (float)x / (GridW - 1);
                    float ny = (float)y / (GridH - 1);
                    sBw.Write(nx); sBw.Write(ny);
                    sBw.Write(0.0f); sBw.Write(0.0f);
                    rBw.Write(nx); rBw.Write(ny);
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
            Width       = (uint)GridW,
            Height      = (uint)GridH,
            Format      = RenderingDevice.DataFormat.R32G32Sfloat,
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
        _material.SetShaderParameter("grid_dims",        new Vector2I(GridW, GridH));
        _material.SetShaderParameter("line_color",       LineColor);
        _meshInstance.Material = _material;
    }

    public override void _PhysicsProcess(double delta)
    {
        UploadEffectors();
        UploadParams();
        Dispatch();
        _readIsA = !_readIsA;
    }

    void UploadEffectors()
    {
        // std430 SSBO layout — order-critical, must match WarpEffectorData struct in WarpGrid.glsl.
        // BinaryWriter writes little-endian (all Godot-supported platforms) which matches GPU expectation.
        Array.Clear(_effScratch, 0, _effScratch.Length);
        int count = 0;
        var origin = GlobalPosition;

        using var ms = new MemoryStream(_effScratch);
        using var bw = new BinaryWriter(ms);
        foreach (var path in Effectors)
        {
            if (count >= MaxEffectors) break;
            var node = GetNodeOrNull<WarpEffector>(path);
            if (node == null) continue;
            var d = node.ToData(origin, GridSizePixels);
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
        // Block is 64 bytes; 52 bytes written here, remaining 12 left zeroed as padding.
        Array.Clear(_paramScratch, 0, _paramScratch.Length);
        using var ms = new MemoryStream(_paramScratch);
        using var bw = new BinaryWriter(ms);
        bw.Write((uint)GridW); bw.Write((uint)GridH);
        float sx = 1.0f / (GridW - 1);
        float sy = 1.0f / (GridH - 1);
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
        _rd.BufferUpdate(_bufParams, 0, (uint)_paramScratch.Length, _paramScratch);
    }

    void Dispatch()
    {
        var set = _readIsA ? _uniformSetA : _uniformSetB;
        uint gx = (uint)((GridW + LocalSize - 1) / LocalSize);
        uint gy = (uint)((GridH + LocalSize - 1) / LocalSize);

        long list = _rd.ComputeListBegin();
        _rd.ComputeListBindComputePipeline(list, _pipeline);
        _rd.ComputeListBindUniformSet(list, set, 0);
        _rd.ComputeListDispatch(list, gx, gy, 1);
        _rd.ComputeListAddBarrier(list);
        _rd.ComputeListEnd();
    }

    public override void _ExitTree()
    {
        if (_rd == null) return;
        // Crash-safety: guard each RID so partial InitGpu failure (mid-construction) cleans up safely.
        FreeIfValid(_uniformSetA); FreeIfValid(_uniformSetB);
        FreeIfValid(_imgPositions);
        FreeIfValid(_bufStateA);   FreeIfValid(_bufStateB);
        FreeIfValid(_bufRest);     FreeIfValid(_bufEff);
        FreeIfValid(_bufParams);
        FreeIfValid(_pipeline);    FreeIfValid(_shader);
        _rd = null;
    }

    void FreeIfValid(Rid rid)
    {
        if (rid.IsValid) _rd.FreeRid(rid);
    }
}
