using System.Runtime.ExceptionServices;
using OpenTK.Audio.OpenAL;

namespace Dream.Client.Audio;

// The OpenAL sound device (OpenAL Soft on Linux/macOS). One pump thread owns the device, the context and
// one streaming source; every AL call happens on that thread after MakeContextCurrent. The constructor
// waits for the thread to report whether opening worked, so GameAudio's fall-back-to-silence sees the failure.
public sealed class OpenALOutput : IAudioOutput
{
    private const int BufferCount = 4;
    private const int FramesPerBuffer = 2048;
    private const int Channels = 2;
    private const int PollMilliseconds = 5;
    private const int OpenTimeoutMilliseconds = 5000;
    private const int JoinTimeoutMilliseconds = 2000;

    private readonly AudioMixer _mixer;
    private readonly AudioReadStats _stats;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _opened = new(false);
    private readonly CancellationTokenSource _stop = new();
    private Exception? _openError;
    private int _disposed;

    public OpenALOutput(AudioMixer mixer)
    {
        ArgumentNullException.ThrowIfNull(mixer);
        _mixer = mixer;
        _stats = new AudioReadStats(mixer);
        _thread = new Thread(Pump) { IsBackground = true, Name = "dream-openal" };
        try
        {
            _thread.Start();
            if (!_opened.Wait(OpenTimeoutMilliseconds))
            {
                // A driver that hangs while opening must not hang the game: the pump thread is a background
                // thread, and once _stop is set it releases whatever it opened as soon as it gets control back.
                throw new InvalidOperationException("OpenAL: opening the audio device timed out");
            }

            if (_openError is { } error)
            {
                _thread.Join(JoinTimeoutMilliseconds);
                ExceptionDispatchInfo.Capture(error).Throw();
            }
        }
        catch
        {
            // Same rule as NAudioOutput.StartDevice: release everything half-built before rethrowing.
            // The stats own a timer that would otherwise keep printing against a mixer nobody pulls.
            _stop.Cancel();
            _stats.Dispose();
            throw;
        }

        AudioDiagnostics.Log($"openal device opened ({BufferCount} x {FramesPerBuffer} frames)");
    }

    // float -> signed 16 bit. Clamps to [-1, 1]; NaN becomes silence.
    internal static void ConvertToPcm16(ReadOnlySpan<float> src, Span<short> dst)
    {
        int n = Math.Min(src.Length, dst.Length);
        for (int i = 0; i < n; i++)
        {
            float v = src[i];
            if (float.IsNaN(v))
            {
                dst[i] = 0;
                continue;
            }

            v = Math.Clamp(v, -1f, 1f);
            dst[i] = (short)MathF.Round(v * short.MaxValue);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _stop.Cancel();
        if (_thread.IsAlive)
        {
            _thread.Join(JoinTimeoutMilliseconds);
        }

        _stats.Dispose();
    }

    private void Pump()
    {
        ALDevice device = ALDevice.Null;
        ALContext context = ALContext.Null;
        int source = 0;
        int[] buffers = [];
        bool contextCurrent = false;
        try
        {
            device = ALC.OpenDevice(null);
            if (device == ALDevice.Null)
            {
                throw new InvalidOperationException("OpenAL: no audio device");
            }

            context = ALC.CreateContext(device, (int[]?)null);
            if (context == ALContext.Null || !ALC.MakeContextCurrent(context))
            {
                throw new InvalidOperationException("OpenAL: cannot create a context");
            }

            contextCurrent = true;
            source = AL.GenSource();
            buffers = AL.GenBuffers(BufferCount);
            ALError error = AL.GetError();
            if (error != ALError.NoError)
            {
                throw new InvalidOperationException("OpenAL: init error " + error);
            }

            float[] scratch = new float[FramesPerBuffer * Channels];
            short[] pcm = new short[scratch.Length];
            foreach (int buffer in buffers)
            {
                Fill(buffer, scratch, pcm);
            }

            AL.SourceQueueBuffers(source, buffers);
            AL.SourcePlay(source);
            error = AL.GetError();
            if (error != ALError.NoError)
            {
                throw new InvalidOperationException("OpenAL: start error " + error);
            }

            _opened.Set();
            Loop(source, scratch, pcm);
        }
        catch (Exception ex)
        {
            // A failure before the open was reported reaches the constructor; a later one just ends audio.
            if (!_opened.IsSet)
            {
                _openError = ex;
            }
            else
            {
                AudioDiagnostics.Log("openal pump stopped: " + ex.Message);
            }
        }
        finally
        {
            Release(device, context, source, buffers, contextCurrent);
            _opened.Set();
        }
    }

    private void Loop(int source, float[] scratch, short[] pcm)
    {
        while (!_stop.IsCancellationRequested)
        {
            Refill(source, scratch, pcm);

            // Underrun: the source ran dry and stopped. Every queued buffer is now processed, so refill again
            // (the source may have finished more since the first look) before restarting; otherwise the restart
            // would replay audio that was already heard.
            AL.GetSource(source, ALGetSourcei.SourceState, out int state);
            if ((ALSourceState)state != ALSourceState.Playing)
            {
                Refill(source, scratch, pcm);
                AL.SourcePlay(source);
            }

            _stop.Token.WaitHandle.WaitOne(PollMilliseconds);
        }
    }

    private void Refill(int source, float[] scratch, short[] pcm)
    {
        AL.GetSource(source, ALGetSourcei.BuffersProcessed, out int processed);
        if (processed > 0)
        {
            int[] done = AL.SourceUnqueueBuffers(source, processed);
            foreach (int buffer in done)
            {
                Fill(buffer, scratch, pcm);
            }

            AL.SourceQueueBuffers(source, done);
        }
    }

    private void Fill(int buffer, float[] scratch, short[] pcm)
    {
        _stats.Begin(FramesPerBuffer);
        long began = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            int read = _mixer.Read(scratch, 0, scratch.Length);
            _stats.Level(scratch, 0, read);
            if (read < scratch.Length)
            {
                Array.Clear(scratch, read, scratch.Length - read);
            }
        }
        catch (Exception)
        {
            // Audio must never take the game down: a failed block is a block of silence.
            Array.Clear(scratch);
        }
        finally
        {
            _stats.End(began);
        }

        ConvertToPcm16(scratch, pcm);
        AL.BufferData(buffer, ALFormat.Stereo16, pcm, AudioMixer.SampleRate);
    }

    private static void Release(ALDevice device, ALContext context, int source, int[] buffers, bool contextCurrent)
    {
        try
        {
            if (source != 0)
            {
                AL.SourceStop(source);
                AL.Source(source, ALSourcei.Buffer, 0);
                AL.DeleteSource(source);
            }

            if (buffers.Length > 0)
            {
                AL.DeleteBuffers(buffers);
            }
        }
        catch (Exception)
        {
            // Tearing down a device that is already gone.
        }

        try
        {
            if (contextCurrent)
            {
                ALC.MakeContextCurrent(ALContext.Null);
            }

            if (context != ALContext.Null)
            {
                ALC.DestroyContext(context);
            }

            if (device != ALDevice.Null)
            {
                ALC.CloseDevice(device);
            }
        }
        catch (Exception)
        {
            // Same.
        }
    }
}
