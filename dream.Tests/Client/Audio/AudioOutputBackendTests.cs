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

    [Theory]
    [InlineData("\topenal\n", true, "OpenAL")]
    [InlineData("OPENAL", false, "OpenAL")]
    [InlineData("  WinMM	", false, "WinMM")]
    [InlineData("   ", true, "WinMM")]
    [InlineData("   ", false, "OpenAL")]
    public void WhitespaceAndCaseAreTolerated(string value, bool isWindows, string expected)
    {
        Assert.Equal(expected, AudioOutputFactory.Resolve(Env(value), isWindows).ToString());
    }

    [Fact]
    public void ResolveReadsOnlyTheBackendVariableAndRejectsANullReader()
    {
        List<string> asked = [];
        AudioOutputFactory.Resolve(name => { asked.Add(name); return null; }, isWindows: true);
        Assert.Equal(["DREAM_AUDIO_BACKEND"], asked);
        Assert.Throws<ArgumentNullException>(() => AudioOutputFactory.Resolve(null!, isWindows: true));
        Assert.Throws<ArgumentNullException>(() => AudioOutputFactory.Create(null!));
    }

    // The dump switch is what decides "no device"; Create tests DumpPath, which is read once at type load,
    // so precedence over the backend choice itself cannot be driven from a test without a seam. Pin the parser.
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("  ", null)]
    [InlineData(" out.wav ", "out.wav")]
    public void DumpPathParsingTrims(string? value, string? expected)
    {
        Assert.Equal(expected, AudioDiagnostics.ResolveDump(value));
    }

    [Fact]
    public void ConvertHandlesMismatchedLengthsAndNegativeRounding()
    {
        short[] dst = [7, 7, 7, 7];
        OpenALOutput.ConvertToPcm16([0.5f, -0.5f], dst);
        Assert.InRange(dst[0], 16383, 16384);
        Assert.InRange(dst[1], -16384, -16383);
        Assert.Equal(7, dst[2]);
        Assert.Equal(7, dst[3]);

        short[] small = new short[1];
        OpenALOutput.ConvertToPcm16([1f, 1f, 1f], small);
        Assert.Equal(short.MaxValue, small[0]);

        short[] untouched = [7, 7];
        OpenALOutput.ConvertToPcm16([], untouched);
        Assert.Equal([(short)7, (short)7], untouched);
    }

    // With no OpenAL library (or no device) opening must throw one of the failures the backend produces, and must
    // return within a bound, so GameAudio falls back to silence instead of hanging or crashing.
    [Fact]
    public void OpenAlOpenFailureThrowsExpectedTypeWithinBoundOrOpensCleanly()
    {
        AudioMixer mixer = new(new AudioVolumes());
        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using OpenALOutput output = new(mixer);
        }
        catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException or InvalidOperationException)
        {
            // Library missing, or the library loaded and reported no device / no context / a timeout.
        }

        // 5 s open timeout + 2 s join timeout inside the backend, plus slack.
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), "OpenALOutput construction took " + clock.Elapsed);
    }
}
