using System.Buffers.Binary;

namespace Dream.Client.Audio;

// A decoded sound: interleaved float samples, 1 or 2 channels, any sample rate. The mixer resamples
// on the fly, so a 22.05 kHz loop and a 44.1 kHz effect share one output rate.
public sealed class WavClip
{
    public WavClip(float[] samples, int channels, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (channels is < 1 or > 2)
        {
            throw new ArgumentOutOfRangeException(nameof(channels));
        }

        if (sampleRate is < 4000 or > 192000)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }

        Samples = samples;
        Channels = channels;
        SampleRate = sampleRate;
        Frames = samples.Length / channels;
    }

    public float[] Samples { get; }

    public int Channels { get; }

    public int SampleRate { get; }

    public int Frames { get; }

    public double DurationSeconds => (double)Frames / SampleRate;

    public static WavClip Load(string path) => Parse(File.ReadAllBytes(path));

    // RIFF/WAVE, PCM 16-bit, mono or stereo. Anything else throws InvalidDataException.
    public static WavClip Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12 || !data[..4].SequenceEqual("RIFF"u8) || !data.Slice(8, 4).SequenceEqual("WAVE"u8))
        {
            throw new InvalidDataException("Not a RIFF/WAVE file.");
        }

        int channels = 0;
        int sampleRate = 0;
        int bits = 0;
        int format = 0;
        int position = 12;
        while (position + 8 <= data.Length)
        {
            ReadOnlySpan<byte> id = data.Slice(position, 4);
            int size = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(position + 4, 4));
            int body = position + 8;
            if (size < 0)
            {
                throw new InvalidDataException("Negative chunk size.");
            }

            int available = Math.Min(size, data.Length - body);
            if (id.SequenceEqual("fmt "u8))
            {
                if (available < 16)
                {
                    throw new InvalidDataException("Short fmt chunk.");
                }

                format = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(body, 2));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(body + 2, 2));
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(body + 4, 4));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(body + 14, 2));
            }
            else if (id.SequenceEqual("data"u8))
            {
                if (format != 1 || bits != 16)
                {
                    throw new InvalidDataException("Only 16-bit PCM is supported.");
                }

                if (channels is < 1 or > 2)
                {
                    throw new InvalidDataException("Only mono and stereo are supported.");
                }

                int count = available / 2 / channels * channels;
                float[] samples = new float[count];
                for (int i = 0; i < count; i++)
                {
                    samples[i] = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(body + (i * 2), 2)) / 32768f;
                }

                return new WavClip(samples, channels, sampleRate);
            }

            position = body + size + (size & 1);
        }

        throw new InvalidDataException("No data chunk.");
    }
}
