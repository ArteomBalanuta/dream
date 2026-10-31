using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Dream.Application;
using Dream.Client.Scenes;
using Dream.Client.Startup;
using Dream.Tests.Utilities;
using Xunit;

namespace Dream.Tests.Client.Scenes;

// PlayScene and Game need a GL context, so teardown is checked structurally: the order of the
// disposal steps, the per-step failure handling and that every owned disposable is released.
public class SceneTeardownTests
{
    // Fields PlayScene holds but does not own. Game owns the local server and disposes it last.
    private static readonly HashSet<string> PlaySceneNonOwningFields = ["_localServer"];

    [Fact]
    public void PlayScene_Dispose_Releases_Render_Then_ModelAssets_Before_The_Network()
    {
        string body = ExtractMethodBody(ReadSource("dream", "Client", "Scenes", "PlayScene.cs"), "public void Dispose()");

        int guard = IndexOf(body, "if (_disposed)");
        int render = IndexOf(body, "RunDisposeStep(_render.Dispose, ref failures);");
        int models = IndexOf(body, "RunDisposeStep(ModelAssets.DisposeAll, ref failures);");
        int combatFeedback = IndexOf(body, "RunDisposeStep(_combatFeedback.Dispose, ref failures);");
        int actionFeedback = IndexOf(body, "RunDisposeStep(_actionFeedback.Dispose, ref failures);");
        int runtime = IndexOf(body, "RunDisposeStep(_runtime.Dispose, ref failures);");
        int remoteClient = IndexOf(body, "RunDisposeStep(DisposeRemoteClient, ref failures);");

        Assert.True(guard < render, "The disposed guard must come before the first step.");
        Assert.True(render < models, "Render must be disposed before the shared model cache.");
        Assert.True(models < combatFeedback, "GL cleanup must come before the feedback systems.");
        Assert.True(combatFeedback < actionFeedback);
        Assert.True(actionFeedback < runtime);
        Assert.True(runtime < remoteClient, "The remote client must be disposed last.");
    }

    [Fact]
    public void PlayScene_Dispose_Collects_Step_Failures_And_Rethrows_At_The_End()
    {
        string source = ReadSource("dream", "Client", "Scenes", "PlayScene.cs");
        string dispose = ExtractMethodBody(source, "public void Dispose()");
        string runStep = ExtractMethodBody(source, "private static void RunDisposeStep(Action step, ref List<Exception>? failures)");
        string remoteClient = ExtractMethodBody(source, "private void DisposeRemoteClient()");

        Assert.Matches(@"try\s*\{\s*step\(\);\s*\}\s*catch\s*\(Exception ex\)\s*\{\s*\(failures \?\?= \[\]\)\.Add\(ex\);\s*\}", runStep);
        Assert.Contains("ExceptionDispatchInfo.Capture(failures[0]).Throw();", dispose);
        Assert.Contains("throw new AggregateException(failures);", dispose);
        Assert.True(
            IndexOf(dispose, "RunDisposeStep(DisposeRemoteClient, ref failures);") <
            IndexOf(dispose, "throw new AggregateException(failures);"));
        Assert.Contains("_remoteClient.DisposeAsync().AsTask().GetAwaiter().GetResult();", remoteClient);

        // Every step goes through RunDisposeStep; nothing is disposed outside the failure handling.
        foreach (Match call in Regex.Matches(dispose, @"[\w.]+\.(Dispose|DisposeAll|DisposeAsync)\b"))
        {
            int start = dispose.LastIndexOf('\n', call.Index) + 1;
            string line = dispose[start..dispose.IndexOf('\n', call.Index)];
            Assert.Contains("RunDisposeStep(", line);
        }
    }

    [Fact]
    public void PlayScene_Disposes_Every_Owned_Disposable_Field()
    {
        string source = ReadSource("dream", "Client", "Scenes", "PlayScene.cs");
        List<string> owned = [];
        List<string> missing = [];

        foreach (FieldInfo field in typeof(PlayScene).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            if (PlaySceneNonOwningFields.Contains(field.Name) || !IsDisposable(field.FieldType))
            {
                continue;
            }

            owned.Add(field.Name);
            if (!IsReleased(source, field.Name))
            {
                missing.Add($"{field.Name} ({field.FieldType.Name})");
            }
        }

        Assert.Empty(missing);
        // Guards the classification itself, so a reflection change cannot make the test vacuous.
        foreach (string expected in new[] { "_render", "_combatFeedback", "_actionFeedback", "_runtime", "_remoteClient" })
        {
            Assert.Contains(expected, owned);
        }
    }

    [Fact]
    public void StartupConnectionScene_Dispose_Is_Guarded()
    {
        Assert.True(typeof(IDisposable).IsAssignableFrom(typeof(StartupConnectionScene)));
        string body = ExtractMethodBody(ReadSource("dream", "Client", "Startup", "StartupConnectionScene.cs"), "public void Dispose()");

        Assert.True(IndexOf(body, "if (_disposed)") < IndexOf(body, "_disposed = true;"));
        Assert.True(IndexOf(body, "_disposed = true;") < IndexOf(body, "_renderer.Dispose();"));
    }

    [Fact]
    public void Game_OnUnload_Disposes_The_Scenes_Before_BaseOnUnload_And_The_Local_Server_Last()
    {
        Assert.True(typeof(Game).GetMethod("OnUnload", BindingFlags.Instance | BindingFlags.NonPublic, Type.EmptyTypes)?.DeclaringType == typeof(Game));
        string body = ExtractMethodBody(ReadSource("dream", "Application", "Game.cs"), "protected override void OnUnload()");

        int playScene = IndexOf(body, "_playScene?.Dispose();");
        int startupScene = IndexOf(body, "_startupScene?.Dispose();");
        int baseUnload = IndexOf(body, "base.OnUnload();");
        // Bounded by the persistence drain deadline plus busy_timeout, and it also waits for any
        // earlier integrated server still saving (the shutdown gate), so the final checkpoint is
        // not cut off; only a server hung past that is abandoned.
        int localServer = IndexOf(body, "PlaySessionEndTransition.StopLocalServerForExit(_localServer, _localServerShutdown)");
        Assert.DoesNotContain("GetAwaiter().GetResult()", body);

        Assert.True(playScene < baseUnload, "The play scene must be disposed while the GL context is current, before base.OnUnload.");
        Assert.True(startupScene < baseUnload, "The startup scene must be disposed before base.OnUnload.");
        Assert.True(playScene < localServer && startupScene < localServer, "The local server must outlive the scenes' connections.");
        Assert.True(baseUnload < localServer);
        // base.OnUnload may throw; the local server is still disposed, in its own finally.
        Assert.Matches(@"try\s*\{\s*base\.OnUnload\(\);\s*\}\s*catch\s*\(Exception ex\)\s*\{[^}]*\}\s*finally\s*\{\s*(//[^\n]*\s*)*if\s*\(PlaySessionEndTransition\.StopLocalServerForExit\(_localServer,\s*_localServerShutdown\)\s*is\s*Exception\s+stopFailure\)\s*\{\s*\(failures\s*\?\?=\s*\[\]\)\.Add\(stopFailure\);", body);
        // Failures are collected: one is rethrown as-is, several as an AggregateException.
        Assert.Contains("ExceptionDispatchInfo.Capture(failures[0]).Throw();", body);
        Assert.Contains("throw new AggregateException(failures);", body);
    }

    [Fact]
    public void PlayScene_RenderPerfLog_Has_No_Duplicate_PreDraw_Suffix()
    {
        string source = ReadSource("dream", "Client", "Scenes", "PlayScene.cs");
        Assert.DoesNotContain("preDraw=", source);
        Assert.Contains("performance.ToHudLines()", source);
    }

    [Fact]
    public void Game_Disposes_The_Startup_Scene_When_The_Play_Scene_Replaces_It()
    {
        string source = ReadSource("dream", "Application", "Game.cs");
        string update = ExtractMethodBody(source, "protected override void OnUpdateFrame(FrameEventArgs args)");

        int playScene = IndexOf(update, "_playScene = new PlayScene(");
        int dispose = IndexOf(update, "_startupScene.Dispose();");
        int clear = IndexOf(update, "_startupScene = null;");

        Assert.True(playScene < dispose && dispose < clear);
        // The startup scene is the only scene ever replaced; nothing else assigns either field.
        Assert.Single(Regex.Matches(source, @"_playScene\s*=\s*new\b"));
        Assert.Single(Regex.Matches(source, @"_startupScene\s*=\s*new\b"));
    }

    // NETR-2: when the play session ends, Game checks it after the scene's update, clears the
    // scene and swaps the local server handle before the teardown (so OnUnload cannot dispose
    // either again), tears down through PlaySessionEndTransition, then shows the connection
    // screen with the reason.
    [Fact]
    public void Game_Returns_To_The_Startup_Scene_When_The_Session_Ends()
    {
        string source = ReadSource("dream", "Application", "Game.cs");
        string update = ExtractMethodBody(source, "protected override void OnUpdateFrame(FrameEventArgs args)");
        Assert.True(
            IndexOf(update, "_playScene.Update(args, KeyboardState, MouseState);") <
            IndexOf(update, "ReturnToStartupIfSessionEnded();"));

        string switchBody = ExtractMethodBody(source, "private void ReturnToStartupIfSessionEnded()");
        int decision = IndexOf(switchBody, "PlaySessionEndTransition.HasEnded(_playScene, out string? reason)");
        int clearScene = IndexOf(switchBody, "_playScene = null;");
        int captureServer = IndexOf(switchBody, "ILocalServerHandle endedLocalServer = _localServer;");
        int replaceServer = IndexOf(switchBody, "_localServer = CreateLocalServerHandle();");
        // The ended server's stop is tracked in the shutdown gate, so the next one waits for it.
        int tearDown = IndexOf(switchBody, "PlaySessionEndTransition.TearDown(endedScene, endedLocalServer, shutdownGate: _localServerShutdown)");
        // Whether the session was on the integrated server is decided before its teardown.
        int relaunchTarget = IndexOf(switchBody, "RemoteClientOptions? relaunchFor = PlaySessionEndTransition.RelaunchTarget(endedLocalServer, endedOptions);");
        Assert.True(replaceServer < relaunchTarget && relaunchTarget < tearDown);
        int startup = IndexOf(switchBody, "ShowStartupScene(");
        int reasonArgument = IndexOf(switchBody, "sessionEndReason: reason");

        Assert.True(decision < clearScene, "The scene is left only once its session has ended.");
        Assert.True(clearScene < tearDown, "The scene field is cleared before the teardown, so OnUnload cannot dispose it again.");
        Assert.True(captureServer < replaceServer && replaceServer < tearDown, "The handle is replaced before the old one is stopped.");
        Assert.True(tearDown < startup && startup < reasonArgument, "The connection screen comes back after the teardown, with the reason.");
        // The teardown failures are reported, not thrown out of the frame.
        Assert.DoesNotContain("throw", switchBody);
        // W9-4: returning to the menu starts no integrated server and keeps the endpoint as it
        // was; only a session to this game's own integrated server relaunches one, on connect.
        Assert.Contains("PlaySessionEndTransition.ReturnOptions(endedOptions)", switchBody);
        Assert.Contains("resolveLocalPort: false", switchBody);
        Assert.DoesNotContain("resolveLocalPort: true", switchBody);
        Assert.DoesNotContain("EnsureStarted", switchBody);
        Assert.Contains("relaunchLocalServerFor: relaunchFor", switchBody);
        // A handle that was merely started (a loopback config starts it at launch) no longer
        // decides the relaunch.
        Assert.DoesNotContain("stoppedLocalServer ? endedOptions", switchBody);
        // Every new integrated server starts after the stops the gate tracks.
        Assert.Contains("startAfter: () => _localServerShutdown.Pending", ExtractMethodBody(source, "private ILocalServerHandle CreateLocalServerHandle()"));

        // Only ShowStartupScene creates a startup scene, at launch and after a session ends.
        string show = ExtractMethodBody(source, "private void ShowStartupScene(");
        Assert.Contains("_startupScene = new StartupConnectionScene(", show);
        Assert.Contains("sessionEndReason);", show);
        Assert.Contains("IRemoteSessionConnector connector = new RemoteSessionConnector();", show);
        Assert.Matches(@"if \(relaunchLocalServerFor != null\)\s*\{\s*connector = new LocalServerRelaunchConnector\(\s*connector,\s*_localServer,", show);
        Assert.Contains("NetworkSessionCoordinator coordinator = new(_localServer, connector, _contentError);", show);
    }

    [Fact]
    public void StartupConnectionScene_Shows_The_Session_End_Reason_And_Does_Not_Auto_Reconnect()
    {
        string source = ReadSource("dream", "Client", "Startup", "StartupConnectionScene.cs");
        string constructor = ExtractMethodBody(source, "public StartupConnectionScene(");

        Assert.Contains("_autoConnectAttempted = sessionEndReason != null;", constructor);
        Assert.Contains("_coordinator.ReportSessionEnded(sessionEndReason);", constructor);
    }

    private static bool IsDisposable(Type type) =>
        typeof(IDisposable).IsAssignableFrom(type) || typeof(IAsyncDisposable).IsAssignableFrom(type);

    // A direct call or a method group passed to a disposal step; DisposeAsync counts.
    private static bool IsReleased(string source, string fieldName)
    {
        string name = Regex.Escape(fieldName);
        return Regex.IsMatch(source, $@"(?<![\w.]){name}\??\.(Dispose|DisposeAll|DisposeAsync)(\(\)|(?=\s*[,)]))");
    }

    private static int IndexOf(string text, string value)
    {
        int index = text.IndexOf(value, StringComparison.Ordinal);
        Assert.True(index >= 0, $"Expected to find '{value}'.");
        return index;
    }

    // The text between the signature's opening brace and its matching closing brace.
    private static string ExtractMethodBody(string source, string signature)
    {
        int start = IndexOf(source, signature);
        int open = source.IndexOf('{', start);
        int depth = 0;
        for (int index = open; index < source.Length; index++)
        {
            if (source[index] == '{')
            {
                depth++;
            }
            else if (source[index] == '}' && --depth == 0)
            {
                return source[(open + 1)..index].Replace("\r\n", "\n");
            }
        }

        throw new InvalidOperationException($"Unbalanced braces after '{signature}'.");
    }

    private static string ReadSource(params string[] relativePath)
    {
        return PartialClassSource.Read(Path.Combine([FindRepoRoot(), .. relativePath]));
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "dream")) &&
                Directory.Exists(Path.Combine(current.FullName, "dream.Tests")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
