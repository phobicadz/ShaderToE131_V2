namespace ShaderToE131;

/// <summary>
/// Maps shader framebuffer pixels to E.1.31 channel order using serpentine layout.
/// Matrix: 53 wide × 11 high = 583 pixels (1749 RGB channels).
/// </summary>
public static class PixelMapper
{
    public const int Width = 53;
    public const int Height = 11;
    public const int TotalPixels = Width * Height;       // 583
    public const int TotalChannels = TotalPixels * 3;    // 1749

    /// <summary>
    /// Convert a framebuffer (x, y) coordinate to an index in the LED strip.
    /// Even rows: left → right. Odd rows: right → left (serpentine).
    /// </summary>
    public static int ToLedIndex(int x, int y)
    {
        if (x < 0 || x >= Width || y < 0 || y >= Height) return -1;

        int rowOffset = y * Width;
        return rowOffset + (y % 2 == 1 ? (Width - 1 - x) : x);
    }

    /// <summary>
    /// Convert a framebuffer pixel to its RGB channel offsets in the E.1.31 buffer.
    /// Returns (redChannel, greenChannel, blueChannel).
    /// Channel order: RGB per pixel.
    /// </summary>
    public static (int r, int g, int b) ToRgbChannels(int x, int y)
    {
        int idx = ToLedIndex(x, y);
        return (idx * 3, idx * 3 + 1, idx * 3 + 2);
    }

    /// <summary>
    /// Fill the E.1.31 buffer from a shader framebuffer (width×height RGBA array).
    /// </summary>
    public static void MapFrame(ReadOnlySpan<byte> framebuffer, Span<byte> e131Buffer)
    {
        // Clear buffer
        e131Buffer.Fill(0);

        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int srcIdx = (y * Width + x) * 4; // RGBA in framebuffer
                int rCh, gCh, bCh;
                (rCh, gCh, bCh) = ToRgbChannels(x, y);

                e131Buffer[rCh] = framebuffer[srcIdx];     // R
                e131Buffer[gCh] = framebuffer[srcIdx + 1]; // G
                e131Buffer[bCh] = framebuffer[srcIdx + 2]; // B
            }
        }
    }

    /// <summary>
    /// Aspect ratio of the matrix: width / height.
    /// ShaderToys expect u_resolution.x/u_resolution.y ≈ 1:1 (or near-square).
    /// The shader's UV X coordinate needs to be compressed by this factor so it sees a square canvas.
    /// </summary>
    public const float AspectRatio = Width / (float)Height; // ~4.818

    /// <summary>
    /// Given a matrix UV (0-1), compute what ShaderToy UV to use in the shader.
    /// Compress X back to square canvas proportions so the shader renders correctly
    /// when stretched across the wide rectangle.
    /// </summary>
    public static System.Numerics.Vector2 UnWarpUv(System.Numerics.Vector2 matrixUv)
    {
        float shaderX = matrixUv.X / AspectRatio;
        return new System.Numerics.Vector2(shaderX, matrixUv.Y);
    }
}
