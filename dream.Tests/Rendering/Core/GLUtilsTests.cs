using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using OpenTK.Graphics.OpenGL4;
using Xunit;
using Dream.Rendering.Core;

namespace Dream.Tests.Rendering.Core;

public class GLUtilsTests
{
    [Fact]
    public void Debug_Callback_Is_A_Rooted_Static_Delegate_To_A_Static_Method()
    {
        FieldInfo[] callbackFields = typeof(GLUtils)
            .GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .Where(field => field.FieldType == typeof(DebugProc))
            .ToArray();

        FieldInfo field = Assert.Single(callbackFields);
        Assert.True(field.IsStatic);
        Assert.True(field.IsInitOnly, "The callback must be assigned once, so the driver's pointer stays valid.");

        DebugProc? callback = (DebugProc?)field.GetValue(null);
        Assert.NotNull(callback);
        Assert.Same(callback, field.GetValue(null));
        Assert.Null(callback!.Target);
        Assert.True(callback.Method.IsStatic);
        Assert.DoesNotContain('<', callback.Method.Name);
        Assert.Null(callback.Method.GetCustomAttribute<CompilerGeneratedAttribute>());
        Assert.Equal(typeof(GLUtils), callback.Method.DeclaringType);
    }

    [Fact]
    public void DebugMessageCallback_Is_Only_Passed_The_Static_Field()
    {
        string source = File.ReadAllText(Path.Combine(RepoRoot(), "dream", "Rendering", "Core", "GLUtils.cs"));
        FieldInfo field = typeof(GLUtils)
            .GetFields(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(candidate => candidate.FieldType == typeof(DebugProc));

        MatchCollection calls = Regex.Matches(source, @"GL\.DebugMessageCallback\(\s*([^,]+),");
        Match call = Assert.Single(calls);
        Assert.Equal(field.Name, call.Groups[1].Value.Trim());
    }

    [Theory]
    [InlineData(ErrorCode.NoError, GLFaultKind.None)]
    [InlineData(ErrorCode.InvalidEnum, GLFaultKind.Recoverable)]
    [InlineData(ErrorCode.InvalidValue, GLFaultKind.Recoverable)]
    [InlineData(ErrorCode.InvalidOperation, GLFaultKind.Recoverable)]
    [InlineData(ErrorCode.InvalidFramebufferOperation, GLFaultKind.Recoverable)]
    [InlineData(ErrorCode.OutOfMemory, GLFaultKind.Fatal)]
    [InlineData(ErrorCode.ContextLost, GLFaultKind.Fatal)]
    public void Classify_Marks_Out_Of_Memory_And_Context_Lost_Fatal(ErrorCode error, GLFaultKind expected)
    {
        Assert.Equal(expected, GLUtils.Classify(error));
    }

    [Fact]
    public void CheckGLError_Drains_Every_Error_And_Returns_The_Worst()
    {
        Queue<ErrorCode> errors = new([ErrorCode.InvalidEnum, ErrorCode.OutOfMemory, ErrorCode.InvalidValue, ErrorCode.NoError]);
        RecordingSink sink = new();

        GLFaultKind kind = GLUtils.CheckGLError("test frame", () => errors.Dequeue(), sink);

        Assert.Equal(GLFaultKind.Fatal, kind);
        Assert.Empty(errors);
        Assert.Equal(3, sink.Reports.Count);
        Assert.All(sink.Reports, report => Assert.Equal(GLUtils.ErrorFaultSource, report.Source));
        GLErrorException oom = Assert.IsType<GLErrorException>(sink.Reports[1].Exception);
        Assert.Equal(ErrorCode.OutOfMemory, oom.Error);
        Assert.Equal("test frame", oom.Context);
    }

    [Fact]
    public void CheckGLError_Reports_Nothing_When_There_Is_No_Error()
    {
        RecordingSink sink = new();

        Assert.Equal(GLFaultKind.None, GLUtils.CheckGLError("idle", () => ErrorCode.NoError, sink));
        Assert.Empty(sink.Reports);
    }

    [Fact]
    public void CheckGLError_Stops_On_A_Context_That_Reports_Lost_Forever()
    {
        RecordingSink sink = new();
        int calls = 0;

        GLFaultKind kind = GLUtils.CheckGLError("lost", () =>
        {
            calls++;
            return ErrorCode.ContextLost;
        }, sink);

        Assert.Equal(GLFaultKind.Fatal, kind);
        Assert.Equal(GLUtils.MaxErrorsDrainedPerCheck, calls);
    }

    [Theory]
    [InlineData(DebugSeverity.DebugSeverityHigh, true)]
    [InlineData(DebugSeverity.DebugSeverityMedium, true)]
    [InlineData(DebugSeverity.DebugSeverityLow, false)]
    [InlineData(DebugSeverity.DebugSeverityNotification, false)]
    public void Debug_Output_Keeps_Only_High_And_Medium_Severity(DebugSeverity severity, bool expected)
    {
        Assert.Equal(expected, GLUtils.ShouldReportDebugMessage(severity));
    }

    [Fact]
    public void Game_Requests_A_Debug_Context_And_Checks_Errors_Only_Under_Debug()
    {
        string source = File.ReadAllText(Path.Combine(RepoRoot(), "dream", "Application", "Game.cs"));

        Assert.Matches(@"#if DEBUG\s+//[^\n]*\n\s*Flags = ContextFlags\.Debug,\s*#endif", source);
        Assert.Matches(@"#if DEBUG\s+bool installed = GLUtils\.InstallDebugOutput\(RenderFaultLog\.Shared\);", source);
        Assert.Matches(@"#if DEBUG\s+internal const int GLErrorCheckIntervalFrames = 1;", source);
        Assert.Contains("GLUtils.CheckGLError(\"frame end\") != GLFaultKind.Fatal", source);
        Assert.Contains("Close();", source);
    }

    private sealed class RecordingSink : IRenderFaultSink
    {
        public List<(string Source, Exception Exception)> Reports { get; } = [];

        public void Report(string source, Exception exception) => Reports.Add((source, exception));
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
