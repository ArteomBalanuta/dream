using OpenTK.Mathematics;

namespace Dream.Client.Audio;

// The real IGameAudio: clips from the bank, spatialisation on the render thread, mixing on the
// device thread. No method throws.
public sealed class MixerGameAudio : IGameAudio
{
    private readonly AudioMixer _mixer;
    private readonly AudioClipBank _bank;
    private readonly IAudioOutput? _output;
    private readonly Random _random = new();
    private Vector3 _listener;
    private Vector3 _right = Vector3.UnitX;
    private MusicTrack _music = MusicTrack.None;
    private AudioVolumes _volumes;
    private bool _disposed;

    public MixerGameAudio(AudioMixer mixer, AudioClipBank bank, IAudioOutput? output, AudioVolumes volumes)
    {
        _mixer = mixer ?? throw new ArgumentNullException(nameof(mixer));
        _bank = bank ?? throw new ArgumentNullException(nameof(bank));
        _output = output;
        _volumes = volumes;
        _mixer.SetVolumes(volumes);
        foreach (AudioLoopId id in Enum.GetValues<AudioLoopId>())
        {
            if (_bank.TryGetLoop(id, out WavClip clip))
            {
                _mixer.AttachLoop(id, clip);
            }
        }

        _mixer.SetLoopTarget(AudioLoopId.Wind, 0.6f, AudioMixer.AmbienceFadeSeconds);
    }

    public bool IsActive => !_disposed && _volumes.Enabled;

    public AudioVolumes Volumes => _volumes;

    public void Play(SoundId id, float volume = 1f, float pitch = 1f)
    {
        const float Centre = 0.70710678f;
        Start(id, Centre * volume, Centre * volume, pitch);
    }

    public void PlayAt(SoundId id, Vector3 position, float volume = 1f, float pitch = 1f)
    {
        AudioMixer.Spatialize(_listener, _right, position, out float left, out float right);
        Start(id, left * volume, right * volume, pitch);
    }

    public void SetListener(Vector3 position, Vector3 right)
    {
        if (IsFinite(position))
        {
            _listener = position;
        }

        if (IsFinite(right))
        {
            _right = right;
        }
    }

    public void SetMusic(MusicTrack track)
    {
        if (_disposed || track == _music)
        {
            return;
        }

        _music = track;
        _mixer.SetLoopTarget(AudioLoopId.MusicMenu, track == MusicTrack.Menu ? 1f : 0f, AudioMixer.MusicFadeSeconds);
        _mixer.SetLoopTarget(AudioLoopId.MusicWorld, track == MusicTrack.World ? 1f : 0f, AudioMixer.MusicFadeSeconds);
    }

    public void SetAmbience(float night01)
    {
        if (_disposed)
        {
            return;
        }

        float night = float.IsFinite(night01) ? Math.Clamp(night01, 0f, 1f) : 0f;
        _mixer.SetLoopTarget(AudioLoopId.Night, night * 0.7f, AudioMixer.AmbienceFadeSeconds);
    }

    public void SetVolumes(AudioVolumes volumes)
    {
        _volumes = volumes;
        _mixer.SetVolumes(volumes);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _output?.Dispose();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[audio] device close failed: {ex.Message}");
        }
    }

    private void Start(SoundId id, float left, float right, float pitch)
    {
        if (_disposed || !_volumes.Enabled || !float.IsFinite(left) || !float.IsFinite(right))
        {
            return;
        }

        if (_bank.TryGet(id, _random, out WavClip clip))
        {
            _mixer.PlayOneShot(id, clip, Math.Max(0f, left), Math.Max(0f, right), pitch);
        }
    }

    private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
