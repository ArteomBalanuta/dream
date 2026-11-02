using OpenTK.Mathematics;

namespace Dream.Client.Audio;

// Always forwards to whatever GameAudio.Current is now, so a subscriber made before audio started
// (or after it was switched on in the options) still reaches the real thing.
public sealed class CurrentGameAudio : IGameAudio
{
    public static readonly CurrentGameAudio Instance = new();

    public bool IsActive => GameAudio.Current.IsActive;

    public AudioVolumes Volumes => GameAudio.Current.Volumes;

    public void Play(SoundId id, float volume = 1f, float pitch = 1f)
    {
        GameAudio.LogPlay(id, false);
        GameAudio.Current.Play(id, volume, pitch);
    }

    public void PlayAt(SoundId id, Vector3 position, float volume = 1f, float pitch = 1f)
    {
        GameAudio.LogPlay(id, true);
        GameAudio.Current.PlayAt(id, position, volume, pitch);
    }

    public void SetListener(Vector3 position, Vector3 right) => GameAudio.Current.SetListener(position, right);

    public void SetMusic(MusicTrack track) => GameAudio.Current.SetMusic(track);

    public void SetAmbience(float night01) => GameAudio.Current.SetAmbience(night01);

    public void SetVolumes(AudioVolumes volumes) => GameAudio.Current.SetVolumes(volumes);

    public void Dispose()
    {
    }
}
