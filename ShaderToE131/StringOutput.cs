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

    /// <summary>
    /// Build the sACN sender for a string target, or report why it cannot be built.
    ///
    /// A string target is normally an mDNS name, and an mDNS name disappears with the
    /// device it describes. That is a disabled output with a reason attached to it, not
    /// a reason the program cannot run: the LED matrix is a separate device reached by
    /// a literal address, and it stays lit. The alternative is an unhandled exception
    /// out of Main, which is what took the matrix down with an absent string
    /// controller and left launchd restarting a process that died again ten seconds
    /// later, 2000-odd times.
    /// </summary>
    public static bool TryCreateSender(string target, int port, out E131Sender? sender, out string? failure)
    {
        try
        {
            sender = new E131Sender(target, port);
            failure = null;
            return true;
        }
        catch (Exception ex)
        {
            sender = null;
            failure = ex.Message;
            return false;
        }
    }
}
