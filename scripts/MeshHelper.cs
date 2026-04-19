using Godot;

namespace WarpGrid;

public static class MeshHelper
{
    public static Vector2[] BuildGridVertices(int gridW, int gridH, Vector2 sizePixels)
    {
        var verts = new Vector2[gridW * gridH];
        for (int y = 0; y < gridH; y++)
            for (int x = 0; x < gridW; x++)
                verts[y * gridW + x] = new Vector2(
                    (float)x / (gridW - 1) * sizePixels.X,
                    (float)y / (gridH - 1) * sizePixels.Y);
        return verts;
    }

    public static Vector2[] BuildGridUVs(int gridW, int gridH)
    {
        var uvs = new Vector2[gridW * gridH];
        for (int y = 0; y < gridH; y++)
            for (int x = 0; x < gridW; x++)
                uvs[y * gridW + x] = new Vector2(
                    (float)x / (gridW - 1),
                    (float)y / (gridH - 1));
        return uvs;
    }

    public static int[] BuildQuadGridIndices(int gridW, int gridH)
    {
        int cellsX = gridW - 1;
        int cellsY = gridH - 1;
        var indices = new int[cellsX * cellsY * 6];
        int k = 0;
        for (int y = 0; y < cellsY; y++)
        {
            for (int x = 0; x < cellsX; x++)
            {
                int tl = y * gridW + x;
                int tr = tl + 1;
                int bl = tl + gridW;
                int br = bl + 1;
                indices[k++] = tl; indices[k++] = tr; indices[k++] = bl;
                indices[k++] = tr; indices[k++] = br; indices[k++] = bl;
            }
        }
        return indices;
    }
}
