using System;
using System.Buffers.Binary;
using Godot;

namespace WarpGrid;

public static class WarpGridGpuManifest
{
    public const int PackedTexelSizeBytes = 16;
    public const int PositionTexelStride = PackedTexelSizeBytes;
    public static readonly Image.Format PositionImageFormat = Image.Format.Rgbaf;

    private static readonly string[] DisplayShaderContractFragments =
    {
        "uniform sampler2D positions_tex : filter_linear;",
        "uniform ivec2 display_grid_dims;",
        "uniform float physics_min_spacing = 32.0;",
        "// positions_tex is CPU-packed RGBA32F:",
        "// .xy = current position in pixels",
        "// .zw = anchor position in pixels",
        "vec2 sample_uv = (uv * (phys_tex_size - vec2(1.0)) + vec2(0.5)) / phys_tex_size;",
        "v_warp = position - anchor;",
        "return length(warp) / max(physics_min_spacing, 1.0);",
    };

    public readonly record struct PackedTexelData(
        float PositionX,
        float PositionY,
        float AnchorX,
        float AnchorY);

    public static void VerifyDisplayShaderContract(string shaderSource)
    {
        ArgumentNullException.ThrowIfNull(shaderSource);

        foreach (string fragment in DisplayShaderContractFragments)
        {
            if (!shaderSource.Contains(fragment, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"WarpGrid display shader contract mismatch: missing '{fragment}'.");
            }
        }
    }

    public static void VerifyPositionsTextureManifest(string shaderSource) =>
        VerifyDisplayShaderContract(shaderSource);

    public static byte[] PackTexel(in PackedTexelData data)
    {
        var packed = new byte[PackedTexelSizeBytes];
        WriteTexel(packed, data);
        return packed;
    }

    public static void WriteTexel(Span<byte> destination, in PackedTexelData data)
    {
        if (destination.Length < PackedTexelSizeBytes)
        {
            throw new ArgumentException(
                $"Destination span must be at least {PackedTexelSizeBytes} bytes.",
                nameof(destination));
        }

        BinaryPrimitives.WriteSingleLittleEndian(destination[0..4], data.PositionX);
        BinaryPrimitives.WriteSingleLittleEndian(destination[4..8], data.PositionY);
        BinaryPrimitives.WriteSingleLittleEndian(destination[8..12], data.AnchorX);
        BinaryPrimitives.WriteSingleLittleEndian(destination[12..16], data.AnchorY);
    }
}
