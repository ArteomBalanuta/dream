using System.IO;
using System.Text.RegularExpressions;
using Dream.Rendering.Core;
using OpenTK.Graphics.OpenGL4;
using Xunit;

namespace Dream.Tests.Rendering.Core;

// GPU-7: KHR_debug messages are classified by type and severity. Real problems are render faults;
// driver performance notices ("shader recompiled based on GL state") are logged once per id as
// information and never counted as faults.
public class GLDebugMessageFilterTests
{
    [Theory]
    [InlineData(DebugType.DebugTypeError, DebugSeverity.DebugSeverityHigh, GLDebugMessageDisposition.ReportFault)]
    [InlineData(DebugType.DebugTypeError, DebugSeverity.DebugSeverityMedium, GLDebugMessageDisposition.ReportFault)]
    [InlineData(DebugType.DebugTypeUndefinedBehavior, DebugSeverity.DebugSeverityHigh, GLDebugMessageDisposition.ReportFault)]
    [InlineData(DebugType.DebugTypeUndefinedBehavior, DebugSeverity.DebugSeverityMedium, GLDebugMessageDisposition.ReportFault)]
    [InlineData(DebugType.DebugTypeDeprecatedBehavior, DebugSeverity.DebugSeverityHigh, GLDebugMessageDisposition.ReportFault)]
    [InlineData(DebugType.DebugTypeDeprecatedBehavior, DebugSeverity.DebugSeverityMedium, GLDebugMessageDisposition.ReportFault)]
    [InlineData(DebugType.DebugTypePerformance, DebugSeverity.DebugSeverityHigh, GLDebugMessageDisposition.LogInfo)]
    [InlineData(DebugType.DebugTypePerformance, DebugSeverity.DebugSeverityMedium, GLDebugMessageDisposition.LogInfo)]
    [InlineData(DebugType.DebugTypePortability, DebugSeverity.DebugSeverityMedium, GLDebugMessageDisposition.LogInfo)]
    [InlineData(DebugType.DebugTypeOther, DebugSeverity.DebugSeverityHigh, GLDebugMessageDisposition.LogInfo)]
    [InlineData(DebugType.DebugTypeMarker, DebugSeverity.DebugSeverityHigh, GLDebugMessageDisposition.Ignore)]
    [InlineData(DebugType.DebugTypePushGroup, DebugSeverity.DebugSeverityMedium, GLDebugMessageDisposition.Ignore)]
    [InlineData(DebugType.DebugTypePopGroup, DebugSeverity.DebugSeverityMedium, GLDebugMessageDisposition.Ignore)]
    [InlineData(DebugType.DebugTypeError, DebugSeverity.DebugSeverityLow, GLDebugMessageDisposition.Ignore)]
    [InlineData(DebugType.DebugTypeError, DebugSeverity.DebugSeverityNotification, GLDebugMessageDisposition.Ignore)]
    [InlineData(DebugType.DebugTypeUndefinedBehavior, DebugSeverity.DebugSeverityLow, GLDebugMessageDisposition.Ignore)]
    [InlineData(DebugType.DebugTypePerformance, DebugSeverity.DebugSeverityLow, GLDebugMessageDisposition.Ignore)]
    [InlineData(DebugType.DebugTypePerformance, DebugSeverity.DebugSeverityNotification, GLDebugMessageDisposition.Ignore)]
    public void Classification_Follows_Type_And_Severity(DebugType type, DebugSeverity severity, GLDebugMessageDisposition expected)
    {
        Assert.Equal(expected, GLUtils.ClassifyDebugMessage(type, severity));
    }

    [Fact]
    public void The_Shader_Recompiled_Notice_From_The_Wave8_Perf_Run_Is_Information_Logged_Once()
    {
        // NVIDIA id 131218: "Vertex shader in program N is being recompiled based on GL state",
        // reported as DebugSeverityMedium DebugTypePerformance from DebugSourceApi.
        GLDebugMessageFilter filter = new();

        Assert.Equal(
            GLDebugMessageDisposition.LogInfo,
            filter.Decide(DebugSource.DebugSourceApi, DebugType.DebugTypePerformance, 131218, DebugSeverity.DebugSeverityMedium));
        for (int i = 0; i < 10; i++)
        {
            Assert.Equal(
                GLDebugMessageDisposition.Ignore,
                filter.Decide(DebugSource.DebugSourceApi, DebugType.DebugTypePerformance, 131218, DebugSeverity.DebugSeverityMedium));
        }
    }

    [Fact]
    public void Information_Is_Deduplicated_Per_Id_And_Source()
    {
        GLDebugMessageFilter filter = new();

        Assert.Equal(GLDebugMessageDisposition.LogInfo, Decide(filter, DebugSource.DebugSourceApi, DebugType.DebugTypePerformance, 1));
        Assert.Equal(GLDebugMessageDisposition.LogInfo, Decide(filter, DebugSource.DebugSourceApi, DebugType.DebugTypePerformance, 2));
        // KHR_debug scopes ids by source, so the same number from another source is another message.
        Assert.Equal(GLDebugMessageDisposition.LogInfo, Decide(filter, DebugSource.DebugSourceShaderCompiler, DebugType.DebugTypePerformance, 1));
        // A different informational type with an id already logged is still that same message id.
        Assert.Equal(GLDebugMessageDisposition.Ignore, Decide(filter, DebugSource.DebugSourceApi, DebugType.DebugTypeOther, 1));
        Assert.Equal(GLDebugMessageDisposition.Ignore, Decide(filter, DebugSource.DebugSourceApi, DebugType.DebugTypePerformance, 2));
    }

    [Fact]
    public void Faults_Are_Never_Deduplicated_By_The_Filter()
    {
        GLDebugMessageFilter filter = new();

        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(GLDebugMessageDisposition.ReportFault, Decide(filter, DebugSource.DebugSourceApi, DebugType.DebugTypeError, 1281));
        }

        // An info message with the id of an earlier fault is still logged once.
        Assert.Equal(GLDebugMessageDisposition.LogInfo, Decide(filter, DebugSource.DebugSourceApi, DebugType.DebugTypePerformance, 1281));
    }

    [Fact]
    public void Ignored_Messages_Do_Not_Use_Up_An_Id()
    {
        GLDebugMessageFilter filter = new();

        Assert.Equal(
            GLDebugMessageDisposition.Ignore,
            filter.Decide(DebugSource.DebugSourceApi, DebugType.DebugTypePerformance, 7, DebugSeverity.DebugSeverityLow));
        Assert.Equal(GLDebugMessageDisposition.LogInfo, Decide(filter, DebugSource.DebugSourceApi, DebugType.DebugTypePerformance, 7));
    }

    [Fact]
    public void Concurrent_Callers_Log_Each_Id_Exactly_Once()
    {
        GLDebugMessageFilter filter = new();
        int logged = 0;

        Parallel.For(0, 4000, i =>
        {
            if (Decide(filter, DebugSource.DebugSourceApi, DebugType.DebugTypePerformance, i % 40) == GLDebugMessageDisposition.LogInfo)
            {
                Interlocked.Increment(ref logged);
            }
        });

        Assert.Equal(40, logged);
    }

    [Fact]
    public void The_Callback_Reports_Only_Faults_To_The_Sink_And_Logs_Information_Separately()
    {
        string source = File.ReadAllText(Path.Combine(RepoRoot(), "dream", "Rendering", "Core", "GLUtils.cs"));
        Match callback = Regex.Match(source, @"private static void OnDebugMessage\((?s:.*?)\r?\n    \}\r?\n");
        Assert.True(callback.Success, "OnDebugMessage not found.");
        string body = callback.Value;

        Assert.Contains("s_debugMessageFilter.Decide(source, type, id, severity)", body);
        Assert.Matches(
            @"if \(disposition == GLDebugMessageDisposition\.ReportFault\)\s*\{\s*sink\.Report\(DebugOutputFaultSource,",
            body);
        Assert.Single(Regex.Matches(body, @"sink\.Report\("));
        Assert.Contains("WriteDebugInfo(", body);
    }

    private static GLDebugMessageDisposition Decide(GLDebugMessageFilter filter, DebugSource source, DebugType type, int id) =>
        filter.Decide(source, type, id, DebugSeverity.DebugSeverityMedium);

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
