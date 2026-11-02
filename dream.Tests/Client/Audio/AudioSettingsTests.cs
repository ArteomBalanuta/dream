using System.Globalization;
using Xunit;
using Dream.Client.Audio;
using Dream.Configuration;
using Dream.Rendering.Ui;
using OpenTK.Mathematics;

namespace Dream.Tests.Client.Audio;

public class AudioSettingsTests
{
    [Fact]
    public void Defaults_Match_The_Plan()
    {
        AudioVolumes v = AudioVolumes.FromConfig(new DesktopClientConfig());

        Assert.Equal(new AudioVolumes(true, 0.8f, 0.8f, 0.5f, 0.7f), v);
        Assert.Equal(v, AudioVolumes.FromConfig(null));
    }

    [Fact]
    public void Config_Round_Trips_Through_The_Text_File()
    {
        string dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            string path = Path.Combine(dir, "desktop-client.txt");
            DesktopClientConfig config = new();
            new AudioVolumes(false, 0.25f, 0.5f, 0.125f, 1f).ApplyTo(config);
            DesktopClientTextConfig.Save(path, config);

            DesktopClientConfig loaded = DesktopClientTextConfig.LoadOrCreate(path);

            Assert.Equal(new AudioVolumes(false, 0.25f, 0.5f, 0.125f, 1f), AudioVolumes.FromConfig(loaded));
            string text = File.ReadAllText(path);
            Assert.Contains("audio_enabled=0", text);
            Assert.Contains("volume_music=0.125", text);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Missing_Keys_Keep_The_Defaults_And_Bad_Values_Are_Clamped()
    {
        string dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            string path = Path.Combine(dir, "desktop-client.txt");
            File.WriteAllText(path, "host=1.2.3.4\nvolume_master=7\nvolume_sfx=-3\nvolume_ui=NaN\n");

            AudioVolumes v = AudioVolumes.FromConfig(DesktopClientTextConfig.LoadOrCreate(path));

            Assert.True(v.Enabled);
            Assert.Equal(1f, v.Master);
            Assert.Equal(0f, v.Sfx);
            Assert.Equal(0.5f, v.Music);
            Assert.Equal(0.7f, v.Ui);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Sanitize_Clamps_Volumes_On_The_Config_Itself()
    {
        DesktopClientConfig config = new() { VolumeMaster = 9f, VolumeMusic = float.PositiveInfinity, VolumeUi = -1f };

        config.Sanitize();

        Assert.Equal(1f, config.VolumeMaster);
        Assert.Equal(0.5f, config.VolumeMusic);
        Assert.Equal(0f, config.VolumeUi);
    }

    [Theory]
    [InlineData("0", true, "DREAM_AUDIO=0")]
    [InlineData("false", true, "DREAM_AUDIO=0")]
    [InlineData("1", true, null)]
    [InlineData(null, false, "audio_enabled=0")]
    [InlineData(null, true, null)]
    public void Disable_Reason_Follows_Environment_And_Setting(string? dreamAudio, bool enabled, string? expected)
    {
        string? reason = GameAudio.ResolveDisableReason(
            name => name == "DREAM_AUDIO" ? dreamAudio : null,
            AudioVolumes.Default with { Enabled = enabled });

        Assert.Equal(expected, reason);
    }

    [Fact]
    public void Capture_Mode_Is_Always_Silent()
    {
        string? reason = GameAudio.ResolveDisableReason(
            name => name == "DREAM_HUD_CAPTURE" ? "out/dir" : null,
            AudioVolumes.Default);

        Assert.NotNull(reason);
    }

    [Fact]
    public void Null_Audio_Is_Safe_And_Silent()
    {
        NullGameAudio audio = new();
        audio.Play(SoundId.Hit);
        audio.PlayAt(SoundId.Hit, Vector3.One);
        audio.SetListener(Vector3.Zero, Vector3.UnitX);
        audio.SetMusic(MusicTrack.World);
        audio.SetAmbience(1f);
        audio.SetVolumes(new AudioVolumes(false, 0, 0, 0, 0));

        Assert.False(audio.IsActive);
        Assert.False(audio.Volumes.Enabled);
        audio.Dispose();
    }

    [Fact]
    public void Router_Reports_Hover_Once_And_Click_On_Release()
    {
        UiInputRouter router = new();
        List<UiFeedback> heard = [];
        router.LocalFeedback = heard.Add;
        UiRect box = new(10, 10, 40, 40);

        void Frame(float x, float y, bool left)
        {
            router.BeginFrame(new UiPointer(new Vector2(x, y), left, false, true));
            router.Button(1, box);
            router.EndFrame();
        }

        Frame(300, 300, false);
        Assert.Empty(heard);
        Frame(20, 20, false);
        Frame(20, 20, false);
        Frame(21, 20, false);
        Assert.Equal([UiFeedback.Hover], heard);

        Frame(20, 20, true);
        Frame(20, 20, false);
        Assert.Equal([UiFeedback.Hover, UiFeedback.Click], heard);

        Frame(300, 300, false);
        Frame(300, 300, false);
        Frame(20, 20, false);
        Frame(20, 20, false);
        Assert.Equal(3, heard.Count);
        Assert.Equal(UiFeedback.Hover, heard[2]);
    }

    [Fact]
    public void Router_Without_A_Listener_Does_Nothing_Special()
    {
        UiInputRouter router = new();
        router.BeginFrame(new UiPointer(new Vector2(20, 20), false, false, true));
        Assert.False(router.Button(1, new UiRect(10, 10, 40, 40)));
    }

    [Fact]
    public void Volume_Text_Uses_Invariant_Culture()
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            DesktopClientConfig config = new() { VolumeMusic = 0.5f };
            Assert.Contains("volume_music=0.5", DesktopClientTextConfig.Serialize(config));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
