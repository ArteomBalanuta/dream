using Xunit;
using Dream.Client.Audio;

namespace Dream.Tests.Client.Audio;

// The generated sounds in dream/audio: every id has a clip, the format is what the mixer expects,
// nothing clips, loops close on themselves, and the folder fits its budget. Regenerate with
// tools/audio/build_all.py.
public class AudioAssetsTests
{
    private const long BudgetBytes = 12L * 1024 * 1024;

    private static string RepoRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "dream")) &&
                Directory.Exists(Path.Combine(current.FullName, "dream.Tests")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("repo root not found");
    }

    private static string AudioDir() => Path.Combine(RepoRoot(), "dream", "audio");

    [Fact]
    public void Generator_Scripts_Exist()
    {
        foreach (string name in new[] { "gen_sfx.py", "sfx_combat.py", "sfx_wolf.py", "gen_music.py", "gen_ambience.py", "build_all.py", "synth.py", "README.md" })
        {
            Assert.True(File.Exists(Path.Combine(RepoRoot(), "tools", "audio", name)), name);
        }
    }

    [Fact]
    public void Bank_Loads_A_Clip_For_Every_Sound_And_All_Four_Loops()
    {
        List<string> problems = [];
        AudioClipBank bank = AudioClipBank.Load(AudioDir(), problems.Add);

        Assert.Empty(problems);
        Random random = new(5);
        foreach (SoundId id in Enum.GetValues<SoundId>())
        {
            Assert.True(bank.TryGet(id, random, out WavClip clip), $"missing sound {id}");
            Assert.Equal(AudioMixer.SampleRate, clip.SampleRate);
            Assert.Equal(1, clip.Channels);
            Assert.InRange(clip.DurationSeconds, 0.04, 6.0);
        }

        foreach (AudioLoopId id in Enum.GetValues<AudioLoopId>())
        {
            Assert.True(bank.TryGetLoop(id, out WavClip loop), $"missing loop {id}");
            Assert.Equal(22050, loop.SampleRate);
            Assert.Equal(id is AudioLoopId.MusicMenu or AudioLoopId.MusicWorld ? 2 : 1, loop.Channels);
            Assert.InRange(loop.DurationSeconds, 4.0, 40.0);
        }
    }

    [Fact]
    public void No_Clip_Clips_And_One_Shots_Start_And_End_Quiet()
    {
        foreach (string path in Directory.EnumerateFiles(AudioDir(), "*.wav", SearchOption.AllDirectories))
        {
            WavClip clip = WavClip.Load(path);
            float peak = clip.Samples.Max(MathF.Abs);
            Assert.True(peak <= 0.97f, $"{path} peak {peak}");
            Assert.True(peak >= 0.2f, $"{path} is nearly silent");
            if (Path.GetFileName(Path.GetDirectoryName(path)) == "sfx")
            {
                Assert.True(MathF.Abs(clip.Samples[0]) < 0.02f, $"{path} starts with a click");
                Assert.True(MathF.Abs(clip.Samples[^1]) < 0.02f, $"{path} ends with a click");
            }
        }
    }

    [Fact]
    public void Loops_Close_Without_A_Seam_Click()
    {
        foreach (string path in Directory.EnumerateFiles(Path.Combine(AudioDir(), "music"), "*.wav")
                     .Concat(Directory.EnumerateFiles(Path.Combine(AudioDir(), "ambience"), "*.wav")))
        {
            WavClip clip = WavClip.Load(path);
            double sumSquares = 0;
            int steps = 0;
            for (int f = 1; f < clip.Frames; f++)
            {
                float d = clip.Samples[f * clip.Channels] - clip.Samples[(f - 1) * clip.Channels];
                sumSquares += d * d;
                steps++;
            }

            double typical = Math.Sqrt(sumSquares / steps);
            double seam = 0;
            for (int c = 0; c < clip.Channels; c++)
            {
                seam = Math.Max(seam, Math.Abs(clip.Samples[c] - clip.Samples[((clip.Frames - 1) * clip.Channels) + c]));
            }

            Assert.True(seam <= typical * 4.0, $"{path}: seam {seam} vs typical step {typical}");
        }
    }

    [Fact]
    public void Folder_Stays_Within_The_Twelve_Megabyte_Budget()
    {
        long total = Directory.EnumerateFiles(AudioDir(), "*", SearchOption.AllDirectories).Sum(p => new FileInfo(p).Length);

        Assert.InRange(total, 1, BudgetBytes);
    }
}
