using Dream.Client.Audio;
using Xunit;

namespace Dream.Tests.Client.Audio;

public sealed class AudioOutputBackendTests
{
    private static Func<string, string?> Env(string? value) => name => name == "DREAM_AUDIO_BACKEND" ? value : null;

    [Fact]
    public void DefaultIsWinMmOnWindowsAndOpenAlElsewhere()
    {
        Assert.Equal(AudioBackend.WinMM, AudioOutputFactory.Resolve(Env(null), isWindows: true));
        Assert.Equal(AudioBackend.OpenAL, AudioOutputFactory.Resolve(Env(null), isWindows: false));
    }

    [Theory]
    [InlineData("openal", true, "OpenAL")]
    [InlineData("OpenAL", true, "OpenAL")]
    [InlineData(" WINMM ", false, "WinMM")]
    [InlineData("winmm", true, "WinMM")]
    public void OverrideWinsOnEitherOs(string value, bool isWindows, string expected)
    {
        Assert.Equal(expected, AudioOutputFactory.Resolve(Env(value), isWindows).ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("pulse")]
    [InlineData("1")]
    public void JunkFallsBackToTheOsDefault(string value)
    {
        Assert.Equal(AudioBackend.WinMM, AudioOutputFactory.Resolve(Env(value), isWindows: true));
        Assert.Equal(AudioBackend.OpenAL, AudioOutputFactory.Resolve(Env(value), isWindows: false));
    }

    [Fact]
    public void ConvertClampsNanAndEndpoints()
    {
        float[] src = [1.5f, -1.5f, float.NaN, 1f, -1f, 0f, 0.5f, float.PositiveInfinity];
        short[] dst = new short[src.Length];
        OpenALOutput.ConvertToPcm16(src, dst);
        Assert.Equal(short.MaxValue, dst[0]);
        Assert.Equal(-short.MaxValue, dst[1]);
        Assert.Equal(0, dst[2]);
        Assert.Equal(short.MaxValue, dst[3]);
        Assert.Equal(-short.MaxValue, dst[4]);
        Assert.Equal(0, dst[5]);
        Assert.InRange(dst[6], 16383, 16384);
        Assert.Equal(short.MaxValue, dst[7]);
    }

    // With no openal32.dll (or no device) opening must throw, never hang or crash, so GameAudio falls back to silence.
    [Fact]
    public void OpenAlOpenFailureThrowsOrOpensCleanly()
    {
        AudioMixer mixer = new(new AudioVolumes());
        try
        {
            using OpenALOutput output = new(mixer);
        }
        catch (Exception)
        {
            // Expected on a machine without OpenAL.
        }
    }
}
