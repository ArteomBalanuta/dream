using Xunit;
using Xunit.Abstractions;
using Dream.Client.Audio;

namespace Dream.Tests.Client.Audio;

// The shipped clips through the real mixer, offline: the level of a held-up bed must be steady.
public class AudioRealMixTests(ITestOutputHelper output)
{
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

    [Fact]
    public void World_Music_And_Wind_Hold_A_Steady_Level()
    {
        AudioClipBank bank = AudioClipBank.Load(AudioDir());
        AudioVolumes volumes = new(true, 1f, 1f, 1f, 1f);
        AudioMixer mixer = new(volumes);
        foreach (AudioLoopId id in Enum.GetValues<AudioLoopId>())
        {
            Assert.True(bank.TryGetLoop(id, out WavClip clip));
            mixer.AttachLoop(id, clip);
        }

        mixer.SetLoopTarget(AudioLoopId.MusicWorld, 1f, 0.01f);
        mixer.SetLoopTarget(AudioLoopId.Wind, 0.6f, 0.01f);
        float[] block = new float[735 * 2];
        List<double> rms = [];
        double sum = 0;
        long n = 0;
        for (int b = 0; b < 44100 * 30 / 735; b++)
        {
            mixer.Read(block, 0, block.Length);
            for (int i = 0; i < block.Length; i += 2)
            {
                sum += block[i] * block[i];
                n++;
            }

            if (n >= 44100)
            {
                rms.Add(Math.Sqrt(sum / n));
                sum = 0;
                n = 0;
            }
        }

        output.WriteLine(string.Join(" ", rms.Select(r => r.ToString("F3"))));
        Assert.True(rms.Max() < rms.Min() * 2.5, "level wanders: " + string.Join(" ", rms));
    }
}
