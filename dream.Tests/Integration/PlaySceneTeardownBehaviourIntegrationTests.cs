using System.Reflection;
using System.Runtime.CompilerServices;
using OpenTK.Mathematics;
using Xunit;
using Dream.Client.Networking;
using Dream.Client.Scenes;
using Dream.Client.World;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Transport;
using Dream.Rendering.Core;
using Dream.Rendering.Hud;
using Dream.Simulation.Runtime;

namespace Dream.Tests.Integration;

// GFX-4 with GFX-3: PlayScene.Dispose runs its steps in a fixed order (Render, the shared model
// cache, combat feedback, action feedback, the client world, the network client), every step runs
// even when an earlier one throws, one failure is rethrown as is and several as an
// AggregateException in step order, and a second Dispose does nothing. SceneTeardownTests checks
// this structurally from the source; this checks the behaviour.
//
// PlayScene builds its Render (GL) in the constructor and takes no injectable disposables, so there
// is no public seam: the scene is created without its constructor and its owned fields are set to
// headless collaborators. Faults are injected through real objects only: a Render or
// CombatFeedbackSystem created without a constructor fails inside its own Dispose (a null field),
// and the network client fails through its ITransportConnection, the one dependency PlayScene's
// collaborators accept from outside.
public class PlaySceneTeardownBehaviourIntegrationTests
{
    [Fact]
    public void Dispose_FirstStepThrows_LaterStepsStillRun_TheSingleFailureIsRethrownAsIs_SecondDisposeIsANoOp()
    {
        RecordingConnection connection = new(failOnDispose: null);
        PlayScene scene = CreateScene(
            render: BrokenRender(),
            combatFeedback: WorkingCombatFeedback(),
            connection);

        // Render.Dispose binds its first step to a null renderer (ArgumentException from the delegate).
        ArgumentException failure = Assert.Throws<ArgumentException>(scene.Dispose);

        Assert.Contains("Dream.Rendering.Core.Render.Dispose", failure.StackTrace);
        // The network client is the last step; it ran although Render's step failed first.
        Assert.Equal(1, connection.DisposeCalls);

        scene.Dispose();
        Assert.Equal(1, connection.DisposeCalls);
    }

    [Fact]
    public void Dispose_OnlyTheLastStepThrows_ThatExceptionInstanceIsRethrown()
    {
        InvalidOperationException networkFailure = new("Simulated transport close failure.");
        RecordingConnection connection = new(networkFailure);
        PlayScene scene = CreateScene(
            render: AlreadyDisposedRender(),
            combatFeedback: WorkingCombatFeedback(),
            connection);

        Exception thrown = Assert.Throws<InvalidOperationException>(scene.Dispose);

        Assert.Same(networkFailure, thrown);
        Assert.Equal(1, connection.DisposeCalls);
    }

    [Fact]
    public void Dispose_SeveralStepsThrow_AggregatesTheFailuresInStepOrder_RenderFirst_NetworkLast()
    {
        InvalidOperationException networkFailure = new("Simulated transport close failure.");
        RecordingConnection connection = new(networkFailure);
        PlayScene scene = CreateScene(
            render: BrokenRender(),
            combatFeedback: BrokenCombatFeedback(),
            connection);

        AggregateException aggregate = Assert.Throws<AggregateException>(scene.Dispose);

        Assert.Equal(3, aggregate.InnerExceptions.Count);
        Assert.Contains("Dream.Rendering.Core.Render.Dispose", aggregate.InnerExceptions[0].StackTrace);
        Assert.Contains("Dream.Rendering.Hud.CombatFeedbackSystem.Dispose", aggregate.InnerExceptions[1].StackTrace);
        Assert.Same(networkFailure, aggregate.InnerExceptions[2]);

        // Idempotent after a failed teardown too: nothing runs or throws again.
        scene.Dispose();
        Assert.Equal(1, connection.DisposeCalls);
    }

    private static PlayScene CreateScene(Render render, CombatFeedbackSystem combatFeedback, RecordingConnection connection)
    {
        SceneEventHub events = new();
        PlayScene scene = (PlayScene)RuntimeHelpers.GetUninitializedObject(typeof(PlayScene));
        SetField(scene, "_render", render);
        SetField(scene, "_combatFeedback", combatFeedback);
        SetField(scene, "_actionFeedback", new AuthoritativeActionFeedbackSystem(events));
        SetField(scene, "_runtime", new ClientWorldSession(8, new Vector3(4f, 1.5f, 4f), events));
        SetField(scene, "_remoteClient", new RemoteClientSession(
            connection,
            new TransportJsonCodec(),
            new LengthPrefixedMessageFramer(),
            new RemoteClientOptions("127.0.0.1", 1, "teardown-session", "Teardown", "dream-tests")));
        return scene;
    }

    // Fails inside its own Dispose: every renderer field is null.
    private static Render BrokenRender()
    {
        return (Render)RuntimeHelpers.GetUninitializedObject(typeof(Render));
    }

    // Its own guard makes Dispose a no-op, so the step succeeds without GL.
    private static Render AlreadyDisposedRender()
    {
        Render render = BrokenRender();
        SetField(render, "_disposed", true);
        return render;
    }

    private static CombatFeedbackSystem WorkingCombatFeedback()
    {
        return new CombatFeedbackSystem(new SceneEventHub(), new FloatingCombatTextSystem());
    }

    // Fails inside its own Dispose: its subscription is null.
    private static CombatFeedbackSystem BrokenCombatFeedback()
    {
        return (CombatFeedbackSystem)RuntimeHelpers.GetUninitializedObject(typeof(CombatFeedbackSystem));
    }

    private static void SetField(object target, string name, object value)
    {
        FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"{target.GetType().Name}.{name} not found; the teardown seam moved.");
        field.SetValue(target, value);
    }

    private sealed class RecordingConnection(Exception? failOnDispose) : ITransportConnection
    {
        private int _disposeCalls;

        public int DisposeCalls => Volatile.Read(ref _disposeCalls);

        public string ConnectionId => "teardown";

        public ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromResult(0);

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCalls);
            return failOnDispose == null ? ValueTask.CompletedTask : ValueTask.FromException(failOnDispose);
        }
    }
}
