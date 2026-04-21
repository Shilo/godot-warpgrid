#nullable enable
using Godot;
using System;
using System.Collections.Generic;

public partial class WarpGridManager : Node2D
{
    [Export] public float Mass { get; set; } = 1.0f;
    [Export] public float Damping { get; set; } = 0.98f;
    [Export] public float Stiffness { get; set; } = 0.3f;
    [Export] public float RestLength { get; set; } = 1.0f;
    [Export] public float MouseStrength { get; set; } = 1500.0f;
    [Export] public float MouseRadius { get; set; } = 120.0f;
    [Export(PropertyHint.Range, "0.1,1.0,0.01")] public float ViewportFill { get; set; } = 1.0f;
    [Export] public Color LineColor { get; set; } = new(0.14f, 0.81f, 0.96f, 1.0f);

    private const int ThreadGroupSizeX = 4;
    private const int ThreadGroupSizeY = 4;
    private const int GridUnitSideX = 15;
    private const int GridUnitSideY = 7;
    private const int MouseUniformSize = 32;
    private const int PropertiesUniformSize = 16;
    private const int DeltaUniformSize = 16;

    private static readonly RenderingDevice.UniformType UniformImage = (RenderingDevice.UniformType)3;
    private static readonly RenderingDevice.UniformType UniformUniformBuffer = (RenderingDevice.UniformType)7;
    private static readonly RenderingDevice.UniformType UniformStorageBuffer = (RenderingDevice.UniformType)8;
    private static readonly RenderingDevice.DataFormat PositionsTextureFormat = (RenderingDevice.DataFormat)108;

    private RenderingDevice? _rd;
    private MeshInstance2D? _display;
    private ShaderMaterial? _displayMaterial;
    private Texture2Drd? _positionsTexture;

    private Rid _positionBufferRid = new();
    private Rid _velocityBufferRid = new();
    private Rid _externalForcesBufferRid = new();
    private Rid _neighboursBufferRid = new();
    private Rid _mouseUniformRid = new();
    private Rid _propertiesUniformRid = new();
    private Rid _deltaUniformRid = new();
    private Rid _positionsTextureRid = new();
    private Rid _velocityShaderRid = new();
    private Rid _positionShaderRid = new();
    private Rid _velocityPipelineRid = new();
    private Rid _positionPipelineRid = new();
    private Rid _velocityUniformSetRid = new();
    private Rid _positionUniformSetRid = new();

    private bool _pointerHeld;
    private bool _touchActive;
    private Vector2 _pointerGlobalPosition;
    private Vector2 _lastViewportSize;
    private bool _resourcesReady;

    private int GridResX => GridUnitSideX * ThreadGroupSizeX;
    private int GridResY => GridUnitSideY * ThreadGroupSizeY;
    private int VertCount => GridResX * GridResY;

    public override void _Ready()
    {
        _lastViewportSize = GetViewportRect().Size;
        Position = _lastViewportSize * 0.5f;
        UpdateDisplayScale();
        _pointerGlobalPosition = GlobalPosition;

        _rd = RenderingServer.GetRenderingDevice();
        if (_rd == null)
        {
            GD.PushError("WarpGridManager requires the main RenderingDevice. Ensure the project is running with the Forward+ or Mobile renderer.");
            SetPhysicsProcess(false);
            return;
        }

        EnsureDisplayNode();
        CreateSimulationResources();
        if (!_velocityShaderRid.IsValid || !_positionShaderRid.IsValid || !_velocityPipelineRid.IsValid || !_positionPipelineRid.IsValid)
        {
            SetPhysicsProcess(false);
            return;
        }

        CreateDisplayResources();
        _resourcesReady = true;
    }

    public override void _Process(double delta)
    {
        Vector2 viewportSize = GetViewportRect().Size;
        Position = viewportSize * 0.5f;
        if (viewportSize == _lastViewportSize)
        {
            return;
        }

        _lastViewportSize = viewportSize;
        Position = viewportSize * 0.5f;
        UpdateDisplayScale();
    }

    public override void _Input(InputEvent @event)
    {
        switch (@event)
        {
            case InputEventMouseButton mouseButton when mouseButton.ButtonIndex == MouseButton.Left:
                _pointerHeld = mouseButton.Pressed;
                _touchActive = false;
                _pointerGlobalPosition = GetGlobalMousePosition();
                break;
            case InputEventMouseMotion:
                if (!_touchActive)
                {
                    _pointerGlobalPosition = GetGlobalMousePosition();
                }
                break;
            case InputEventScreenTouch touch:
                _pointerHeld = touch.Pressed;
                _touchActive = touch.Pressed;
                _pointerGlobalPosition = ScreenToCanvas(touch.Position);
                break;
            case InputEventScreenDrag drag:
                _pointerHeld = true;
                _touchActive = true;
                _pointerGlobalPosition = ScreenToCanvas(drag.Position);
                break;
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        if (!_resourcesReady || _rd == null)
        {
            return;
        }

        if (!_touchActive)
        {
            _pointerGlobalPosition = GetGlobalMousePosition();
        }

        UpdateUniforms((float)delta);
        DispatchSimulation();
    }

    public override void _ExitTree()
    {
        ReleaseResources();
    }

    private void EnsureDisplayNode()
    {
        _display = GetNodeOrNull<MeshInstance2D>("WarpGridDisplay");
        if (_display != null)
        {
            return;
        }

        _display = new MeshInstance2D
        {
            Name = "WarpGridDisplay"
        };
        AddChild(_display);
    }

    private void CreateSimulationResources()
    {
        if (_rd == null)
        {
            return;
        }

        Vector2[] positions = CreateInitialPositions();
        byte[] positionBytes = EncodeVector2Array(positions);
        byte[] zeroForces = EncodeVector2Array(new Vector2[VertCount]);
        byte[] neighbours = EncodeNeighbours();

        _positionBufferRid = _rd.StorageBufferCreate((uint)positionBytes.Length, positionBytes);
        _velocityBufferRid = _rd.StorageBufferCreate((uint)zeroForces.Length, zeroForces);
        _externalForcesBufferRid = _rd.StorageBufferCreate((uint)zeroForces.Length, zeroForces);
        _neighboursBufferRid = _rd.StorageBufferCreate((uint)neighbours.Length, neighbours);
        _mouseUniformRid = _rd.UniformBufferCreate(MouseUniformSize, BuildMouseUniformBytes(Vector2.Zero, MouseStrength, MouseRadius, 0u));
        _propertiesUniformRid = _rd.UniformBufferCreate(PropertiesUniformSize, BuildPropertiesUniformBytes());
        _deltaUniformRid = _rd.UniformBufferCreate(DeltaUniformSize, BuildDeltaUniformBytes(0.0f));
        _positionsTextureRid = CreatePositionsTexture(positions);

        string sharedSource = StripComputeHint(FileAccess.GetFileAsString("res://Shaders/WarpGrid.glsl"));
        _velocityShaderRid = CreateShaderVariant(sharedSource, "WARPGRID_VELOCITY_PASS");
        _positionShaderRid = CreateShaderVariant(sharedSource, "WARPGRID_POSITION_PASS");
        if (!_velocityShaderRid.IsValid || !_positionShaderRid.IsValid)
        {
            GD.PushError("WarpGridManager failed to create one or more compute shader variants.");
            return;
        }

        _velocityPipelineRid = _rd.ComputePipelineCreate(_velocityShaderRid);
        _positionPipelineRid = _rd.ComputePipelineCreate(_positionShaderRid);
        _velocityUniformSetRid = CreateUniformSet(_velocityShaderRid);
        _positionUniformSetRid = CreateUniformSet(_positionShaderRid);
    }

    private void CreateDisplayResources()
    {
        if (_display == null)
        {
            return;
        }

        _positionsTexture = new Texture2Drd
        {
            TextureRdRid = _positionsTextureRid
        };

        Shader shader = GD.Load<Shader>("res://Shaders/WarpGridDisplay.gdshader");
        _displayMaterial = new ShaderMaterial
        {
            Shader = shader
        };
        _displayMaterial.SetShaderParameter("positions_texture", _positionsTexture);
        _displayMaterial.SetShaderParameter("line_color", LineColor);

        _display.Mesh = BuildDisplayMesh();
        _display.Material = _displayMaterial;
    }

    private Rid CreateUniformSet(Rid shaderRid)
    {
        if (_rd == null)
        {
            return new Rid();
        }

        var uniforms = new Godot.Collections.Array<RDUniform>
        {
            CreateUniform(0, UniformStorageBuffer, _positionBufferRid),
            CreateUniform(1, UniformStorageBuffer, _velocityBufferRid),
            CreateUniform(2, UniformStorageBuffer, _externalForcesBufferRid),
            CreateUniform(3, UniformStorageBuffer, _neighboursBufferRid),
            CreateUniform(4, UniformUniformBuffer, _mouseUniformRid),
            CreateUniform(5, UniformUniformBuffer, _propertiesUniformRid),
            CreateUniform(6, UniformUniformBuffer, _deltaUniformRid),
            CreateUniform(7, UniformImage, _positionsTextureRid)
        };

        return _rd.UniformSetCreate(uniforms, shaderRid, 0);
    }

    private Rid CreateShaderVariant(string sharedSource, string define)
    {
        if (_rd == null)
        {
            return new Rid();
        }

        var shaderSource = new RDShaderSource
        {
            SourceCompute = InjectDefineAfterVersion(sharedSource, define)
        };

        RDShaderSpirV spirv = _rd.ShaderCompileSpirVFromSource(shaderSource);
        if (!string.IsNullOrEmpty(spirv.CompileErrorCompute))
        {
            GD.PushError($"{define} compile error: {spirv.CompileErrorCompute}");
            return new Rid();
        }

        return _rd.ShaderCreateFromSpirV(spirv, define);
    }

    private Rid CreatePositionsTexture(Vector2[] positions)
    {
        if (_rd == null)
        {
            return new Rid();
        }

        var format = new RDTextureFormat
        {
            Width = (uint)GridResX,
            Height = (uint)GridResY,
            Depth = 1,
            ArrayLayers = 1,
            Mipmaps = 1,
            TextureType = RenderingDevice.TextureType.Type2D,
            Format = PositionsTextureFormat,
            UsageBits = RenderingDevice.TextureUsageBits.SamplingBit | RenderingDevice.TextureUsageBits.StorageBit
        };

        byte[] initialPixels = BuildInitialTextureBytes(positions);
        var textureData = new Godot.Collections.Array<byte[]>
        {
            initialPixels
        };

        return _rd.TextureCreate(format, new RDTextureView(), textureData);
    }

    private void UpdateUniforms(float delta)
    {
        if (_rd == null)
        {
            return;
        }

        Vector2 localPointer = ToLocal(_pointerGlobalPosition);

        _rd.BufferUpdate(_mouseUniformRid, 0, MouseUniformSize, BuildMouseUniformBytes(localPointer, MouseStrength, MouseRadius, _pointerHeld ? 1u : 0u));
        _rd.BufferUpdate(_propertiesUniformRid, 0, PropertiesUniformSize, BuildPropertiesUniformBytes());
        _rd.BufferUpdate(_deltaUniformRid, 0, DeltaUniformSize, BuildDeltaUniformBytes(delta));
    }

    private void DispatchSimulation()
    {
        if (_rd == null)
        {
            return;
        }

        long computeList = _rd.ComputeListBegin();
        _rd.ComputeListBindComputePipeline(computeList, _velocityPipelineRid);
        _rd.ComputeListBindUniformSet(computeList, _velocityUniformSetRid, 0);
        _rd.ComputeListDispatch(computeList, GridUnitSideX, GridUnitSideY, 1);
        _rd.ComputeListAddBarrier(computeList);
        _rd.ComputeListBindComputePipeline(computeList, _positionPipelineRid);
        _rd.ComputeListBindUniformSet(computeList, _positionUniformSetRid, 0);
        _rd.ComputeListDispatch(computeList, GridUnitSideX, GridUnitSideY, 1);
        _rd.ComputeListEnd();
    }

    private ArrayMesh BuildDisplayMesh()
    {
        var vertices = new List<Vector2>();
        var uvs = new List<Vector2>();

        for (int y = 0; y < GridResY; y++)
        {
            for (int x = 0; x < GridResX - 1; x++)
            {
                vertices.Add(Vector2.Zero);
                vertices.Add(Vector2.Zero);
                uvs.Add(GridToUv(x, y));
                uvs.Add(GridToUv(x + 1, y));
            }
        }

        for (int y = 0; y < GridResY - 1; y++)
        {
            for (int x = 0; x < GridResX; x++)
            {
                vertices.Add(Vector2.Zero);
                vertices.Add(Vector2.Zero);
                uvs.Add(GridToUv(x, y));
                uvs.Add(GridToUv(x, y + 1));
            }
        }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices.ToArray();
        arrays[(int)Mesh.ArrayType.TexUV] = uvs.ToArray();

        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Lines, arrays);
        return mesh;
    }

    private Vector2[] CreateInitialPositions()
    {
        Vector2[] positions = new Vector2[VertCount];

        float worldGridSideLengthX = GetWorldGridSideLengthX();
        float worldGridSideLengthY = GetWorldGridSideLengthY();

        for (int i = 0; i < VertCount; i++)
        {
            float x = (((i % GridResX) - GridResX / 2.0f) / GridResX) * worldGridSideLengthX;
            float y = (((i / GridResX) - GridResY / 2.0f) / GridResY) * worldGridSideLengthY;
            positions[i] = new Vector2(x, y);
        }

        return positions;
    }

    private byte[] EncodeNeighbours()
    {
        int[] data = new int[VertCount * 24];
        int offset = 0;

        for (int i = 0; i < VertCount; i++)
        {
            int[] pairs = GetNeighbourIndexFlagPairs(i);
            Array.Copy(pairs, 0, data, offset, pairs.Length);
            offset += pairs.Length;
        }

        return EncodeIntArray(data);
    }

    private int[] GetNeighbours(int index)
    {
        return new int[]
        {
            index + GridResX,
            index + GridResX + 1,
            index + 1,
            index - GridResX + 1,
            index - GridResX,
            index - GridResX - 1,
            index - 1,
            index + GridResX - 1
        };
    }

    private int[] GetNeighbourIndexFlagPairs(int index)
    {
        int[] neighbourIndexes = GetNeighbours(index);
        int[] bendIndexes = new int[]
        {
            neighbourIndexes[0] + GridResX,
            neighbourIndexes[2] + 1,
            neighbourIndexes[4] - GridResX,
            neighbourIndexes[6] - 1
        };

        int[] neighbours = new int[12];
        Array.Copy(neighbourIndexes, neighbours, neighbourIndexes.Length);
        Array.Copy(bendIndexes, 0, neighbours, 8, bendIndexes.Length);

        int[] pairs = new int[24];
        for (int i = 0; i < 12; i++)
        {
            int idx = neighbours[i];
            int flag = 0;

            if (i % 4 == 0 || i == 10)
            {
                flag = VerticalNeighbourExists(idx, VertCount) ? 1 : 0;
            }
            else if (i == 1 || i == 3)
            {
                flag = VerticalNeighbourExists(idx, VertCount) && EastNeighbourExists(idx, GridResX, VertCount) ? 1 : 0;
            }
            else if (i == 2)
            {
                flag = EastNeighbourExists(idx, GridResX, VertCount) ? 1 : 0;
            }
            else if (i == 9)
            {
                flag = EastBendNeighbourExists(idx, GridResX, VertCount) ? 1 : 0;
            }
            else if (i == 5 || i == 7)
            {
                flag = VerticalNeighbourExists(idx, VertCount) && WestNeighbourExists(idx, GridResX) ? 1 : 0;
            }
            else if (i == 6)
            {
                flag = WestNeighbourExists(idx, GridResX) ? 1 : 0;
            }
            else if (i == 11)
            {
                flag = WestBendNeighbourExists(idx, GridResX) ? 1 : 0;
            }

            pairs[i * 2] = idx;
            pairs[i * 2 + 1] = flag;
        }

        return pairs;
    }

    private bool EastNeighbourExists(int nIdx, int gridSideX, int maxIdx)
    {
        return nIdx % gridSideX > 0 && nIdx < maxIdx;
    }

    private bool EastBendNeighbourExists(int nIdx, int gridSideX, int maxIdx)
    {
        return nIdx % gridSideX > 1 && nIdx < maxIdx;
    }

    private bool WestNeighbourExists(int nIdx, int gridSideX)
    {
        return (nIdx % gridSideX) < (gridSideX - 1) && nIdx >= 0;
    }

    private bool WestBendNeighbourExists(int nIdx, int gridSideX)
    {
        return (nIdx % gridSideX) < (gridSideX - 2) && nIdx >= 0;
    }

    private bool VerticalNeighbourExists(int nIdx, int maxIdx)
    {
        return nIdx >= 0 && nIdx < maxIdx;
    }

    private float GetWorldGridSideLengthX()
    {
        return GridResX * RestLength;
    }

    private float GetWorldGridSideLengthY()
    {
        return GridResY * RestLength;
    }

    private void UpdateDisplayScale()
    {
        Vector2 viewportSize = GetViewportRect().Size;
        if (viewportSize.X <= 0.0f || viewportSize.Y <= 0.0f)
        {
            return;
        }

        float simWidth = Mathf.Max(GetWorldGridSideLengthX(), 1.0f);
        float simHeight = Mathf.Max(GetWorldGridSideLengthY(), 1.0f);
        float scaleX = (viewportSize.X * ViewportFill) / simWidth;
        float scaleY = (viewportSize.Y * ViewportFill) / simHeight;
        float uniformScale = Mathf.Min(scaleX, scaleY);

        Scale = Vector2.One * uniformScale;
    }

    private static RDUniform CreateUniform(int binding, RenderingDevice.UniformType type, Rid rid)
    {
        var uniform = new RDUniform
        {
            Binding = binding,
            UniformType = type
        };
        uniform.AddId(rid);
        return uniform;
    }

    private static byte[] EncodeVector2Array(Vector2[] values)
    {
        float[] flattened = new float[values.Length * 2];
        for (int i = 0; i < values.Length; i++)
        {
            flattened[i * 2] = values[i].X;
            flattened[i * 2 + 1] = values[i].Y;
        }

        return EncodeFloatArray(flattened);
    }

    private static byte[] BuildInitialTextureBytes(Vector2[] positions)
    {
        float[] pixels = new float[positions.Length * 4];
        for (int i = 0; i < positions.Length; i++)
        {
            pixels[i * 4] = positions[i].X;
            pixels[i * 4 + 1] = positions[i].Y;
            pixels[i * 4 + 2] = 0.0f;
            pixels[i * 4 + 3] = 1.0f;
        }

        return EncodeFloatArray(pixels);
    }

    private byte[] BuildPropertiesUniformBytes()
    {
        return EncodeFloatArray(new float[] { Mass, Damping, Stiffness, RestLength });
    }

    private static byte[] BuildDeltaUniformBytes(float delta)
    {
        return EncodeFloatArray(new float[] { delta, 0.0f, 0.0f, 0.0f });
    }

    private static byte[] BuildMouseUniformBytes(Vector2 position, float strength, float radius, uint clickState)
    {
        byte[] bytes = new byte[MouseUniformSize];
        WriteFloat(bytes, 0, position.X);
        WriteFloat(bytes, 4, position.Y);
        WriteFloat(bytes, 8, strength);
        WriteFloat(bytes, 12, radius);
        WriteUInt(bytes, 16, clickState);
        return bytes;
    }

    private static byte[] EncodeFloatArray(float[] values)
    {
        byte[] bytes = new byte[values.Length * sizeof(float)];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static byte[] EncodeIntArray(int[] values)
    {
        byte[] bytes = new byte[values.Length * sizeof(int)];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static void WriteFloat(byte[] bytes, int offset, float value)
    {
        Buffer.BlockCopy(BitConverter.GetBytes(value), 0, bytes, offset, sizeof(float));
    }

    private static void WriteUInt(byte[] bytes, int offset, uint value)
    {
        Buffer.BlockCopy(BitConverter.GetBytes(value), 0, bytes, offset, sizeof(uint));
    }

    private Vector2 GridToUv(int x, int y)
    {
        return new Vector2((x + 0.5f) / GridResX, (y + 0.5f) / GridResY);
    }

    private Vector2 ScreenToCanvas(Vector2 screenPosition)
    {
        return GetCanvasTransform().AffineInverse() * screenPosition;
    }

    private static string StripComputeHint(string source)
    {
        return source.Replace("#[compute]\r\n", string.Empty).Replace("#[compute]\n", string.Empty);
    }

    private static string InjectDefineAfterVersion(string source, string define)
    {
        const string versionLine = "#version 450";
        int versionIndex = source.IndexOf(versionLine, StringComparison.Ordinal);
        if (versionIndex < 0)
        {
            return $"#define {define}\n{source}";
        }

        int lineEndIndex = source.IndexOf('\n', versionIndex);
        if (lineEndIndex < 0)
        {
            return $"{source}\n#define {define}\n";
        }

        return source.Insert(lineEndIndex + 1, $"#define {define}\n");
    }

    private void ReleaseResources()
    {
        if (_rd == null)
        {
            return;
        }

        FreeRid(_positionUniformSetRid);
        FreeRid(_velocityUniformSetRid);
        FreeRid(_velocityPipelineRid);
        FreeRid(_positionPipelineRid);
        FreeRid(_velocityShaderRid);
        FreeRid(_positionShaderRid);
        FreeRid(_positionsTextureRid);
        FreeRid(_deltaUniformRid);
        FreeRid(_propertiesUniformRid);
        FreeRid(_mouseUniformRid);
        FreeRid(_neighboursBufferRid);
        FreeRid(_externalForcesBufferRid);
        FreeRid(_velocityBufferRid);
        FreeRid(_positionBufferRid);
    }

    private void FreeRid(Rid rid)
    {
        if (rid.IsValid)
        {
            _rd?.FreeRid(rid);
        }
    }
}
