namespace LedAgent;

/// <summary>
/// A driver for the RGB LEDs of the machine this agent runs on.
///
/// The agent itself knows nothing about motherboards: it speaks the line protocol and hands
/// colours to whichever backend was selected at startup. Adding support for a new vendor means
/// adding one class here — nothing in the protocol, the server, or ShaderToE131 changes.
/// </summary>
public interface ILedBackend : IDisposable
{
    /// <summary>Short identifier reported in the greeting, STATUS, and logs.</summary>
    string Name { get; }

    /// <summary>
    /// Open the hardware. Returns false and fills <paramref name="error"/> when the backend
    /// cannot be used on this machine (missing vendor SDK, no device, wrong bitness). The agent
    /// still starts in that state: senders get a clear error instead of a crash.
    /// </summary>
    bool TryInitialize(out string? error);

    /// <summary>Number of RGB devices this backend drives (0 when unavailable).</summary>
    int DeviceCount { get; }

    /// <summary>Set every LED to a colour packed as 0x00RRGGBB.</summary>
    void SetColor(int color);

    /// <summary>Turn every LED off.</summary>
    void TurnOff();
}
