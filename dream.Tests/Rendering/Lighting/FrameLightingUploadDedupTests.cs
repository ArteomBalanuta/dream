using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Dream.Rendering.Lighting;
using Xunit;

namespace Dream.Tests.Rendering.Lighting;

// The frame uploads FrameLighting twice with identical contents (shadow prep, then the world pass); the repeat is skipped.
public class FrameLightingUploadDedupTests
{
    [Fact]
    public void SameBytes_Is_True_For_Copies()
    {
        FrameLighting a = FrameLighting.ReferenceNoon;
        FrameLighting b = a;
        Assert.True(FrameLightingBuffer.SameBytes(in a, in b));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(FrameLighting.SizeInBytes / 2)]
    [InlineData(FrameLighting.SizeInBytes - 4)]
    public void SameBytes_Is_False_For_A_One_Ulp_Change(int byteOffset)
    {
        FrameLighting a = FrameLighting.ReferenceNoon;
        FrameLighting b = a;
        ref float member = ref Unsafe.As<byte, float>(ref Unsafe.Add(ref Unsafe.As<FrameLighting, byte>(ref b), byteOffset));
        member = BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(member) + 1);

        Assert.False(FrameLightingBuffer.SameBytes(in a, in b));
        Assert.False(FrameLightingBuffer.SameBytes(in b, in a));
    }

    [Fact]
    public void The_Skip_Guards_Both_Uploads_And_The_Counter_But_Not_BindWorld()
    {
        string source = File.ReadAllText(Path.Combine(RepoRoot(), "dream", "Rendering", "Lighting", "FrameLightingBuffer.cs"));
        Match upload = Regex.Match(
            source,
            @"if \(!_hasUploaded \|\| !SameBytes\(in world, in _lastWorld\) \|\| !SameBytes\(in viewModel, in _lastViewModel\)\)\s*\{(?<body>.*?)\n        \}\s*\n\s*BindWorld\(\);",
            RegexOptions.Singleline);

        Assert.True(upload.Success);
        string body = upload.Groups["body"].Value;
        Assert.Equal(2, Regex.Matches(body, @"GL\.BufferSubData\(").Count);
        Assert.Contains("RenderCounters.CountUniformUpload();", body);
        Assert.DoesNotContain("BindWorld", body);
    }

    private static string RepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "dream", "dream.csproj")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
