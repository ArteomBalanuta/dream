using Dream.Rendering.Ui;

namespace Dream.Client.Audio;

// The process-wide audio. Current is silent until EnsureStarted succeeds; it never throws and never
// returns null, so callers need no checks. Render thread only (start, shutdown, calls).
public static class GameAudio
{
    private static IGameAudio s_current = NullGameAudio.Instance;
    private static bool s_hardDisabled;
    private static string? s_loggedReason;
    private const string SettingOffReason = "audio_enabled=0";

    public static IGameAudio Current => s_current;

    // DREAM_AUDIO_LOG=1 prints every sound the game asks for ("[audio] play Swing"), even while audio is
    // silent (DREAM_AUDIO=0, the HUD capture): a verification aid, off by default.
    public static bool PlayLogEnabled { get; set; } = IsPlayLogRequested(Environment.GetEnvironmentVariable);

    public static bool IsPlayLogRequested(Func<string, string?> environment) =>
        environment("DREAM_AUDIO_LOG")?.Trim() == "1";

    public static void LogPlay(SoundId id, bool positional)
    {
        if (PlayLogEnabled)
        {
            Console.WriteLine($"[audio] play {id}{(positional ? " (at)" : string.Empty)}");
        }
    }

    // Idempotent. Starts the device and loads the clips on the first call that is allowed to sound;
    // later calls just apply the volumes. A setting that is off is retried on the next call (so
    // switching Sound on later works); a missing device or an environment switch is final.
    public static void EnsureStarted(AudioVolumes volumes)
    {
        // Menu clicks and hovers (the classic UI router) sound through whatever is current.
        UiInputRouter.Feedback = PlayUiFeedback;
        try
        {
            if (s_current is not NullGameAudio)
            {
                s_current.SetVolumes(volumes);
                return;
            }

            NullGameAudio.Instance.SetVolumes(volumes);
            if (s_hardDisabled)
            {
                return;
            }

            string? reason = ResolveDisableReason(Environment.GetEnvironmentVariable, volumes);
            if (reason is not null)
            {
                // The player's switch can come back on; an environment switch cannot.
                s_hardDisabled = volumes.Enabled || reason != SettingOffReason;
                LogOnce(reason);
                return;
            }

            string directory = AudioClipBank.DefaultDirectory;
            if (!Directory.Exists(directory))
            {
                Disable("no audio folder at " + directory);
                return;
            }

            AudioClipBank bank = AudioClipBank.Load(directory, Console.Error.WriteLine);
            AudioMixer mixer = new(volumes);
            // DREAM_AUDIO_DUMP replaces the device with a timer-driven pull into a WAV file (no sound);
            // otherwise the factory picks WinMM or OpenAL (DREAM_AUDIO_BACKEND).
            IAudioOutput output = AudioOutputFactory.Create(mixer);
            s_current = new MixerGameAudio(mixer, bank, output, volumes);
        }
        catch (Exception ex)
        {
            Disable(ex.Message);
        }
    }

    public static void Shutdown()
    {
        UiInputRouter.Feedback = null;
        IGameAudio current = s_current;
        s_current = NullGameAudio.Instance;
        try
        {
            current.Dispose();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[audio] shutdown failed: {ex.Message}");
        }
    }

    // Why audio must stay silent, or null when it may start. DREAM_AUDIO=0 and the capture harness
    // (DREAM_HUD_CAPTURE) are environment switches; audio_enabled=0 is the player's setting.
    public static string? ResolveDisableReason(Func<string, string?> environment, AudioVolumes volumes)
    {
        string? flag = environment("DREAM_AUDIO");
        if (flag is not null && (flag.Trim() == "0" || flag.Trim().Equals("false", StringComparison.OrdinalIgnoreCase)))
        {
            return "DREAM_AUDIO=0";
        }

        // DREAM_AUDIO=1 set explicitly lets a scripted capture run sound (audio diagnostics).
        bool forced = flag is not null && AudioDiagnostics.IsOn(flag);
        if (!forced && (!string.IsNullOrWhiteSpace(environment("DREAM_HUD_CAPTURE")) || !string.IsNullOrWhiteSpace(environment("DREAM_MENU_CAPTURE"))))
        {
            return "a capture harness (DREAM_HUD_CAPTURE or DREAM_MENU_CAPTURE) is set";
        }

        return volumes.Enabled ? null : SettingOffReason;
    }

    private static void PlayUiFeedback(UiFeedback feedback)
    {
        SoundId id = feedback switch
        {
            UiFeedback.Click => SoundId.UiClick,
            UiFeedback.Coin => SoundId.VendorCoin,
            UiFeedback.Refuse => SoundId.VendorRefuse,
            _ => SoundId.UiHover
        };
        LogPlay(id, false);
        s_current.Play(id);
    }

    private static void Disable(string reason)
    {
        s_hardDisabled = true;
        s_current = NullGameAudio.Instance;
        LogOnce(reason);
    }

    private static void LogOnce(string reason)
    {
        if (s_loggedReason == reason)
        {
            return;
        }

        s_loggedReason = reason;
        Console.WriteLine($"[audio] disabled: {reason}");
    }
}
