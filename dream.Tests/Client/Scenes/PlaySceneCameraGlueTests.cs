using System.IO;
using Dream.Client.CameraControl;
using Dream.Client.Scenes;
using Dream.Tests.Utilities;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Client.Scenes;

// A PlayScene needs a window, a runtime and a render, so the glue is tested in the pieces it is made of: the
// confine applier (the decision and the OS calls, on a fake confiner), the policy inputs the scene feeds it, the
// zoom-anchor plane fallback, and the source shape of the Y write-back and the host's clip rectangle.
public class PlaySceneCameraGlueTests
{
    private sealed class FakeConfiner : ICursorConfiner
    {
        public bool IsConfined { get; set; }

        public int Confines { get; private set; }

        public int Releases { get; private set; }

        public void Confine(Box2i clientRectScreen)
        {
            Confines++;
            IsConfined = true;
        }

        public void Release()
        {
            Releases++;
            IsConfined = false;
        }
    }

    private static readonly Box2i Rect = new(100, 50, 1540, 1074);

    [Fact]
    public void Confines_Once_When_Wanted_And_Stays_Quiet_While_The_Clip_Holds()
    {
        FakeConfiner fake = new();
        CameraConfineApplier applier = new();

        applier.Apply(fake, true, true, Rect);
        applier.Apply(fake, true, true, Rect);
        applier.Apply(fake, true, true, Rect);

        Assert.Equal(1, fake.Confines);
        Assert.Equal(0, fake.Releases);
    }

    [Fact]
    public void Reapplies_When_The_Os_Clip_Was_Lost()
    {
        FakeConfiner fake = new();
        CameraConfineApplier applier = new();
        applier.Apply(fake, true, true, Rect);

        fake.IsConfined = false;
        applier.Apply(fake, true, true, Rect);

        Assert.Equal(2, fake.Confines);
    }

    [Fact]
    public void Reapplies_When_The_Rectangle_Moves()
    {
        FakeConfiner fake = new();
        CameraConfineApplier applier = new();
        applier.Apply(fake, true, true, Rect);

        applier.Apply(fake, true, true, new Box2i(0, 0, 800, 600));

        Assert.Equal(2, fake.Confines);
    }

    [Fact]
    public void Releases_Once_When_No_Longer_Wanted_And_Never_Before_A_Clip_Was_Applied()
    {
        FakeConfiner fake = new();
        CameraConfineApplier applier = new();
        applier.Apply(fake, false, true, Rect);
        Assert.Equal(0, fake.Releases);

        applier.Apply(fake, true, true, Rect);
        applier.Apply(fake, false, true, Rect);
        applier.Apply(fake, false, true, Rect);

        Assert.Equal(1, fake.Releases);
    }

    [Fact]
    public void Reapplies_On_Every_Focus_Gained_Edge_Even_If_The_Confiner_Claims_The_Clip_Holds()
    {
        FakeConfiner fake = new();
        CameraConfineApplier applier = new();
        applier.Apply(fake, true, true, Rect);

        // Focus lost: the policy says release. Focus regained: confine again, whatever IsConfined says.
        applier.Apply(fake, false, false, Rect);
        fake.IsConfined = true;
        applier.Apply(fake, true, true, Rect);

        Assert.Equal(2, fake.Confines);
        Assert.Equal(1, fake.Releases);
    }

    [Fact]
    public void Reset_Forgets_The_Applied_Clip_So_The_Next_Want_Confines_Again()
    {
        FakeConfiner fake = new();
        CameraConfineApplier applier = new();
        applier.Apply(fake, true, true, Rect);

        applier.Reset();
        applier.Apply(fake, true, true, Rect);

        Assert.Equal(2, fake.Confines);
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, false, true)]
    public void Each_Blocker_Makes_The_Scene_Release(bool pauseOrSettings, bool loading, bool minimised, bool focusLost)
    {
        // The scene computes want from these exact blockers; drive the applier with the policy's answer.
        FakeConfiner fake = new();
        CameraConfineApplier applier = new();
        applier.Apply(fake, true, true, Rect);

        bool focused = !focusLost;
        bool want = CursorConfinePolicy.ShouldConfine(CameraConfineMode.Always, focused, minimised, true, pauseOrSettings, loading, true);
        applier.Apply(fake, want, focused, Rect);

        bool anyBlocker = pauseOrSettings || loading || minimised || focusLost;
        Assert.Equal(anyBlocker ? 1 : 0, fake.Releases);
    }

    [Fact]
    public void The_Scene_Feeds_The_Policy_Every_Blocker_And_The_Frames_Own_Focus()
    {
        string source = PartialClassSource.Read(Path.Combine(RepoRoot(), "dream", "Client", "Scenes", "PlayScene.Camera.cs"));
        string apply = Method(source, "public void ApplyCursorConfinement(");

        Assert.Contains("_cameraWindowFocused,", apply);
        Assert.DoesNotContain("_windowFocused", apply);
        Assert.Contains("_settings.PauseMenuVisible || _settings.SettingsVisible", apply);
        Assert.Contains("LoadingOverlayVisible", apply);
        Assert.Contains("minimised", apply);
        Assert.Contains("_cameraWindowFocused = focused;", Method(source, "public void SyncCameraWindowState("));
        Assert.Contains("_cameraConfiner?.Release();", Method(source, "public void ReleaseCameraResources("));
        Assert.Contains("_cameraConfine?.Reset();", Method(source, "public void ReleaseCameraResources("));

        // The scene's Dispose lets the cursor go first, outside the counted teardown steps.
        string lifetime = PartialClassSource.Read(Path.Combine(RepoRoot(), "dream", "Client", "Scenes", "PlayScene.Lifetime.cs"));
        string dispose = Method(lifetime, "public void Dispose(");
        Assert.True(dispose.IndexOf("ReleaseCameraResources();", StringComparison.Ordinal)
            < dispose.IndexOf("RunDisposeStep(_render.Dispose", StringComparison.Ordinal));
        Assert.DoesNotContain("RunDisposeStep(ReleaseCameraResources", dispose);
    }

    [Fact]
    public void The_Y_Toggle_Is_Written_Back_To_The_Settings_And_Not_Reapplied()
    {
        string source = PartialClassSource.Read(Path.Combine(RepoRoot(), "dream", "Client", "Scenes", "PlayScene.Camera.cs"));
        string advance = Method(source, "private TacticalCameraPose AdvanceCamera(");
        string sync = Method(source, "private void SyncAnchorModeToSettings(");

        Assert.True(advance.IndexOf("rig.Advance(", StringComparison.Ordinal) < advance.IndexOf("SyncAnchorModeToSettings(rig)", StringComparison.Ordinal));
        Assert.Contains("rig.AnchorMode == _settings.TacticalCamera.AnchorMode", sync);
        Assert.Contains("_settings.SetTacticalCamera(_settings.TacticalCamera with { AnchorMode = rig.AnchorMode });", sync);
        Assert.Contains("_cameraSettingsApplied = _settings.TacticalCamera;", sync);
    }

    [Fact]
    public void The_Host_Clips_To_The_Client_Area_And_Releases_Before_The_Scene_Disposes()
    {
        string game = PartialClassSource.Read(Path.Combine(RepoRoot(), "dream", "Application", "Game.cs"));
        string sync = Method(game, "private unsafe void SyncPlayCameraWindow(");
        string unload = Method(game, "protected override void OnUnload(");

        Assert.Contains("new(ClientLocation, ClientLocation + ClientSize)", sync);
        Assert.DoesNotContain("new(Location", sync);
        Assert.True(unload.IndexOf("ReleaseCameraClipGuarded();", StringComparison.Ordinal) < unload.IndexOf("_playScene?.Dispose();", StringComparison.Ordinal));
        Assert.Contains("ReleaseCameraClipGuarded();", Method(game, "protected override void Dispose(bool disposing)"));
        Assert.Contains("catch (Exception", Method(game, "private void ReleaseCameraClipGuarded("));
    }

    [Fact]
    public void Zoom_Anchor_Fallback_Cuts_The_Ray_With_The_Plane_Through_The_View_Target()
    {
        Assert.True(PlayScene.TryIntersectPlaneY(new Vector3(0f, 10f, 0f), Vector3.Normalize(new Vector3(1f, -1f, 0f)), 2f, out Vector3 hit));
        Assert.Equal(2f, hit.Y, 3);
        Assert.Equal(8f, hit.X, 3);
        // Parallel to the plane, and pointing away from it: no anchor.
        Assert.False(PlayScene.TryIntersectPlaneY(new Vector3(0f, 10f, 0f), Vector3.UnitX, 2f, out _));
        Assert.False(PlayScene.TryIntersectPlaneY(new Vector3(0f, 10f, 0f), Vector3.UnitY, 2f, out _));
    }

    private static string Method(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature);
        int open = source.IndexOf('{', start);
        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}' && --depth == 0)
            {
                return source[start..(i + 1)];
            }
        }

        throw new InvalidOperationException(signature);
    }

    private static string RepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "dream", "dream.csproj")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("repository root");
    }
}
