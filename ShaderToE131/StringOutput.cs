namespace ShaderToE131;

/// <summary>
/// sACN constraints and validation for the optional LED string output.
/// </summary>
internal static class StringOutput
{
    /// <summary>sACN carries 510 DMX slots per universe.</summary>
    public const int ChannelsPerUniverse = 510;

    /// <summary>Lowest valid sACN universe number.</summary>
    public const int MinUniverse = 1;

    /// <summary>Highest valid sACN universe number (universes are written as ushort).</summary>
    public const int MaxUniverse = 63999;

    /// <summary>
    /// Number of sACN universes required to carry <paramref name="ledCount"/> RGB LEDs
    /// (ledCount × 3 channels, 510 per universe, ceiling). Uses long arithmetic so an
    /// extremely large count cannot overflow int before validation.
    /// </summary>
    public static long UniversesRequired(int ledCount)
    {
        long channels = (long)ledCount * 3;
        return (channels + ChannelsPerUniverse - 1) / ChannelsPerUniverse;
    }

    /// <summary>
    /// Validates LED string settings against the sACN universe contract (universes
    /// 1..63999). Bounds the requested size by the channel capacity available from
    /// <paramref name="baseUniverse"/> so the string buffer can neither overflow the
    /// universe range nor grow without limit.
    /// Returns a human-readable error message, or null if the settings are valid.
    /// </summary>
    public static string? Validate(int ledCount, int baseUniverse)
    {
        if (ledCount < 1)
            return $"--string-size must be >= 1 (got {ledCount}).";

        long universes = UniversesRequired(ledCount);
        long lastUniverse = (long)baseUniverse + universes - 1;
        if (baseUniverse < MinUniverse || baseUniverse > MaxUniverse)
            return $"--string-universe must be {MinUniverse}..{MaxUniverse} (got {baseUniverse}).";
        if (lastUniverse > MaxUniverse)
            return $"--string-size {ledCount} needs {universes} universes starting at {baseUniverse} (last universe {lastUniverse}), but universes are limited to {MinUniverse}..{MaxUniverse}; use a smaller --string-size or a lower --string-universe.";
        return null;
    }
}
