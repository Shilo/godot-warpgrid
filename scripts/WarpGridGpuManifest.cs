using System;
using System.IO;

namespace WarpGrid;

public static class WarpGridGpuManifest
{
    public const int ParamSize = 48;
    public const int ForceScalerOffset = 36;
    public const int AnchorStiffnessOffset = 40;
    public const int Padding2Offset = 44;

    static readonly (string Fragment, int Offset)[] GridParamsManifest =
    {
        ("uvec2 grid_size;         // offset 0", 0),
        ("vec2  grid_spacing;      // offset 8", 8),
        ("float tension;           // offset 16", 16),
        ("float damping;           // offset 20", 20),
        ("float edge_damp;         // offset 24", 24),
        ("float dir_decay;         // offset 28", 28),
        ("uint  effector_count;    // offset 32", 32),
        ("float force_scaler;      // offset 36", 36),
        ("float anchor_stiffness;  // offset 40", 40),
        ("float _pad2;             // offset 44", 44),
    };

    public readonly record struct GridParamsData(
        uint GridSizeX,
        uint GridSizeY,
        float GridSpacingX,
        float GridSpacingY,
        float Tension,
        float Damping,
        float EdgeDamp,
        float DirDecay,
        uint EffectorCount,
        float ForceScaler,
        float AnchorStiffness);

    public static void VerifyGridParamsManifest(string shaderSource)
    {
        ArgumentNullException.ThrowIfNull(shaderSource);

        foreach (var (fragment, offset) in GridParamsManifest)
        {
            if (!shaderSource.Contains(fragment, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"GridParams manifest mismatch at offset {offset}: missing '{fragment}'.");
            }
        }
    }

    public static byte[] PackGridParams(in GridParamsData data)
    {
        var packed = new byte[ParamSize];
        using var ms = new MemoryStream(packed);
        using var bw = new BinaryWriter(ms);

        bw.Write(data.GridSizeX);
        bw.Write(data.GridSizeY);
        bw.Write(data.GridSpacingX);
        bw.Write(data.GridSpacingY);
        bw.Write(data.Tension);
        bw.Write(data.Damping);
        bw.Write(data.EdgeDamp);
        bw.Write(data.DirDecay);
        bw.Write(data.EffectorCount);
        bw.Write(data.ForceScaler);
        bw.Write(data.AnchorStiffness);
        bw.Write(0.0f);

        return packed;
    }
}
