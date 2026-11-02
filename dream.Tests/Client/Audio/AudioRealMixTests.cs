using NAudio.Wave;
using Xunit;
using Dream.Client.Audio;

namespace Dream.Tests.Client.Audio;

// The shipped clips through the real mixer and NAudio's own byte-buffer adapter, offline (no device).
// Regression for "sound is repeating and stuck": WaveOutEvent reads through SampleToWaveProvider, whose
// float[] aliases a byte[]; clearing it with Array.Clear cleared a quarter of the block, so each pass
// added onto the last and the level climbed to the limiter.
public class AudioRealMixTests
{
    private const int DeviceBlockBytes = 17640; // WaveOutEvent: 200 ms / 4 buffers at 44.1 kHz stereo float

    private static string AudioDir()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "dream")) &&
                Directory.Exists(Path.Combine(current.FullName, "dream.Tests")))
            {
                return Path.Combine(current.FullName, "dream", "audio");
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("repo root not found");
    }

    private static List<double> SecondRms(IWaveProvider wave, int seconds)
    {
        byte[] bytes = new byte[DeviceBlockBytes];
        float[] floats = new float[DeviceBlockBytes / 4];
        List<double> rms = [];
        double sum = 0;
        long n = 0;
        while (rms.Count < seconds)
        {
            int got = wave.Read(bytes, 0, bytes.Length);
            Buffer.BlockCopy(bytes, 0, floats, 0, got);
            for (int i = 0; i < got / 4; i += 2)
            {
                sum += floats[i] * floats[i];
                n++;
            }

            if (n >= AudioMixer.SampleRate)
            {
                rms.Add(Math.Sqrt(sum / n));
                sum = 0;
                n = 0;
            }
        }

        return rms;
    }

    [Fact]
    public void Device_Path_Holds_A_Steady_Level_For_The_World_Music()
    {
        AudioClipBank bank = AudioClipBank.Load(AudioDir());
        Assert.True(bank.TryGetLoop(AudioLoopId.MusicWorld, out WavClip clip));
        AudioMixer mixer = new(new AudioVolumes(true, 0.1f, 0.8f, 0.5f, 0.7f));
        mixer.AttachLoop(AudioLoopId.MusicWorld, clip);
        mixer.SetLoopTarget(AudioLoopId.MusicWorld, 1f, 2.5f);
        IWaveProvider wave = NAudioOutput.CreateProvider(mixer).ToWaveProvider();

        List<double> rms = SecondRms(wave, 20);

        // The clip alone at master 0.1 x music 0.5 sits near 240/32768; the broken path reached 4x that.
        double ceiling = 400.0 / 32768.0;
        for (int i = 4; i < rms.Count; i++)
        {
            Assert.True(rms[i] < ceiling, $"second {i} level {rms[i] * 32768:F0} wanders above {ceiling * 32768:F0}");
        }
    }

    [Fact]
    public void Device_Path_Output_Equals_Direct_Mixer_Output()
    {
        AudioClipBank bank = AudioClipBank.Load(AudioDir());
        Assert.True(bank.TryGetLoop(AudioLoopId.MusicWorld, out WavClip clip));
        AudioVolumes volumes = new(true, 0.5f, 0.8f, 0.5f, 0.7f);
        AudioMixer direct = new(volumes);
        AudioMixer viaDevice = new(volumes);
        foreach (AudioMixer mixer in new[] { direct, viaDevice })
        {
            mixer.AttachLoop(AudioLoopId.MusicWorld, clip);
            mixer.SetLoopTarget(AudioLoopId.MusicWorld, 1f, 0.05f);
        }

        IWaveProvider wave = NAudioOutput.CreateProvider(viaDevice).ToWaveProvider();
        byte[] bytes = new byte[DeviceBlockBytes];
        float[] got = new float[DeviceBlockBytes / 4];
        float[] expected = new float[DeviceBlockBytes / 4];
        for (int block = 0; block < 60; block++)
        {
            int read = wave.Read(bytes, 0, bytes.Length);
            Buffer.BlockCopy(bytes, 0, got, 0, read);
            direct.Read(expected, 0, read / 4);
            for (int i = 0; i < read / 4; i++)
            {
                Assert.Equal(expected[i], got[i]);
            }
        }
    }
}
