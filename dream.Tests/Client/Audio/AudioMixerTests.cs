using OpenTK.Mathematics;
using Xunit;
using Dream.Client.Audio;

namespace Dream.Tests.Client.Audio;

public class AudioMixerTests
{
    private const int Block = 2048;

    private static WavClip Constant(float value, int frames = 20000, int sampleRate = AudioMixer.SampleRate)
    {
        float[] samples = new float[frames];
        Array.Fill(samples, value);
        return new WavClip(samples, 1, sampleRate);
    }

    private static float[] Render(AudioMixer mixer, int frames = Block)
    {
        float[] buffer = new float[frames * 2];
        mixer.Read(buffer, 0, buffer.Length);
        return buffer;
    }

    private static AudioVolumes Full => new(true, 1f, 1f, 1f, 1f);

    [Fact]
    public void OneShot_Plays_Then_Finishes()
    {
        AudioMixer mixer = new(Full);
        mixer.PlayOneShot(SoundId.Hit, Constant(0.5f, 3000), 1f, 1f, 1f);

        float[] first = Render(mixer);
        Assert.Equal(0.5f, first[100], 3);
        Assert.Equal(1, mixer.ActiveVoiceCount);

        Render(mixer);
        float[] tail = Render(mixer);
        Assert.Equal(0, mixer.ActiveVoiceCount);
        Assert.Equal(0f, tail[0]);
    }

    [Fact]
    public void Output_Never_Clips_Even_With_Many_Loud_Voices()
    {
        AudioMixer mixer = new(Full);
        SoundId[] ids = Enum.GetValues<SoundId>();
        for (int i = 0; i < 24; i++)
        {
            mixer.PlayOneShot(ids[i % ids.Length], Constant(0.95f), 1f, 1f, 1f);
        }

        float[] buffer = Render(mixer);
        float peak = buffer.Max(MathF.Abs);
        Assert.InRange(peak, 0.5f, 1f);
        Assert.All(buffer, v => Assert.InRange(v, -1f, 1f));
    }

    [Fact]
    public void SoftLimit_Is_Transparent_Below_The_Knee_And_Bounded_Above()
    {
        Assert.Equal(0.3f, AudioMixer.SoftLimit(0.3f));
        Assert.Equal(-0.7f, AudioMixer.SoftLimit(-0.7f));
        Assert.True(AudioMixer.SoftLimit(50f) <= 1f);
        Assert.True(AudioMixer.SoftLimit(-50f) >= -1f);
        Assert.Equal(0f, AudioMixer.SoftLimit(float.NaN));
    }

    [Fact]
    public void Voice_Count_Is_Capped_At_Thirty_Two()
    {
        AudioMixer mixer = new(Full);
        SoundId[] ids = Enum.GetValues<SoundId>();
        // 26 distinct ids, cycled across several blocks so the retrigger guard never drops one.
        int started = 0;
        while (started < 60)
        {
            for (int i = 0; i < ids.Length && started < 60; i++, started++)
            {
                mixer.PlayOneShot(ids[i], Constant(0.1f, 400000), 1f, 1f, 1f);
            }

            Render(mixer, 4096);
        }

        Assert.Equal(AudioMixer.MaxVoices, mixer.ActiveVoiceCount);
    }

    [Fact]
    public void Same_Sound_Within_The_Retrigger_Guard_Starts_Once()
    {
        AudioMixer mixer = new(Full);
        mixer.PlayOneShot(SoundId.Footstep, Constant(0.2f), 1f, 1f, 1f);
        mixer.PlayOneShot(SoundId.Footstep, Constant(0.2f), 1f, 1f, 1f);
        Render(mixer, 256);
        Assert.Equal(1, mixer.ActiveVoiceCount);

        // 40 ms is 1764 frames; after that the same sound may start again.
        Render(mixer, 2000);
        mixer.PlayOneShot(SoundId.Footstep, Constant(0.2f), 1f, 1f, 1f);
        Render(mixer, 256);
        Assert.Equal(2, mixer.ActiveVoiceCount);
    }

    [Fact]
    public void Different_Sounds_Do_Not_Share_The_Guard()
    {
        AudioMixer mixer = new(Full);
        mixer.PlayOneShot(SoundId.Footstep, Constant(0.2f), 1f, 1f, 1f);
        mixer.PlayOneShot(SoundId.Swing, Constant(0.2f), 1f, 1f, 1f);
        Render(mixer, 256);
        Assert.Equal(2, mixer.ActiveVoiceCount);
    }

    [Fact]
    public void Bus_And_Master_Volumes_Scale_The_Output_And_Act_On_Playing_Sounds()
    {
        AudioMixer mixer = new(new AudioVolumes(true, 0.5f, 0.5f, 1f, 1f));
        mixer.PlayOneShot(SoundId.Hit, Constant(0.4f, 100000), 1f, 1f, 1f);
        float[] half = Render(mixer, 512);
        Assert.Equal(0.4f * 0.25f, half[10], 3);

        mixer.SetVolumes(new AudioVolumes(true, 1f, 1f, 1f, 1f));
        float[] full = Render(mixer, 512);
        Assert.Equal(0.4f, full[10], 3);

        mixer.SetVolumes(new AudioVolumes(false, 1f, 1f, 1f, 1f));
        Assert.Equal(0f, Render(mixer, 512)[10]);
    }

    [Fact]
    public void Ui_Sounds_Follow_The_Ui_Bus()
    {
        AudioMixer mixer = new(new AudioVolumes(true, 1f, 1f, 1f, 0.25f));
        mixer.PlayOneShot(SoundId.UiClick, Constant(0.8f, 100000), 1f, 1f, 1f);
        Assert.Equal(0.2f, Render(mixer, 256)[4], 3);
    }

    [Fact]
    public void Loop_Fades_In_Instead_Of_Starting_At_Full_Level()
    {
        AudioMixer mixer = new(Full);
        mixer.AttachLoop(AudioLoopId.MusicWorld, Constant(0.5f, 1000));
        mixer.SetLoopTarget(AudioLoopId.MusicWorld, 1f, 1f);

        float[] buffer = Render(mixer, 4096);
        Assert.True(buffer[0] < 0.01f, "the loop starts silent");
        Assert.True(buffer[2 * 4000] > buffer[2 * 100], "and rises");
        Assert.True(buffer[2 * 4000] < 0.5f, "one second is not over yet");
    }

    [Fact]
    public void Loop_Wraps_Without_A_Gap()
    {
        AudioMixer mixer = new(Full);
        mixer.AttachLoop(AudioLoopId.Wind, Constant(0.5f, 700));
        mixer.SetLoopTarget(AudioLoopId.Wind, 1f, 0.01f);
        float[] buffer = Render(mixer, 8000);
        Assert.All(buffer.Skip(2 * 2000), v => Assert.InRange(v, 0.499f, 0.5001f));
    }

    [Fact]
    public void Louder_Closer_And_Panned_Toward_The_Source()
    {
        Vector3 listener = Vector3.Zero;
        Vector3 right = Vector3.UnitX;

        AudioMixer.Spatialize(listener, right, new Vector3(0.2f, 0, 0), out float nearL, out float nearR);
        AudioMixer.Spatialize(listener, right, new Vector3(20f, 0, 0), out float farL, out float farR);
        AudioMixer.Spatialize(listener, right, new Vector3(-8f, 0, 0), out float leftL, out float leftR);
        AudioMixer.Spatialize(listener, right, new Vector3(100f, 0, 0), out float goneL, out float goneR);

        Assert.True(MathF.Abs(nearL - nearR) < 0.1f, "underfoot is centred");
        Assert.True(nearL + nearR > farL + farR, "far is quieter");
        Assert.True(farR > farL, "a source on the right is louder in the right ear");
        Assert.True(leftL > leftR, "a source on the left is louder in the left ear");
        Assert.Equal(0f, goneL + goneR, 5);
    }

    [Fact]
    public void Spatialize_Survives_Degenerate_Input()
    {
        AudioMixer.Spatialize(Vector3.Zero, Vector3.Zero, Vector3.Zero, out float l, out float r);
        Assert.True(float.IsFinite(l) && float.IsFinite(r));
        AudioMixer.Spatialize(Vector3.Zero, Vector3.UnitX, new Vector3(float.NaN, 0, 0), out l, out r);
        Assert.Equal(0f, l + r);
    }

    [Fact]
    public void Lower_Sample_Rate_Clips_Are_Resampled_To_Full_Length()
    {
        AudioMixer mixer = new(Full);
        // 0.1 s at 22.05 kHz must last 0.1 s of output (4410 frames), not 0.05 s.
        mixer.PlayOneShot(SoundId.Hit, Constant(0.5f, 2205, 22050), 1f, 1f, 1f);
        Render(mixer, 4000);
        Assert.Equal(1, mixer.ActiveVoiceCount);
        Render(mixer, 1000);
        Assert.Equal(0, mixer.ActiveVoiceCount);
    }
}
