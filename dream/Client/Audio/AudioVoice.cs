namespace Dream.Client.Audio;

// One playing one-shot. Audio thread only. Gains are the spatial left/right gains; the bus and
// master volumes are applied by the mixer on every block, so a slider acts on sounds already playing.
internal sealed class AudioVoice
{
    private readonly WavClip _clip;
    private readonly double _step;
    private double _position;

    public AudioVoice(SoundId id, WavClip clip, AudioBus bus, float gainLeft, float gainRight, float pitch, long startedAt)
    {
        Id = id;
        _clip = clip;
        Bus = bus;
        GainLeft = gainLeft;
        GainRight = gainRight;
        StartedAt = startedAt;
        float safePitch = float.IsFinite(pitch) ? Math.Clamp(pitch, 0.25f, 4f) : 1f;
        _step = (double)clip.SampleRate / AudioMixer.SampleRate * safePitch;
    }

    public SoundId Id { get; }

    public AudioBus Bus { get; }

    public float GainLeft { get; }

    public float GainRight { get; }

    public long StartedAt { get; }

    public bool Finished => _position >= _clip.Frames - 1;

    // Adds up to `frames` stereo frames into `mix` (interleaved), scaled by busGain.
    public void MixInto(float[] mix, int offset, int frames, float busGain)
    {
        float[] src = _clip.Samples;
        int channels = _clip.Channels;
        int last = _clip.Frames - 1;
        float gl = GainLeft * busGain;
        float gr = GainRight * busGain;
        double pos = _position;
        for (int i = 0; i < frames && pos < last; i++)
        {
            int index = (int)pos;
            float frac = (float)(pos - index);
            float l;
            float r;
            if (channels == 1)
            {
                float s = src[index] + ((src[index + 1] - src[index]) * frac);
                l = s;
                r = s;
            }
            else
            {
                int a = index * 2;
                l = src[a] + ((src[a + 2] - src[a]) * frac);
                r = src[a + 1] + ((src[a + 3] - src[a + 1]) * frac);
            }

            mix[offset + (i * 2)] += l * gl;
            mix[offset + (i * 2) + 1] += r * gr;
            pos += _step;
        }

        _position = pos;
    }
}

// A looping bed (music track, wind, night): its gain glides to a target so starts, stops and
// crossfades never click. Audio thread only; the clip is set once.
internal sealed class AudioLoop
{
    private readonly WavClip? _clip;
    private readonly double _step;
    private double _position;
    private float _gain;
    private float _target;
    private float _fadePerFrame = 1f;

    public AudioLoop(WavClip? clip)
    {
        _clip = clip is { Frames: >= 2 } ? clip : null;
        _step = _clip is null ? 0 : (double)_clip.SampleRate / AudioMixer.SampleRate;
    }

    public float Gain => _gain;

    public float Target => _target;

    public bool HasClip => _clip is not null;

    public void SetTarget(float target, float fadeSeconds)
    {
        _target = Math.Clamp(float.IsFinite(target) ? target : 0f, 0f, 1f);
        _fadePerFrame = 1f / Math.Max(1f, Math.Max(0.01f, fadeSeconds) * AudioMixer.SampleRate);
    }

    public void MixInto(float[] mix, int offset, int frames, float busGain)
    {
        if (_clip is null || (_gain <= 0f && _target <= 0f))
        {
            _gain = 0f;
            return;
        }

        float[] src = _clip.Samples;
        int channels = _clip.Channels;
        int count = _clip.Frames;
        double pos = _position;
        float gain = _gain;
        for (int i = 0; i < frames; i++)
        {
            if (gain < _target)
            {
                gain = Math.Min(_target, gain + _fadePerFrame);
            }
            else if (gain > _target)
            {
                gain = Math.Max(_target, gain - _fadePerFrame);
            }

            int index = (int)pos;
            int next = index + 1 >= count ? 0 : index + 1;
            float frac = (float)(pos - index);
            float l;
            float r;
            if (channels == 1)
            {
                float s = src[index] + ((src[next] - src[index]) * frac);
                l = s;
                r = s;
            }
            else
            {
                l = src[index * 2] + ((src[next * 2] - src[index * 2]) * frac);
                r = src[(index * 2) + 1] + ((src[(next * 2) + 1] - src[(index * 2) + 1]) * frac);
            }

            float g = gain * busGain;
            mix[offset + (i * 2)] += l * g;
            mix[offset + (i * 2) + 1] += r * g;
            pos += _step;
            if (pos >= count)
            {
                pos -= count;
            }
        }

        _position = pos;
        _gain = gain;
    }
}
