using Xunit;
using Dream.Client.Audio;

namespace Dream.Tests.Client.Audio;

// The wind is outdoor ambience: silent under the main menu, up with the world, down again on return.
public class AudioWindTests
{
    private static float WindAfter(AudioMixer mixer, float seconds)
    {
        float[] block = new float[1470 * 2];
        int blocks = (int)MathF.Ceiling(seconds * AudioMixer.SampleRate / 1470f);
        for (int i = 0; i < blocks; i++)
        {
            mixer.Read(block, 0, block.Length);
        }

        return mixer.LoopGain(AudioLoopId.Wind);
    }

    private static MixerGameAudio Audio(AudioMixer mixer)
    {
        Dictionary<AudioLoopId, WavClip> loops = new()
        {
            [AudioLoopId.Wind] = new WavClip(Enumerable.Repeat(0.3f, 22050).ToArray(), 1, 22050)
        };
        return new MixerGameAudio(mixer, AudioClipBank.FromClips(new Dictionary<SoundId, WavClip[]>(), loops), null, AudioVolumes.Default);
    }

    [Fact]
    public void Wind_Stays_Silent_On_The_Main_Menu()
    {
        AudioMixer mixer = new(AudioVolumes.Default);
        MixerGameAudio audio = Audio(mixer);

        Assert.Equal(0f, WindAfter(mixer, 1f));
        audio.SetMusic(MusicTrack.Menu);
        Assert.Equal(0f, WindAfter(mixer, AudioMixer.AmbienceFadeSeconds + 1f));
    }

    [Fact]
    public void Wind_Rises_With_The_World_And_Falls_Back_On_The_Menu()
    {
        AudioMixer mixer = new(AudioVolumes.Default);
        MixerGameAudio audio = Audio(mixer);

        audio.SetMusic(MusicTrack.World);
        Assert.Equal(MixerGameAudio.WorldWindLevel, WindAfter(mixer, AudioMixer.AmbienceFadeSeconds + 1f), 3);

        audio.SetMusic(MusicTrack.Menu);
        Assert.Equal(0f, WindAfter(mixer, AudioMixer.AmbienceFadeSeconds + 1f), 3);
    }
}
