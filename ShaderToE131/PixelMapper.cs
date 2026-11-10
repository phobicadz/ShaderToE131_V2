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
    /// Straight raster order (matching ShaderToE131 working reference).
    /// </summary>
    public static int ToLedIndex(int x, int y)
    {
        if (x < 0 || x >= Width || y < 0 || y >= Height) return -1;

        return y * Width + x;
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
        => MapFrame(framebuffer, Width, Height, e131Buffer);

    /// <summary>
    /// Fill the E.1.31 buffer from a framebuffer of an arbitrary size, in the same
    /// straight raster layout. Used by outputs that render natively at their own
    /// resolution (e.g. the second matrix) instead of sampling this matrix.
    /// </summary>
    public static void MapFrame(ReadOnlySpan<byte> framebuffer, int width, int height, Span<byte> e131Buffer)
    {
        // Clear buffer
        e131Buffer.Fill(0);

        // Map only whole RGB triples that fit in both buffers.
        int pixels = Math.Min(width * height, e131Buffer.Length / 3);
        for (int i = 0; i < pixels; i++)
        {
            int srcIdx = i * 4;   // RGBA in framebuffer
            int dstIdx = i * 3;   // RGB in E.1.31 buffer
            e131Buffer[dstIdx]     = framebuffer[srcIdx];     // R
            e131Buffer[dstIdx + 1] = framebuffer[srcIdx + 1]; // G
            e131Buffer[dstIdx + 2] = framebuffer[srcIdx + 2]; // B
        }
    }

    /// <summary>
    /// Fill an E.1.31 string buffer (ledCount LEDs × 3 channels) by nearest-
    /// sampling a single row of the matrix framebuffer.
    /// LED i takes the matrix pixel at x = i * Width / ledCount, so the string
    /// mirrors the row across any LED count (duplicates when ledCount &gt; Width).
    /// </summary>
    public static void MapRowToString(ReadOnlySpan<byte> framebuffer, int row, int ledCount, Span<byte> e131Buffer)
    {
        if (row < 0 || row >= Height) row = Height / 2;

        for (int i = 0; i < ledCount; i++)
        {
            int x = i * Width / ledCount; // always in [0, Width) since i < ledCount
            int srcIdx = (row * Width + x) * 4; // RGBA in framebuffer
            e131Buffer[i * 3]     = framebuffer[srcIdx];     // R
            e131Buffer[i * 3 + 1] = framebuffer[srcIdx + 1]; // G
            e131Buffer[i * 3 + 2] = framebuffer[srcIdx + 2]; // B
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
