namespace LedAgent;

/// <summary>
/// Backend that drives nothing: it accepts colours, counts them, and logs them.
///
/// Useful for bringing the agent up on a machine with no supported LED hardware, for
/// checking that the network path from ShaderToE131 works, and as the template a real
/// vendor backend follows.
/// </summary>
public sealed class MockLedBackend : ILedBackend
{
    private long _lastLogMs;
    private int _lastLoggedColor = -1;

    public string Name => "mock";
    public int DeviceCount => 1;

    /// <summary>Colours accepted, for the STATUS line and the tests.</summary>
    public int LastColor { get; private set; } = -1;
    public int ColorUpdates { get; private set; }

    public bool TryInitialize(out string? error)
    {
        error = null;
        Console.WriteLine("[backend] mock: no hardware driven, colours are logged only.");
        return true;
    }

    public void SetColor(int color)
    {
        ColorUpdates++;
        LastColor = color;

        long now = Environment.TickCount64;
        if (color != _lastLoggedColor || now - _lastLogMs > 10_000)
        {
            _lastLoggedColor = color;
            _lastLogMs = now;
            Protocol.Unpack(color, out byte r, out byte g, out byte b);
            Console.WriteLine($"[backend] mock: rgb({r}, {g}, {b})");
        }
    }

    public void TurnOff()
    {
        ColorUpdates++;
        LastColor = 0;
        Console.WriteLine("[backend] mock: off");
    }

    public void Dispose() { }
}
