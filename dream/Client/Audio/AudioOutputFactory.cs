namespace Dream.Client.Audio;

internal enum AudioBackend
{
    WinMM,
    OpenAL,
}

// Picks the sound device. WinMM (NAudio) on Windows, OpenAL (OpenAL Soft) elsewhere, where winmm
// does not exist. DREAM_AUDIO_BACKEND=winmm|openal overrides either way.
internal static class AudioOutputFactory
{
    internal static AudioBackend Resolve(Func<string, string?> env, bool isWindows)
    {
        ArgumentNullException.ThrowIfNull(env);
        string? value = env("DREAM_AUDIO_BACKEND")?.Trim();
        if (string.Equals(value, "winmm", StringComparison.OrdinalIgnoreCase))
        {
            return AudioBackend.WinMM;
        }

        if (string.Equals(value, "openal", StringComparison.OrdinalIgnoreCase))
        {
            return AudioBackend.OpenAL;
        }

        return isWindows ? AudioBackend.WinMM : AudioBackend.OpenAL;
    }

    // DREAM_AUDIO_DUMP still wins (no device). Throws when the device cannot be opened.
    internal static IAudioOutput Create(AudioMixer mixer)
    {
        ArgumentNullException.ThrowIfNull(mixer);
        if (AudioDiagnostics.DumpPath is { } dumpPath)
        {
            return new OfflineDumpOutput(mixer, dumpPath);
        }

        AudioBackend backend = Resolve(Environment.GetEnvironmentVariable, OperatingSystem.IsWindows());
        AudioDiagnostics.Log($"backend {backend}");
        return backend == AudioBackend.OpenAL ? new OpenALOutput(mixer) : new NAudioOutput(mixer);
    }
}
