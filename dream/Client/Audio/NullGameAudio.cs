using OpenTK.Mathematics;

namespace Dream.Client.Audio;

// Silence: used when audio is disabled, there is no device, or in tests. It remembers the volumes so
// a settings screen still reads back what it wrote.
public sealed class NullGameAudio : IGameAudio
{
    public static readonly NullGameAudio Instance = new();

    public bool IsActive => false;

    public AudioVolumes Volumes { get; private set; } = AudioVolumes.Default;

    public void Play(SoundId id, float volume = 1f, float pitch = 1f)
    {
    }

    public void PlayAt(SoundId id, Vector3 position, float volume = 1f, float pitch = 1f)
    {
    }

    public void SetListener(Vector3 position, Vector3 right)
    {
    }

    public void SetMusic(MusicTrack track)
    {
    }

    public void SetAmbience(float night01)
    {
    }

    public void SetVolumes(AudioVolumes volumes)
    {
        Volumes = volumes;
    }

    public void Dispose()
    {
    }
}
