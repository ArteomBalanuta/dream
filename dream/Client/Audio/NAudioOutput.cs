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

    public NAudioOutput(AudioMixer mixer)
    {
        ArgumentNullException.ThrowIfNull(mixer);
        _device = new WaveOutEvent { DesiredLatency = 100, NumberOfBuffers = 3 };
        try
        {
            _device.Init(new MixerProvider(mixer));
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
    }

    private sealed class MixerProvider(AudioMixer mixer) : ISampleProvider
    {
        private readonly AudioMixer _mixer = mixer;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(AudioMixer.SampleRate, 2);

        public int Read(float[] buffer, int offset, int count)
        {
            try
            {
                return _mixer.Read(buffer, offset, count);
            }
            catch (Exception)
            {
                // Audio must never take the game down: a failed block is a block of silence.
                Array.Clear(buffer, offset, count);
                return count;
            }
        }
    }
}
