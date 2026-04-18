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

    public static int[] BuildLineGridIndices(int gridW, int gridH)
    {
        int total  = gridW * gridH;
        int hLines = gridH * (gridW - 1);
        int vLines = gridW * (gridH - 1);
        var indices = new int[(hLines + vLines) * 2];
        int k = 0;

        for (int i = 0; i < total; i++)
        {
            // Skip when i+1 crosses a row boundary (wrap-around prevention).
            if ((i + 1) % gridW == 0) continue;
            if (i + 1 >= total) continue;
            indices[k++] = i;
            indices[k++] = i + 1;
        }

        for (int i = 0; i < total; i++)
        {
            // Skip when i+gridW leaves the grid (vertical wrap-around prevention).
            if (i + gridW >= total) continue;
            indices[k++] = i;
            indices[k++] = i + gridW;
        }

        return indices;
    }
}
