using OpenTK.Mathematics;

namespace Dream.Client.Audio;

// What the game calls to make sound. Always safe to call: the null implementation ignores everything,
// and the real one never throws into the caller. Everything here runs on the render thread.
public interface IGameAudio : IDisposable
{
    // True when sound can actually be heard (a device is open and the setting is on).
    bool IsActive { get; }

    AudioVolumes Volumes { get; }

    // A sound with no position (UI, the local player's own actions).
    void Play(SoundId id, float volume = 1f, float pitch = 1f);

    // A sound in the world: attenuated by distance and panned by the listener's right vector.
    void PlayAt(SoundId id, Vector3 position, float volume = 1f, float pitch = 1f);

    // Where the ears are: the local player's feet or head, and the camera's right vector.
    void SetListener(Vector3 position, Vector3 right);

    // Crossfades to a looping music track (None fades out).
    void SetMusic(MusicTrack track);

    // 0 is day, 1 is deep night: scales the night loop; the wind loop is always on.
    void SetAmbience(float night01);

    void SetVolumes(AudioVolumes volumes);
}
