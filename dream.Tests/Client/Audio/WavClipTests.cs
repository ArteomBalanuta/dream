using System.Buffers.Binary;
using Xunit;
using Dream.Client.Audio;

namespace Dream.Tests.Client.Audio;

public class WavClipTests
{
    internal static byte[] BuildWav(short[] samples, int channels, int sampleRate, bool extraChunk = false)
    {
        using MemoryStream stream = new();
        using BinaryWriter w = new(stream);
        int dataBytes = samples.Length * 2;
        w.Write("RIFF"u8);
        w.Write(36 + dataBytes + (extraChunk ? 12 : 0));
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write((short)channels);
        w.Write(sampleRate);
        w.Write(sampleRate * channels * 2);
        w.Write((short)(channels * 2));
        w.Write((short)16);
        if (extraChunk)
        {
            w.Write("LIST"u8);
            w.Write(4);
            w.Write("abcd"u8);
        }

        w.Write("data"u8);
        w.Write(dataBytes);
        foreach (short s in samples)
        {
            w.Write(s);
        }

        return stream.ToArray();
    }

    [Fact]
    public void Parses_Mono_Pcm16()
    {
        WavClip clip = WavClip.Parse(BuildWav([0, 16384, -16384, 32767], 1, 44100));

        Assert.Equal(1, clip.Channels);
        Assert.Equal(44100, clip.SampleRate);
        Assert.Equal(4, clip.Frames);
        Assert.Equal(0.5f, clip.Samples[1], 4);
        Assert.Equal(-0.5f, clip.Samples[2], 4);
    }

    [Fact]
    public void Parses_Stereo_And_Skips_Unknown_Chunks()
    {
        WavClip clip = WavClip.Parse(BuildWav([100, 200, 300, 400], 2, 22050, extraChunk: true));

        Assert.Equal(2, clip.Channels);
        Assert.Equal(22050, clip.SampleRate);
        Assert.Equal(2, clip.Frames);
        Assert.Equal(400 / 32768f, clip.Samples[3], 6);
    }

    [Fact]
    public void Rejects_Garbage_And_Unsupported_Formats()
    {
        Assert.Throws<InvalidDataException>(() => WavClip.Parse("not a wave file at all"u8.ToArray()));
        Assert.Throws<InvalidDataException>(() => WavClip.Parse([]));

        byte[] eightBit = BuildWav([1, 2], 1, 44100);
        BinaryPrimitives.WriteInt16LittleEndian(eightBit.AsSpan(34), 8);
        Assert.Throws<InvalidDataException>(() => WavClip.Parse(eightBit));

        byte[] noData = BuildWav([1, 2], 1, 44100)[..36];
        Assert.Throws<InvalidDataException>(() => WavClip.Parse(noData));
    }

    [Fact]
    public void Truncated_Data_Chunk_Keeps_The_Whole_Frames()
    {
        byte[] wav = BuildWav([1, 2, 3, 4], 2, 44100);
        WavClip clip = WavClip.Parse(wav[..^3]);

        Assert.Equal(1, clip.Frames);
    }

    [Fact]
    public void Bank_Skips_Missing_And_Broken_Files_And_Reports_Them()
    {
        string dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "sfx"));
            File.WriteAllBytes(Path.Combine(dir, "sfx", "footstep_1.wav"), BuildWav([0, 1000, 0], 1, 44100));
            File.WriteAllBytes(Path.Combine(dir, "sfx", "footstep_2.wav"), BuildWav([0, 2000, 0], 1, 44100));
            File.WriteAllBytes(Path.Combine(dir, "sfx", "swing.wav"), [1, 2, 3]);
            List<string> reports = [];

            AudioClipBank bank = AudioClipBank.Load(dir, reports.Add);

            Assert.True(bank.TryGet(SoundId.Footstep, new Random(1), out _));
            Assert.False(bank.TryGet(SoundId.Swing, new Random(1), out _));
            Assert.False(bank.TryGet(SoundId.Land, new Random(1), out _));
            Assert.Equal(2, bank.OneShotCount);
            Assert.Single(reports);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void FileStem_Is_Snake_Case()
    {
        Assert.Equal("footstep", SoundIds.FileStem(SoundId.Footstep));
        Assert.Equal("hit_critical", SoundIds.FileStem(SoundId.HitCritical));
        Assert.Equal("inventory_open", SoundIds.FileStem(SoundId.InventoryOpen));
    }
}
