using Xunit;
using Dream.Client.Audio;

namespace Dream.Tests.Client.Audio;

// Queue bounds, per-frame calls that must not enqueue, and the capture harness staying silent.
public class AudioHardeningTests
{
    private static WavClip Tone() => new(new float[2000], 1, 44100);

    [Fact]
    public void A_Device_That_Stops_Pulling_Cannot_Grow_The_Queue_Without_Bound()
    {
        AudioMixer mixer = new(AudioVolumes.Default);
        WavClip clip = Tone();
        for (int i = 0; i < 100_000; i++)
        {
            mixer.PlayOneShot(SoundId.Footstep, clip, 0.5f, 0.5f, 1f);
        }

        Assert.True(mixer.QueuedCommands <= AudioMixer.MaxQueuedCommands);
        float[] block = new float[1470 * 2];
        mixer.Read(block, 0, block.Length);
        Assert.Equal(0, mixer.QueuedCommands);
    }

    [Fact]
    public void Ambience_Every_Frame_Queues_Nothing_While_The_Target_Is_Unchanged()
    {
        AudioMixer mixer = new(AudioVolumes.Default);
        MixerGameAudio audio = new(mixer, AudioClipBank.FromClips(new Dictionary<SoundId, WavClip[]>()), null, AudioVolumes.Default);
        float[] block = new float[1470 * 2];
        mixer.Read(block, 0, block.Length);

        for (int i = 0; i < 5000; i++)
        {
            audio.SetAmbience(0.4f);
        }

        Assert.Equal(1, mixer.QueuedCommands);
    }

    [Fact]
    public void Capture_Harnesses_Stay_Silent_Unless_Audio_Is_Forced()
    {
        AudioVolumes on = AudioVolumes.Default;
        Assert.NotNull(GameAudio.ResolveDisableReason(k => k == "DREAM_HUD_CAPTURE" ? "dir" : null, on));
        Assert.NotNull(GameAudio.ResolveDisableReason(k => k == "DREAM_MENU_CAPTURE" ? "dir" : null, on));
        Assert.Null(GameAudio.ResolveDisableReason(k => k switch { "DREAM_HUD_CAPTURE" => "dir", "DREAM_AUDIO" => "1", _ => null }, on));
        Assert.NotNull(GameAudio.ResolveDisableReason(k => k switch { "DREAM_HUD_CAPTURE" => "dir", "DREAM_AUDIO" => "0", _ => null }, on));
    }
}
