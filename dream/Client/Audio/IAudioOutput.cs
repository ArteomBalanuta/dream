namespace Dream.Client.Audio;

// The sound device: pulls the mixer on its own thread. Opening can throw (no device, driver or
// library missing); the caller (GameAudio) falls back to silence.
public interface IAudioOutput : IDisposable
{
}
