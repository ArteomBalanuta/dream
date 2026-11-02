using System.Collections.Concurrent;
using System.Diagnostics;

namespace Dream.Client.Audio;

// Env-gated audio diagnostics, inert when unset.
//   DREAM_AUDIO_LOG=1        logs each started SoundId with its time and every music change, plus a
//                            per-second line of device Read statistics (count, mean/max interval, max
//                            Read duration, underruns = intervals much longer than the audio they covered).
//   DREAM_AUDIO_DUMP=<path>  writes the exact mixed stream (44.1 kHz stereo 16-bit) to a WAV file.
public static class AudioDiagnostics
{
    private static readonly Stopwatch s_clock = Stopwatch.StartNew();

    public static bool LogEnabled { get; } = IsOn(Environment.GetEnvironmentVariable("DREAM_AUDIO_LOG"));

    public static string? DumpPath { get; } = ResolveDump(Environment.GetEnvironmentVariable("DREAM_AUDIO_DUMP"));

    public static double Seconds => s_clock.Elapsed.TotalSeconds;

    public static bool IsOn(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Trim() != "0" && !value.Trim().Equals("false", StringComparison.OrdinalIgnoreCase);

    public static string? ResolveDump(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static void Log(string line)
    {
        if (LogEnabled)
        {
            Console.WriteLine($"[audio-log] t={Seconds:F3} {line}");
        }
    }
}

// Read statistics for the device thread. Cheap on the hot path; a timer prints once a second.
internal sealed class AudioReadStats : IDisposable
{
    private readonly object _gate = new();
    private readonly Timer? _timer;
    private long _lastTicks;
    private int _reads;
    private int _underruns;
    private double _sumInterval;
    private double _maxInterval;
    private double _maxDuration;
    private double _lastCoveredSeconds;
    private double _sumSquares;
    private long _sampleCount;

    public void Level(float[] buffer, int offset, int count)
    {
        if (_timer is null)
        {
            return;
        }

        double sum = 0;
        for (int i = 0; i < count; i += 2)
        {
            sum += buffer[offset + i] * buffer[offset + i];
        }

        lock (_gate)
        {
            _sumSquares += sum;
            _sampleCount += count / 2;
        }
    }

    private readonly AudioMixer? _mixer;

    public AudioReadStats(AudioMixer? mixer = null)
    {
        _mixer = mixer;
        if (AudioDiagnostics.LogEnabled)
        {
            _timer = new Timer(_ => Print(), null, 1000, 1000);
        }
    }

    public bool Active => _timer is not null;

    public void Begin(int frames)
    {
        if (_timer is null)
        {
            return;
        }

        long now = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            if (_lastTicks != 0)
            {
                double interval = (now - _lastTicks) / (double)Stopwatch.Frequency;
                _sumInterval += interval;
                _maxInterval = Math.Max(_maxInterval, interval);
                // The device queues several blocks; a gap longer than three blocks of audio is a dropout.
                if (interval > _lastCoveredSeconds * 3)
                {
                    _underruns++;
                }
            }

            _lastTicks = now;
            _lastCoveredSeconds = frames / (double)AudioMixer.SampleRate;
            _reads++;
        }
    }

    public void End(long beginTimestamp)
    {
        if (_timer is null)
        {
            return;
        }

        double duration = (Stopwatch.GetTimestamp() - beginTimestamp) / (double)Stopwatch.Frequency;
        lock (_gate)
        {
            _maxDuration = Math.Max(_maxDuration, duration);
        }
    }

    public void Dispose() => _timer?.Dispose();

    private void Print()
    {
        int reads;
        int underruns;
        double mean;
        double max;
        double maxDuration;
        double rmsOut;
        lock (_gate)
        {
            reads = _reads;
            underruns = _underruns;
            mean = reads > 1 ? _sumInterval / (reads - 1) : 0;
            max = _maxInterval;
            maxDuration = _maxDuration;
            _reads = 0;
            _underruns = 0;
            _sumInterval = 0;
            _maxInterval = 0;
            _maxDuration = 0;
            rmsOut = _sampleCount > 0 ? Math.Sqrt(_sumSquares / _sampleCount) * 32768 : 0;
            _sumSquares = 0;
            _sampleCount = 0;
        }

        AudioDiagnostics.Log($"reads={reads} meanGap={mean * 1000:F1}ms maxGap={max * 1000:F1}ms maxReadMs={maxDuration * 1000:F2} rmsL={rmsOut:F0} underruns={underruns} gc0={GC.CollectionCount(0)} gc2={GC.CollectionCount(2)} {_mixer?.DescribeLoops()}");
    }
}

// Writes the mixed stream to a 16-bit stereo WAV from a background thread, so the audio thread only
// converts a block and queues it.
internal sealed class AudioDumpWriter : IDisposable
{
    private readonly ConcurrentQueue<short[]> _blocks = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly FileStream _file;
    private readonly Thread _thread;
    private long _dataBytes;
    private volatile bool _stop;

    public AudioDumpWriter(string path)
    {
        _file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        _file.Write(new byte[44]);
        _thread = new Thread(Run) { IsBackground = true, Name = "audio-dump" };
        _thread.Start();
    }

    public void Add(float[] buffer, int offset, int count)
    {
        short[] block = new short[count];
        for (int i = 0; i < count; i++)
        {
            block[i] = (short)Math.Clamp(buffer[offset + i] * 32767f, -32768f, 32767f);
        }

        _blocks.Enqueue(block);
        _signal.Release();
    }

    public void Dispose()
    {
        _stop = true;
        _signal.Release();
        _thread.Join(2000);
        Drain();
        _file.Seek(0, SeekOrigin.Begin);
        using BinaryWriter w = new(_file);
        w.Write("RIFF"u8);
        w.Write((int)(36 + _dataBytes));
        w.Write("WAVEfmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write((short)2);
        w.Write(AudioMixer.SampleRate);
        w.Write(AudioMixer.SampleRate * 4);
        w.Write((short)4);
        w.Write((short)16);
        w.Write("data"u8);
        w.Write((int)_dataBytes);
    }

    private void Run()
    {
        while (!_stop)
        {
            _signal.Wait(200);
            Drain();
        }
    }

    private void Drain()
    {
        while (_blocks.TryDequeue(out short[]? block))
        {
            byte[] bytes = new byte[block.Length * 2];
            Buffer.BlockCopy(block, 0, bytes, 0, bytes.Length);
            _file.Write(bytes);
            _dataBytes += bytes.Length;
        }
    }
}

// DREAM_AUDIO_DUMP=<path>: no device is opened. A thread pulls the mixer in the same blocks and at the
// same cadence as the WaveOutEvent (about 33 ms) and writes the stream to the WAV file.
internal sealed class OfflineDumpOutput : IAudioOutput
{
    private const int BlockFrames = 1470;
    private readonly AudioMixer _mixer;
    private readonly AudioDumpWriter _writer;
    private readonly AudioReadStats _stats;
    private readonly Thread _thread;
    private volatile bool _stop;

    public OfflineDumpOutput(AudioMixer mixer, string path)
    {
        _mixer = mixer;
        _writer = new AudioDumpWriter(path);
        _stats = new AudioReadStats(mixer);
        _thread = new Thread(Run) { IsBackground = true, Name = "audio-offline" };
        AudioDiagnostics.Log($"offline dump to {path} (no device)");
        _thread.Start();
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join(2000);
        _stats.Dispose();
        _writer.Dispose();
    }

    private void Run()
    {
        float[] block = new float[BlockFrames * 2];
        long start = Stopwatch.GetTimestamp();
        long produced = 0;
        while (!_stop)
        {
            double elapsed = (Stopwatch.GetTimestamp() - start) / (double)Stopwatch.Frequency;
            if (produced > elapsed * AudioMixer.SampleRate)
            {
                Thread.Sleep(2);
                continue;
            }

            _stats.Begin(BlockFrames);
            long began = Stopwatch.GetTimestamp();
            int read = _mixer.Read(block, 0, block.Length);
            _stats.Level(block, 0, read);
            _writer.Add(block, 0, read);
            _stats.End(began);
            produced += BlockFrames;
        }
    }
}
