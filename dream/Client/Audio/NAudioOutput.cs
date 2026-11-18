using NAudio.Wave;

namespace Dream.Client.Audio;

// The sound device. The only file that touches NAudio (WinMM waveOut): a WaveOutEvent pulls the
// mixer on its own thread. Opening can throw (no device, driver busy); the caller falls back to silence.
public interface IAudioOutput : IDisposable
{
}

public sealed class NAudioOutput : IAudioOutput
{
    private readonly WaveOutEvent _device;
    private readonly MixerProvider _provider;

    public NAudioOutput(AudioMixer mixer)
    {
        ArgumentNullException.ThrowIfNull(mixer);
        _device = new WaveOutEvent { DesiredLatency = 200, NumberOfBuffers = 4 };
        AudioDiagnostics.Log($"device opened (latency={_device.DesiredLatency} buffers={_device.NumberOfBuffers})");
        _provider = new MixerProvider(mixer);
        StartDevice(_device, _provider);
    }

    // Starts the device on the provider and, when opening fails, releases BOTH of the half-built
    // output's disposables before rethrowing. The device alone is not enough: the provider owns an
    // AudioReadStats, which under DREAM_AUDIO_LOG owns a 1-second Timer that would otherwise print
    // against a mixer nobody pulls for the life of the process, and hold that mixer with it.
    // GameAudio.EnsureStarted only ever sees the exception, so nothing else can reach either one.
    //
    // Internal and parameterised so the failure path can be exercised without a sound device
    // (dream.Tests/BugHunt/AnimAudioOpenFailureDisposeTests.cs).
    internal static void StartDevice<TProvider>(IWavePlayer device, TProvider provider)
        where TProvider : ISampleProvider, IDisposable
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(provider);
        try
        {
            device.Init(provider);
            device.Play();
        }
        catch
        {
            device.Dispose();
            provider.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        try
        {
            _device.Stop();
        }
        catch (Exception)
        {
            // A device that is already gone has nothing left to stop.
        }

        _device.Dispose();
        _provider.Dispose();
    }

    // The provider the device pulls. Public for tests, which drive it through NAudio's own
    // SampleToWaveProvider exactly as the device does.
    public static ISampleProvider CreateProvider(AudioMixer mixer) => new MixerProvider(mixer);

    // NAudio's SampleToWaveProvider hands over a float[] that aliases a byte[] (WaveBuffer). Array.Clear
    // on such an array clears `count` bytes, not floats, so a mixer that clears the buffer it is given
    // leaves three quarters of it holding the previous block, and every pass adds to it: an echo that
    // builds until the soft limiter pins it ("repeating and stuck"). The mixer therefore works in its
    // own scratch array and the result is copied out with a Span, which honours the element size.
    private sealed class MixerProvider(AudioMixer mixer) : ISampleProvider, IDisposable
    {
        private readonly AudioMixer _mixer = mixer;
        private readonly AudioReadStats _stats = new(mixer);
        private float[] _scratch = new float[8192];

        public void Dispose()
        {
            _stats.Dispose();
        }

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(AudioMixer.SampleRate, 2);

        public int Read(float[] buffer, int offset, int count)
        {
            _stats.Begin(count / 2);
            long began = System.Diagnostics.Stopwatch.GetTimestamp();
            Span<float> destination = new(buffer, offset, count);
            try
            {
                if (_scratch.Length < count)
                {
                    _scratch = new float[count];
                }

                int read = _mixer.Read(_scratch, 0, count);
                _stats.Level(_scratch, 0, read);
                _scratch.AsSpan(0, read).CopyTo(destination);
                return read;
            }
            catch (Exception)
            {
                // Audio must never take the game down: a failed block is a block of silence.
                destination.Clear();
                return count;
            }
            finally
            {
                _stats.End(began);
            }
        }
    }
}
