using NAudio.Wave;
using Xunit;
using Dream.Client.Audio;

namespace Dream.Tests.BugHunt;

// BH-ANIM-4: a device that failed to open leaked the mixer provider's 1-second diagnostics timer.
//
// NAudioOutput's constructor builds the provider, then calls Init/Play inside a try/catch. The
// catch disposed the device and not the provider, which owns an AudioReadStats - and that owns a
// System.Threading.Timer whenever DREAM_AUDIO_LOG is on. With a device that constructs but will not
// start (driver busy, device removed, exclusive-mode conflict) a thread-pool timer then printed
// every second for the life of the process against a mixer nobody pulls, holding the AudioMixer.
//
// Headless: no sound device is opened. NAudioOutput.StartDevice is the constructor's own failure
// path, driven over a fake IWavePlayer.
public class AnimAudioOpenFailureDisposeTests
{
    private sealed class FailingPlayer(bool failOnInit) : IWavePlayer
    {
        private readonly bool _failOnInit = failOnInit;

        public int Disposals { get; private set; }

        public PlaybackState PlaybackState { get; private set; } = PlaybackState.Stopped;

        public float Volume { get; set; } = 1f;

        public WaveFormat OutputWaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(AudioMixer.SampleRate, 2);

        public event EventHandler<StoppedEventArgs>? PlaybackStopped;

        public void Init(IWaveProvider waveProvider)
        {
            if (_failOnInit)
            {
                throw new InvalidOperationException("device removed between enumeration and Init");
            }
        }

        public void Play()
        {
            if (!_failOnInit)
            {
                throw new InvalidOperationException("driver busy");
            }

            PlaybackState = PlaybackState.Playing;
        }

        public void Pause() => PlaybackState = PlaybackState.Paused;

        public void Stop()
        {
            PlaybackState = PlaybackState.Stopped;
            PlaybackStopped?.Invoke(this, new StoppedEventArgs());
        }

        public void Dispose() => Disposals++;
    }

    private sealed class WorkingPlayer : IWavePlayer
    {
        public int Disposals { get; private set; }

        public PlaybackState PlaybackState { get; private set; } = PlaybackState.Stopped;

        public float Volume { get; set; } = 1f;

        public WaveFormat OutputWaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(AudioMixer.SampleRate, 2);

        public event EventHandler<StoppedEventArgs>? PlaybackStopped;

        public void Init(IWaveProvider waveProvider)
        {
        }

        public void Play() => PlaybackState = PlaybackState.Playing;

        public void Pause() => PlaybackState = PlaybackState.Paused;

        public void Stop()
        {
            PlaybackState = PlaybackState.Stopped;
            PlaybackStopped?.Invoke(this, new StoppedEventArgs());
        }

        public void Dispose() => Disposals++;
    }

    // Stands in for NAudioOutput's private MixerProvider: the same two interfaces, and it records
    // whether the owner released it.
    private sealed class CountingProvider : ISampleProvider, IDisposable
    {
        public int Disposals { get; private set; }

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(AudioMixer.SampleRate, 2);

        public int Read(float[] buffer, int offset, int count) => count;

        public void Dispose() => Disposals++;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ADeviceThatFailsToOpen_DisposesTheProviderAsWellAsTheDevice(bool failOnInit)
    {
        FailingPlayer device = new(failOnInit);
        CountingProvider provider = new();

        Assert.Throws<InvalidOperationException>(() => NAudioOutput.StartDevice(device, provider));

        Assert.Equal(1, device.Disposals);
        Assert.Equal(1, provider.Disposals);
    }

    [Fact]
    public void ADeviceThatOpens_DisposesNeither_SoTheSuccessPathStillOwnsThem()
    {
        WorkingPlayer device = new();
        CountingProvider provider = new();

        NAudioOutput.StartDevice(device, provider);

        Assert.Equal(PlaybackState.Playing, device.PlaybackState);
        Assert.Equal(0, device.Disposals);
        Assert.Equal(0, provider.Disposals);
    }

    // What the provider's disposal actually releases: AudioReadStats owns the 1-second timer
    // (only when DREAM_AUDIO_LOG is on - LogEnabled is read once per process, so this asserts the
    // shape rather than flipping it) and Dispose is what stops it.
    [Fact]
    public void TheProviderOwnsTheDiagnosticsStats_WhoseDisposeIsTheOnlyThingThatStopsItsTimer()
    {
        AudioMixer mixer = new(new AudioVolumes(true, 1f, 1f, 1f, 1f));
        AudioReadStats stats = new(mixer);

        Assert.Equal(AudioDiagnostics.LogEnabled, stats.Active);
        stats.Dispose();
        stats.Dispose();
        Assert.True(typeof(IDisposable).IsAssignableFrom(typeof(AudioReadStats)));
    }
}
