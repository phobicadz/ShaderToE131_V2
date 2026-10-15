using System.Runtime.InteropServices;

namespace LedAgent.Backends;

/// <summary>
/// Drives the motherboard LEDs of an MSI board through MSI's own Mystic Light SDK
/// (MSLight.dll), which ships with MSI Center / Dragon Center.
///
/// Two hard constraints of that SDK, both handled here with a clear message rather than a
/// crash:
///   * MSLight.dll is 32-bit, so this backend only loads in an x86 process
///     (build/publish LedAgent with <c>-a x86</c>).
///   * MSI Center (or at least its Mystic Light SDK component) must be installed.
///
/// The exports are resolved by name at runtime instead of being declared with DllImport, so a
/// missing or renamed function produces "MSLight.dll does not export X" rather than a
/// EntryPointNotFoundException on first use.
/// </summary>
public sealed class MsiMysticLightBackend : ILedBackend
{
    // MSI Mystic Light SDK constants (Mystic Light SDK user guide).
    private const int DeviceTypeMotherboard = 0;
    private const int EffectStatic = 0x00;
    private const int SpeedNormal = 0x01;

    private readonly string? _explicitDllPath;

    private IntPtr _module;
    private InitFn? _init;
    private DeinitFn? _deinit;
    private GetLedCountFn? _getLedCount;
    private SetLedColorFn? _setLedColor;
    private SetFullEffectsFn? _setFullEffects;
    private int _ledCount;
    private bool _perLedMode;

    public string Name => "msi-mystic-light";
    public int DeviceCount { get; private set; }

    public MsiMysticLightBackend(string? dllPath = null) => _explicitDllPath = dllPath;

    // ─── SDK signatures ───

    [StructLayout(LayoutKind.Sequential)]
    private struct LedColor
    {
        public int R;
        public int G;
        public int B;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int InitFn(int deviceType);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DeinitFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetLedCountFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetLedColorFn(int ledIndex, LedColor color);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetFullEffectsFn(int effect, int speed, LedColor color1, LedColor color2);

    public bool TryInitialize(out string? error)
    {
        error = null;

        if (IntPtr.Size == 8)
        {
            error = "MSLight.dll is 32-bit but this agent is 64-bit. Publish LedAgent with '-a x86' " +
                    "to drive MSI motherboard LEDs, or choose another --backend.";
            return false;
        }

        string? dllPath = _explicitDllPath ?? FindMsiLightDll();
        if (dllPath == null || !File.Exists(dllPath))
        {
            error = "MSLight.dll was not found. Install MSI Center (Mystic Light SDK), or point " +
                    "--msi-dll at it. Other --backend values: mock.";
            return false;
        }

        try
        {
            _module = NativeLibrary.Load(dllPath);
        }
        catch (Exception ex)
        {
            error = $"Failed to load {dllPath} ({ex.GetType().Name}: {ex.Message}). " +
                    "A 32-bit agent process is required.";
            return false;
        }

        Console.WriteLine($"[backend] msi: loaded {dllPath}");

        if (!TryResolve("MSLight_Init", out _init) ||
            !TryResolve("MSLight_GetLEDCount", out _getLedCount))
        {
            error = "MSLight.dll does not export MSLight_Init/MSLight_GetLEDCount — this is not " +
                    "the Mystic Light SDK, or it is a different version.";
            return false;
        }

        TryResolve("MSLight_Deinit", out _deinit);
        TryResolve("MSLight_SetLEDColor", out _setLedColor);
        TryResolve("MSLight_SetFullEffects", out _setFullEffects);

        int initResult;
        try
        {
            initResult = _init!(DeviceTypeMotherboard);
        }
        catch (Exception ex)
        {
            error = $"MSLight_Init failed ({ex.GetType().Name}: {ex.Message}). Is MSI Center running?";
            return false;
        }

        if (initResult != 0)
        {
            error = $"MSLight_Init returned {initResult} — no Mystic Light device found " +
                    "(check the board's RGB header is enabled in BIOS).";
            return false;
        }

        try
        {
            _ledCount = Math.Clamp(_getLedCount!(), 0, 64);
        }
        catch
        {
            _ledCount = 0;
        }

        // Per-LED control is preferred (it is what a static colour needs); if the SDK build
        // does not expose it, fall back to the whole-device static effect.
        _perLedMode = _setLedColor != null && _ledCount > 0;
        if (!_perLedMode && _setFullEffects == null)
        {
            error = "MSLight.dll exposes neither MSLight_SetLEDColor nor MSLight_SetFullEffects.";
            return false;
        }

        DeviceCount = 1;
        Console.WriteLine($"[backend] msi: Mystic Light ready ({_ledCount} LEDs, " +
            $"{(_perLedMode ? "per-LED" : "device-wide static")} mode).");
        return true;
    }

    private bool TryResolve<TDelegate>(string exportName, out TDelegate? fn) where TDelegate : Delegate
    {
        if (NativeLibrary.TryGetExport(_module, exportName, out IntPtr addr))
        {
            fn = Marshal.GetDelegateForFunctionPointer<TDelegate>(addr);
            return true;
        }
        fn = null;
        return false;
    }

    public void SetColor(int color)
    {
        Protocol.Unpack(color, out byte r, out byte g, out byte b);
        LedColor c = new() { R = r, G = g, B = b };

        if (_perLedMode)
        {
            for (int i = 0; i < _ledCount; i++)
            {
                int result = _setLedColor!(i, c);
                if (result != 0)
                    throw new InvalidOperationException($"MSLight_SetLEDColor({i}) returned {result}.");
            }
            return;
        }

        int full = _setFullEffects!(EffectStatic, SpeedNormal, c, c);
        if (full != 0)
            throw new InvalidOperationException($"MSLight_SetFullEffects returned {full}.");
    }

    public void TurnOff() => SetColor(0);

    public void Dispose()
    {
        try { _deinit?.Invoke(); } catch { }
        if (_module != IntPtr.Zero)
        {
            try { NativeLibrary.Free(_module); } catch { }
            _module = IntPtr.Zero;
        }
    }

    /// <summary>
    /// MSI installs the SDK in a few different places depending on which utility was used.
    /// Probing beats asking the user to hunt for a path.
    /// </summary>
    private static string? FindMsiLightDll()
    {
        string[] candidates =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                @"MSI\MSI Center\MSLight.dll"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                @"MSI\MSI Center\Resources\SDK\MSLight.dll"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                @"MSI\Dragon Center\MSLight.dll"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "MSLight.dll"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "MSLight.dll"),
            Path.Combine(AppContext.BaseDirectory, "MSLight.dll"),
        };

        foreach (string candidate in candidates)
            if (File.Exists(candidate)) return candidate;

        // Last resort: a shallow search under the MSI program folder.
        string msiRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "MSI");
        if (!Directory.Exists(msiRoot)) return null;
        try
        {
            foreach (string found in Directory.EnumerateFiles(msiRoot, "MSLight.dll", SearchOption.AllDirectories))
                return found;
        }
        catch (UnauthorizedAccessException) { }
        return null;
    }
}
