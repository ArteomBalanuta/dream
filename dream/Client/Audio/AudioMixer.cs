using System.Collections.Concurrent;
using OpenTK.Mathematics;

namespace Dream.Client.Audio;

public enum AudioLoopId
{
    MusicMenu,
    MusicWorld,
    Wind,
    Night
}

// The pure-C# mixer: no device, no GL. The render thread enqueues commands (a lock-free queue);
// the audio thread calls Read, which drains the queue, mixes up to MaxVoices one-shots plus the
// four loop beds into interleaved stereo float at 44.1 kHz, and soft-limits the sum so it never
// exceeds full scale. Bus and master volumes are read at mix time, so sliders act at once.
public sealed class AudioMixer
{
    public const int SampleRate = 44100;
    public const int MaxVoices = 32;
    public const double RetriggerGuardSeconds = 0.04;
    public const float HearingRadius = 3f;
    public const float HearingRange = 40f;
    public const float MusicFadeSeconds = 2.5f;
    public const float AmbienceFadeSeconds = 3f;

    private enum CommandKind
    {
        Play,
        LoopClip,
        LoopTarget
    }

    private readonly record struct Command(
        CommandKind Kind,
        SoundId Id,
        WavClip? Clip,
        AudioBus Bus,
        float GainLeft,
        float GainRight,
        float Pitch,
        AudioLoopId Loop,
        float Target,
        float FadeSeconds);

    private readonly ConcurrentQueue<Command> _commands = new();
    private readonly List<AudioVoice> _voices = new(MaxVoices);
    private readonly long[] _lastStart = new long[SoundIds.Count];
    private readonly AudioLoop[] _loops = new AudioLoop[4];
    private readonly bool[] _loopIsMusic = [true, true, false, false];
    private long _clock;
    private AudioVolumes _volumes;
    private int _activeVoices;

    public AudioMixer(AudioVolumes volumes)
    {
        _volumes = volumes;
        for (int i = 0; i < _loops.Length; i++)
        {
            _loops[i] = new AudioLoop(null);
        }

        Array.Fill(_lastStart, long.MinValue / 2);
    }

    public AudioVolumes Volumes => _volumes;

    // Voices playing as of the last Read.
    public int ActiveVoiceCount => Volatile.Read(ref _activeVoices);

    // Current gain of a loop bed (diagnostics and tests; a racy read is fine).
    public float LoopGain(AudioLoopId loop) => _loops[(int)loop].Gain;

    public string DescribeLoops() =>
        $"music={LoopGain(AudioLoopId.MusicMenu):F2}/{LoopGain(AudioLoopId.MusicWorld):F2} wind={LoopGain(AudioLoopId.Wind):F2} night={LoopGain(AudioLoopId.Night):F2} voices={ActiveVoiceCount}";

    public void SetVolumes(AudioVolumes volumes)
    {
        _volumes = volumes;
    }

    public void PlayOneShot(SoundId id, WavClip clip, float gainLeft, float gainRight, float pitch)
    {
        _commands.Enqueue(new Command(CommandKind.Play, id, clip, SoundIds.BusOf(id), gainLeft, gainRight, pitch, default, 0f, 0f));
    }

    public void AttachLoop(AudioLoopId loop, WavClip? clip)
    {
        _commands.Enqueue(new Command(CommandKind.LoopClip, default, clip, AudioBus.Sfx, 0f, 0f, 1f, loop, 0f, 0f));
    }

    public void SetLoopTarget(AudioLoopId loop, float target, float fadeSeconds)
    {
        _commands.Enqueue(new Command(CommandKind.LoopTarget, default, null, AudioBus.Sfx, 0f, 0f, 1f, loop, target, fadeSeconds));
    }

    // Fills `count` floats (count / 2 stereo frames) from the audio thread. Always fills the buffer.
    public int Read(float[] buffer, int offset, int count)
    {
        int frames = count / 2;
        Array.Clear(buffer, offset, frames * 2);
        DrainCommands();
        AudioVolumes volumes = _volumes;
        float sfx = volumes.BusGain(AudioBus.Sfx);
        float music = volumes.BusGain(AudioBus.Music);
        float ui = volumes.BusGain(AudioBus.Ui);
        for (int i = _voices.Count - 1; i >= 0; i--)
        {
            AudioVoice voice = _voices[i];
            float gain = voice.Bus switch
            {
                AudioBus.Music => music,
                AudioBus.Ui => ui,
                _ => sfx
            };
            voice.MixInto(buffer, offset, frames, gain);
            if (voice.Finished)
            {
                _voices.RemoveAt(i);
            }
        }

        for (int i = 0; i < _loops.Length; i++)
        {
            _loops[i].MixInto(buffer, offset, frames, _loopIsMusic[i] ? music : sfx);
        }

        for (int i = 0; i < frames * 2; i++)
        {
            buffer[offset + i] = SoftLimit(buffer[offset + i]);
        }

        _clock += frames;
        Volatile.Write(ref _activeVoices, _voices.Count);
        return frames * 2;
    }

    // Transparent below 0.75, then a smooth knee that approaches but never reaches 1.
    public static float SoftLimit(float x)
    {
        if (!float.IsFinite(x))
        {
            return 0f;
        }

        const float Knee = 0.75f;
        float a = MathF.Abs(x);
        if (a <= Knee)
        {
            return x;
        }

        float limited = Knee + ((1f - Knee) * MathF.Tanh((a - Knee) / (1f - Knee)));
        return x < 0 ? -limited : limited;
    }

    // Distance and pan for a sound at `position`: full level within HearingRadius, then a quadratic
    // fade to silence at HearingRange. Pan is equal-power and widens with distance, so a sound
    // underfoot stays centred.
    public static void Spatialize(Vector3 listener, Vector3 right, Vector3 position, out float gainLeft, out float gainRight)
    {
        Vector3 delta = position - listener;
        float distance = delta.Length;
        if (!float.IsFinite(distance))
        {
            gainLeft = 0f;
            gainRight = 0f;
            return;
        }

        float attenuation = 1f;
        if (distance > HearingRadius)
        {
            float u = 1f - ((distance - HearingRadius) / (HearingRange - HearingRadius));
            attenuation = u <= 0f ? 0f : u * u;
        }

        float pan = 0f;
        if (distance > 0.001f)
        {
            Vector3 rightFlat = new(right.X, 0f, right.Z);
            if (rightFlat.LengthSquared > 1e-6f)
            {
                rightFlat.Normalize();
                pan = Math.Clamp(Vector3.Dot(delta / distance, rightFlat), -1f, 1f) * Math.Min(1f, distance / 3f);
            }
        }

        float angle = (pan + 1f) * (MathF.PI / 4f);
        gainLeft = MathF.Cos(angle) * attenuation;
        gainRight = MathF.Sin(angle) * attenuation;
    }

    private void DrainCommands()
    {
        while (_commands.TryDequeue(out Command command))
        {
            switch (command.Kind)
            {
                case CommandKind.Play:
                    StartVoice(command);
                    break;
                case CommandKind.LoopClip:
                    _loops[(int)command.Loop] = new AudioLoop(command.Clip);
                    break;
                case CommandKind.LoopTarget:
                    _loops[(int)command.Loop].SetTarget(command.Target, command.FadeSeconds);
                    break;
            }
        }
    }

    private void StartVoice(in Command command)
    {
        if (command.Clip is null || command.GainLeft + command.GainRight <= 0.0005f)
        {
            return;
        }

        int slot = (int)command.Id;
        if ((uint)slot >= (uint)_lastStart.Length)
        {
            return;
        }

        long guard = (long)(RetriggerGuardSeconds * SampleRate);
        if (_clock - _lastStart[slot] < guard)
        {
            return;
        }

        _lastStart[slot] = _clock;
        if (_voices.Count >= MaxVoices)
        {
            // Steal the oldest voice: the newest sound is the one the player just caused.
            int oldest = 0;
            for (int i = 1; i < _voices.Count; i++)
            {
                if (_voices[i].StartedAt < _voices[oldest].StartedAt)
                {
                    oldest = i;
                }
            }

            _voices.RemoveAt(oldest);
        }

        _voices.Add(new AudioVoice(command.Id, command.Clip, command.Bus, command.GainLeft, command.GainRight, command.Pitch, _clock));
    }
}
