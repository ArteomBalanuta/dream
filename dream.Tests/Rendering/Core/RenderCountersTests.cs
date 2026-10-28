using Xunit;
using Dream.Rendering.Core;
using Dream.Tests.Utilities;

namespace Dream.Tests.Rendering.Core;

public class RenderCountersTests
{
    [Fact]
    public void Counts_Each_Kind_Separately()
    {
        RenderCounters.Reset();

        RenderCounters.CountDraw();
        RenderCounters.CountDraw();
        RenderCounters.CountDraws(5);
        RenderCounters.CountProgramBind();
        for (int i = 0; i < 4; i++)
        {
            RenderCounters.CountUniformUpload();
        }

        Assert.Equal(new RenderCounterSnapshot(7, 1, 4), RenderCounters.Capture());
        Assert.Equal(7, RenderCounters.DrawCalls);
        Assert.Equal(1, RenderCounters.ProgramBinds);
        Assert.Equal(4, RenderCounters.UniformUploads);
    }

    [Fact]
    public void Reset_Zeroes_Every_Counter()
    {
        RenderCounters.CountDraw();
        RenderCounters.CountProgramBind();
        RenderCounters.CountUniformUpload();

        RenderCounters.Reset();

        Assert.Equal(default, RenderCounters.Capture());
    }

    [Fact]
    public void CaptureAndReset_Returns_The_Frame_And_Starts_The_Next_From_Zero()
    {
        RenderCounters.Reset();
        RenderCounters.CountDraw();
        RenderCounters.CountProgramBind();
        RenderCounters.CountProgramBind();
        RenderCounters.CountUniformUpload();

        RenderCounterSnapshot frame = RenderCounters.CaptureAndReset();
        RenderCounters.CountDraw();

        Assert.Equal(new RenderCounterSnapshot(1, 2, 1), frame);
        Assert.Equal(new RenderCounterSnapshot(1, 0, 0), RenderCounters.Capture());
    }

    [Fact]
    public void Counters_Belong_To_The_Counting_Thread()
    {
        RenderCounters.Reset();
        RenderCounters.CountDraw();

        RenderCounterSnapshot other = default;
        Thread thread = new(() =>
        {
            RenderCounters.CountProgramBind();
            other = RenderCounters.Capture();
        });
        thread.Start();
        thread.Join();

        Assert.Equal(new RenderCounterSnapshot(0, 1, 0), other);
        Assert.Equal(new RenderCounterSnapshot(1, 0, 0), RenderCounters.Capture());
    }

    [Fact]
    public void Counting_Allocates_Nothing()
    {
        RenderCounters.Reset();
        AllocationAssert.AtMost(0, () =>
        {
            RenderCounters.CountDraw();
            RenderCounters.CountProgramBind();
            RenderCounters.CountUniformUpload();
            if (RenderCounters.CaptureAndReset().DrawCalls != 1)
            {
                throw new InvalidOperationException("Expected one draw.");
            }
        });
    }

    [Fact]
    public void Shader_Counts_Program_Binds_And_Every_Uniform_Upload()
    {
        // Shader needs a GL context to run, so this checks the source: every GL.Uniform* call
        // and the GL.UseProgram call is followed by its counter.
        string source = File.ReadAllText(Path.Combine(RepoRoot(), "dream", "Rendering", "Core", "Shader.cs"));
        string[] lines = source.Split('\n');
        int uniformCalls = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.StartsWith("GL.Uniform", StringComparison.Ordinal))
            {
                uniformCalls++;
                Assert.Equal("RenderCounters.CountUniformUpload();", lines[i + 1].Trim());
            }
            else if (line.StartsWith("GL.UseProgram(", StringComparison.Ordinal))
            {
                Assert.Equal("RenderCounters.CountProgramBind();", lines[i + 1].Trim());
            }
        }

        Assert.Equal(8, uniformCalls);
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
