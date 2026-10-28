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

    [Fact]
    public void Every_GL_Draw_Call_In_The_Game_Is_Followed_By_A_Draw_Count()
    {
        // Draws need a GL context, so this checks the source: each GL.Draw* statement (instanced
        // ones included, which count as one draw) is followed by RenderCounters.CountDraw().
        string root = Path.Combine(RepoRoot(), "dream");
        string separator = Path.DirectorySeparatorChar.ToString();
        int drawCalls = 0;
        foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, file);
            if (relative.StartsWith("bin" + separator, StringComparison.Ordinal) ||
                relative.StartsWith("obj" + separator, StringComparison.Ordinal))
            {
                continue;
            }

            string[] lines = File.ReadAllText(file).Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (!lines[i].TrimStart().StartsWith("GL.Draw", StringComparison.Ordinal))
                {
                    continue;
                }

                drawCalls++;
                int end = i;
                while (!lines[end].TrimEnd().EndsWith(");", StringComparison.Ordinal))
                {
                    end++;
                }

                int next = end + 1;
                while (lines[next].Trim().Length == 0 || lines[next].TrimStart().StartsWith("//", StringComparison.Ordinal))
                {
                    next++;
                }

                Assert.True(
                    lines[next].Trim() == "RenderCounters.CountDraw();",
                    $"{relative}:{i + 1} draws without RenderCounters.CountDraw() after it.");
            }
        }

        // StaticMeshBuffer, AnimatedModel (plain and instanced), Render (ImGui, cube fans,
        // crosshair) and StartupMenuRenderer (ImGui).
        Assert.True(drawCalls >= 7, $"Expected at least 7 GL.Draw* call sites, found {drawCalls}.");
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
