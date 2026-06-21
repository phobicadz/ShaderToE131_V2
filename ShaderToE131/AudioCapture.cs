using NAudio.Dsp;
using NAudio.Wave;
using NAudio.CoreAudioApi;
using System.Collections.Concurrent;

namespace ShaderToE131;

/// <summary>
/// Captures microphone audio via NAudio, runs FFT on each buffer chunk,
/// and exposes smoothed spectral band values (0–1) for shader uniforms.
/// </summary>
public sealed class AudioCapture : IDisposable
{
    /// <summary>
    /// Spectral bands exposed to the shader as GLSL uniforms.
    /// All values are in [0, 1] range with simple smoothing.
    /// </summary>
    public struct SpectrumValues
    {
        public float Bass;       // ~20–80 Hz — deep kick/bass
        public float LowMid;     // ~80–300 Hz — bass guitars, lower vocals
        public float Mid;        // ~300–1500 Hz — mids, snare, lead vocals
        public float HighMid;    // ~1500–6000 Hz — upper mids, cymbals attack
        public float Treble;     // ~6000–20000 Hz — air, shimmer
        public float Volume;     // overall RMS level (0–1)
    }

    private const int SampleRate = 44100;
    private const int Channels = 1;       // mono is sufficient for spectral analysis
    private const int BitsPerSample = 16;

    // FFT size: must be power of 2. 2^11 = 2048 → ~21.5 Hz resolution at 44.1 kHz.
    private const int FftSize = 2048;     // 2^11
    private const int FftBits = 11;

    // Frequency band center points (Hz) — tuned for typical music / vocal content
    private static readonly (float low, float high)[] _bands =
    {
        (   20f,    80f),   // Bass
        (   80f,   300f),   // Low Mid
        (  300f,  1500f),   // Mid
        ( 1500f,  6000f),   // High Mid
        ( 6000f, 20000f),   // Treble
    };

    /// <summary>
    /// Source type: microphone input or system audio playback (loopback).
    /// </summary>
    public enum AudioSource { Microphone, Loopback }

    private readonly IWaveIn? _waveIn;                    // mic input (WaveInEvent)
    private readonly NAudio.Wave.WasapiLoopbackCapture? _loopback;  // system playback capture
    private readonly ConcurrentQueue<short[]> _fftBuffers = new();
    private volatile bool _isRunning = false;
    private readonly float[] _smoothedBands = new float[_bands.Length + 1]; // +1 for volume
    // Lower smoothing (0.15) gives faster transient response; higher (0.3+) is smoother but slower to react.
    // With per-band peak normalization, we need snappier response for visual reactivity.
    private const float SmoothingFactor = 0.15f;

    // Auto-calibration: exponential moving peak per band.
    // Peaks decay slowly (decay=0.97) so the normalization adapts to current signal level
    // rather than being pinned by early transients or initial frames.
    private const float PeakDecay = 0.97f;
    private static readonly float[] _peakValues = new float[6];

    /// <summary>
    /// Initialize audio capture with the given source type and optional device selection.
    /// For Microphone: loopbackDevice is ignored, use deviceIndex to pick a mic.
    /// For Loopback: deviceIndex selects which render (speaker/headphone) endpoint to capture.
    /// Returns null if no suitable device is available.
    /// </summary>
    public static AudioCapture? Create(AudioSource source = AudioSource.Microphone, int deviceIndex = 0)
    {
        switch (source)
        {
            case AudioSource.Microphone:
                if (NAudio.Wave.WaveInEvent.DeviceCount == 0) return null;
                return new AudioCapture(source, micDeviceIndex: deviceIndex);

            case AudioSource.Loopback:
                // Enumerate render endpoints; try the requested index, fall back to default.
                var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
                var renderDevices = enumerator.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.DeviceState.Active).ToList();
                if (renderDevices.Count == 0) return null;

                int idx = Math.Max(0, Math.Min(deviceIndex, renderDevices.Count - 1));
                return new AudioCapture(source, loopbackDevice: renderDevices[idx]);

            default:
                return null;
        }
    }

    /// <summary>
    /// List all available microphone input devices.
    /// </summary>
    public static IEnumerable<(int index, string name)> ListMicrophones()
    {
        for (int i = 0; i < NAudio.Wave.WaveInEvent.DeviceCount; i++)
        {
            var caps = NAudio.Wave.WaveInEvent.GetCapabilities(i);
            yield return (i, caps.ProductName);
        }
    }

    /// <summary>
    /// List all available render (speaker/headphone) endpoints for loopback capture.
    /// </summary>
    public static IEnumerable<(int index, string name)> ListLoopbackDevices()
    {
        var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
        int i = 0;
        foreach (var device in enumerator.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.DeviceState.Active))
            yield return (i++, device.FriendlyName);
    }

    /// <summary>
    /// List all available audio devices (microphones + render endpoints).
    /// </summary>
    public static void ListAllDevices()
    {
        Console.WriteLine("Microphone input devices:");
        foreach (var (index, name) in ListMicrophones())
            Console.WriteLine($"  [mic {index}] {name}");

        Console.WriteLine();
        Console.WriteLine("Render endpoints (loopback / playback capture):");
        foreach (var (index, name) in ListLoopbackDevices())
            Console.WriteLine($"  [loopback {index}] {name}");
    }

    private AudioCapture(AudioSource source, int micDeviceIndex = 0)
    {
        switch (source)
        {
            case AudioSource.Microphone:
                _waveIn = new NAudio.Wave.WaveInEvent
                {
                    DeviceNumber = micDeviceIndex,
                    WaveFormat = new NAudio.Wave.WaveFormat(SampleRate, Channels, BitsPerSample),
                    BufferMilliseconds = 50   // ~2205 samples per callback
                };
                _waveIn.DataAvailable += OnDataAvailable;
                break;

            case AudioSource.Loopback:
                _loopback = new WasapiLoopbackCapture();
                _loopback.WaveFormat = new NAudio.Wave.WaveFormat(SampleRate, Channels);
                _loopback.DataAvailable += OnDataAvailable;
                break;
        }
    }

    private AudioCapture(AudioSource source, NAudio.CoreAudioApi.MMDevice loopbackDevice)
    {
        // Loopback capture targeting a specific render endpoint.
        _loopback = new WasapiLoopbackCapture(loopbackDevice);
        _loopback.WaveFormat = new NAudio.Wave.WaveFormat(SampleRate, Channels);
        _loopback.DataAvailable += OnDataAvailable;
    }

    /// <summary>
    /// Start recording. Must be called before ReadSpectrum() returns meaningful data.
    /// </summary>
    public void Start()
    {
        try
        {
            _isRunning = true;
            if (_waveIn != null) _waveIn.StartRecording();
            else if (_loopback != null) _loopback.StartRecording();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Audio] Failed to start recording: {ex.Message}");
            _isRunning = false;
        }
    }

    /// <summary>
    /// Stop recording and release the capture device.
    /// </summary>
    public void Stop()
    {
        try
        {
            if (_waveIn != null) _waveIn.StopRecording();
            else if (_loopback != null) _loopback.StopRecording();
            _isRunning = false;
        }
        catch { /* already stopped */ }
    }

    private int _dataAvailableCount;
    private static readonly int DebugEventLimit = 5;

    private void OnDataAvailable(object? sender, NAudio.Wave.WaveInEventArgs e)
    {
        if (!_isRunning || e.BytesRecorded == 0) return;

        _dataAvailableCount++;
        int samplesCount = e.BytesRecorded / 2; // 16-bit = 2 bytes per sample

        // Convert byte[] → short[] (little-endian PCM16)
        var pcm = new short[samplesCount];
        float maxAbs = 0f;
        for (int i = 0; i < samplesCount; i++)
        {
            pcm[i] = BitConverter.ToInt16(e.Buffer, i * 2);
            float absVal = Math.Abs(pcm[i]) / 32768f;
            if (absVal > maxAbs) maxAbs = absVal;
        }

        _fftBuffers.Enqueue(pcm);
        // Log first few events to confirm audio is actually arriving with real signal
        if (_dataAvailableCount <= DebugEventLimit)
            Console.WriteLine($"[Audio] DataAvailable #{_dataAvailableCount}: {samplesCount} samples, maxAbs={maxAbs:F4}");
    }



    /// <summary>
    /// Read the current smoothed spectrum values. Returns zeroed SpectrumValues if no audio data available.
    /// This is thread-safe and can be called from the render loop every frame.
    /// </summary>
    public SpectrumValues ReadSpectrum()
    {
        float[] rawBands = new float[_bands.Length + 1]; // +1 for volume

        // Drain all available buffers and combine FFT results
        int processedCount = 0;
        while (_fftBuffers.TryDequeue(out var pcm))
        {
            if (pcm == null || pcm.Length == 0) continue;

            // Use as much data as we need for a full FFT, or the whole buffer
            int fftLen = Math.Min(FftSize, pcm.Length);
            float[] magnitudes = ComputeFftMagnitudes(pcm, fftLen);

            if (magnitudes != null && magnitudes.Length > 0)
            {
                AccumulateBands(magnitudes, SampleRate, ref rawBands);
                processedCount++;
            }
        }

        // If no data was available this frame, return previous smoothed values
        if (processedCount == 0)
            return new SpectrumValues
            {
                Bass = _smoothedBands[0],
                LowMid = _smoothedBands[1],
                Mid = _smoothedBands[2],
                HighMid = _smoothedBands[3],
                Treble = _smoothedBands[4],
                Volume = _smoothedBands[5]
            };



        // Normalize each band using an exponential-moving-peak detector.
        // This auto-calibrates to whatever signal level the device produces, whether quiet or loud.
        // Peaks decay gradually (0.97 per frame) so normalization adapts to current levels
        // rather than being pinned by early transients.
        for (int i = 0; i < rawBands.Length; i++)
        {
            // Update peak: rise fast, decay slow
            if (rawBands[i] > _peakValues[i])
                _peakValues[i] = rawBands[i];
            else
                _peakValues[i] *= PeakDecay;

            // Normalize against current peak estimate
            float peak = Math.Max(_peakValues[i], 1e-6f);
            rawBands[i] /= peak;
            rawBands[i] = Math.Max(0f, Math.Min(1f, rawBands[i]));
        }

        // Apply exponential smoothing to avoid flickering
        for (int i = 0; i < _smoothedBands.Length; i++)
        {
            _smoothedBands[i] = _smoothedBands[i] * (1f - SmoothingFactor) + rawBands[i] * SmoothingFactor;
            // Clamp to [0, 1]
            _smoothedBands[i] = Math.Max(0f, Math.Min(1f, _smoothedBands[i]));
        }

        return new SpectrumValues
        {
            Bass = _smoothedBands[0],
            LowMid = _smoothedBands[1],
            Mid = _smoothedBands[2],
            HighMid = _smoothedBands[3],
            Treble = _smoothedBands[4],
            Volume = _smoothedBands[5]
        };
    }

    /// <summary>
    /// Apply a Hamming window and compute FFT, returning magnitude values for the first half of bins.
    /// </summary>
    private static float[]? ComputeFftMagnitudes(short[] pcm, int fftLen)
    {
        if (fftLen == 0 || fftLen > FftSize) return null;

        // Round down to nearest power of 2 for NAudio FFT requirement
        int pow2 = 1;
        while (pow2 * 2 <= fftLen) pow2 *= 2;
        if (pow2 < 64) return null; // minimum useful FFT size is 64

        // NAudio's FFT requires a power-of-2 size and uses Complex structs
        var fftComplex = new Complex[pow2];

        // Apply Hamming window and load samples (zero-pad remainder)
        for (int i = 0; i < pow2; i++)
        {
            float sample = i < fftLen ? pcm[i] : 0f;
            float windowed = sample * (float)FastFourierTransform.HammingWindow(i, pow2);
            fftComplex[i].X = windowed / 32768f; // Normalize to [-1, 1]
            fftComplex[i].Y = 0f;
        }

        // Perform FFT (true = forward transform)
        // m is log2 of the FFT size — derive it from pow2 dynamically
        FastFourierTransform.FFT(true, (int)Math.Log(pow2, 2.0), fftComplex);

        // Compute magnitudes for first half of bins (Nyquist limit)
        int numBins = pow2 / 2;
        var magnitudes = new float[numBins];
        float maxMag = 0f;
        for (int i = 0; i < numBins; i++)
        {
            // |X| = sqrt(Re² + Im²)
            magnitudes[i] = (float)Math.Sqrt(fftComplex[i].X * fftComplex[i].X + fftComplex[i].Y * fftComplex[i].Y);
            if (magnitudes[i] > maxMag) maxMag = magnitudes[i];
        }

        // No per-frame logging in static method; caller handles it

        return magnitudes;
    }

    /// <summary>
    /// Map FFT bin magnitudes to spectral bands based on frequency resolution.
    /// Each magnitude bin i corresponds to freq = i * sampleRate / (2 * numBins).
    /// </summary>
    private static void AccumulateBands(float[] magnitudes, int sampleRate, ref float[] bandAccum)
    {
        // magnitudes has pow2/2 entries; each bin spans sampleRate/pow2 Hz
        double freqPerBin = (double)sampleRate / (magnitudes.Length * 2);

        for (int i = 0; i < _bands.Length; i++)
        {
            int lowBin = Math.Max(1, (int)(_bands[i].low / freqPerBin)); // skip DC bin 0
            int highBin = Math.Min((int)(_bands[i].high / freqPerBin), magnitudes.Length - 1);

            // Sum magnitudes in this band
            float sum = 0f;
            for (int b = lowBin; b <= highBin; b++)
                sum += magnitudes[b];

            // Average across the band
            int count = highBin - lowBin + 1;
            if (count > 0)
                bandAccum[i] += sum / count;
        }

        // Overall RMS volume from all bins
        float rmsSum = 0f;
        for (int i = 0; i < magnitudes.Length; i++)
            rmsSum += magnitudes[i] * magnitudes[i];
        bandAccum[_bands.Length] = (float)Math.Sqrt(rmsSum / magnitudes.Length);

        // No per-frame logging in static method; caller handles it
    }

    public void Dispose()
    {
        Stop();
        (_waveIn as IDisposable)?.Dispose();
        (_loopback as IDisposable)?.Dispose();
    }
}
