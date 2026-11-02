using Dream.Configuration;

namespace Dream.Client.Audio;

// The player's audio settings: a switch and four 0..1 sliders. They live in DesktopClientConfig
// (audio_enabled, volume_master, volume_sfx, volume_music, volume_ui).
public readonly record struct AudioVolumes(bool Enabled, float Master, float Sfx, float Music, float Ui)
{
    public const float DefaultMaster = 0.8f;
    public const float DefaultSfx = 0.8f;
    public const float DefaultMusic = 0.5f;
    public const float DefaultUi = 0.7f;

    public static AudioVolumes Default => new(true, DefaultMaster, DefaultSfx, DefaultMusic, DefaultUi);

    public static AudioVolumes FromConfig(DesktopClientConfig? config)
    {
        if (config is null)
        {
            return Default;
        }

        return new AudioVolumes(
            config.AudioEnabled,
            Clamp01(config.VolumeMaster),
            Clamp01(config.VolumeSfx),
            Clamp01(config.VolumeMusic),
            Clamp01(config.VolumeUi));
    }

    public void ApplyTo(DesktopClientConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.AudioEnabled = Enabled;
        config.VolumeMaster = Clamp01(Master);
        config.VolumeSfx = Clamp01(Sfx);
        config.VolumeMusic = Clamp01(Music);
        config.VolumeUi = Clamp01(Ui);
    }

    // Master times the bus slider; zero while the switch is off.
    public float BusGain(AudioBus bus) => !Enabled ? 0f : Clamp01(Master) * bus switch
    {
        AudioBus.Music => Clamp01(Music),
        AudioBus.Ui => Clamp01(Ui),
        _ => Clamp01(Sfx)
    };

    internal static float Clamp01(float value) => float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 0f;
}
