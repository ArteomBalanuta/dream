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
        _device = new WaveOutEvent { DesiredLatency = 100, NumberOfBuffers = 3 };
        _provider = new MixerProvider(mixer);
        try
        {
            _device.Init(_provider);
            _device.Play();
        }
        catch
        {
            _device.Dispose();
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

    private sealed class MixerProvider(AudioMixer mixer) : ISampleProvider, IDisposable
    {
        private readonly AudioMixer _mixer = mixer;
        private readonly AudioReadStats _stats = new(mixer);
        private readonly AudioDumpWriter? _dump = AudioDiagnostics.DumpPath is { } path ? new AudioDumpWriter(path) : null;

        public void Dispose()
        {
            _stats.Dispose();
            _dump?.Dispose();
        }

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(AudioMixer.SampleRate, 2);

        public int Read(float[] buffer, int offset, int count)
        {
            _stats.Begin(count / 2);
            long began = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                int read = _mixer.Read(buffer, offset, count);
                _dump?.Add(buffer, offset, read);
                return read;
            }
            catch (Exception)
            {
                // Audio must never take the game down: a failed block is a block of silence.
                Array.Clear(buffer, offset, count);
                return count;
            }
            finally
            {
                _stats.End(began);
            }
        }
    }
}
