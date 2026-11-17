using Xunit;
using Dream.Client.Audio;

namespace Dream.Tests.Client.Audio;

// Outdoor ambience belongs to the world: wind and night are silent under the main menu and after
// teardown, wind rises with the world, and both fall back when the world is left.
public class AudioWindTests
{
    private static readonly float Settle = AudioMixer.AmbienceFadeSeconds + 1f;

    private static float GainAfter(AudioMixer mixer, AudioLoopId loop, float seconds)
    {
        float[] block = new float[1470 * 2];
        int blocks = (int)MathF.Ceiling(seconds * AudioMixer.SampleRate / 1470f);
        for (int i = 0; i < blocks; i++)
        {
            mixer.Read(block, 0, block.Length);
        }

        return mixer.LoopGain(loop);
    }

    private static MixerGameAudio Audio(AudioMixer mixer)
    {
        WavClip Bed() => new(Enumerable.Repeat(0.3f, 22050).ToArray(), 1, 22050);
        Dictionary<AudioLoopId, WavClip> loops = new()
        {
            [AudioLoopId.Wind] = Bed(),
            [AudioLoopId.Night] = Bed()
        };
        return new MixerGameAudio(mixer, AudioClipBank.FromClips(new Dictionary<SoundId, WavClip[]>(), loops), null, AudioVolumes.Default);
    }

    [Fact]
    public void Wind_Stays_Silent_On_The_Main_Menu()
    {
        AudioMixer mixer = new(AudioVolumes.Default);
        MixerGameAudio audio = Audio(mixer);

        Assert.Equal(0f, GainAfter(mixer, AudioLoopId.Wind, 1f));
        audio.SetMusic(MusicTrack.Menu);
        Assert.Equal(0f, GainAfter(mixer, AudioLoopId.Wind, Settle));
    }

    [Theory]
    [InlineData(MusicTrack.Menu)]
    [InlineData(MusicTrack.None)]
    public void Leaving_The_World_Fades_Wind_And_Night_Out(MusicTrack next)
    {
        AudioMixer mixer = new(AudioVolumes.Default);
        MixerGameAudio audio = Audio(mixer);

        audio.SetMusic(MusicTrack.World);
        audio.SetMusic(MusicTrack.World);
        audio.SetAmbience(1f);
        Assert.Equal(MixerGameAudio.WorldWindLevel, GainAfter(mixer, AudioLoopId.Wind, Settle), 3);
        Assert.True(mixer.LoopGain(AudioLoopId.Night) > 0.5f, "night is up in the world after dark");

        audio.SetMusic(next);
        Assert.Equal(0f, GainAfter(mixer, AudioLoopId.Wind, Settle), 3);
        Assert.Equal(0f, mixer.LoopGain(AudioLoopId.Night), 3);
    }

    [Fact]
    public void Night_Comes_Back_On_Return_To_The_World()
    {
        AudioMixer mixer = new(AudioVolumes.Default);
        MixerGameAudio audio = Audio(mixer);

        audio.SetMusic(MusicTrack.World);
        audio.SetAmbience(1f);
        audio.SetMusic(MusicTrack.Menu);
        Assert.Equal(0f, GainAfter(mixer, AudioLoopId.Night, Settle), 3);

        audio.SetMusic(MusicTrack.World);
        audio.SetAmbience(1f);
        Assert.True(GainAfter(mixer, AudioLoopId.Night, Settle) > 0.5f, "the same night level is sent again");
    }
}
