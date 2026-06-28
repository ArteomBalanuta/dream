using OpenTK.Mathematics;
using Xunit;

public class CombatFeedbackSystemTests
{
    [Fact]
    public void Damage_Event_Spawns_Floating_Text_Through_Hud_Feedback_Path()
    {
        SceneEventHub hub = new();
        FloatingCombatTextSystem floatingText = new();
        CombatFeedbackSystem system = new(hub, floatingText);
        CombatActorHandle actor = new(
            Id: 7,
            Kind: CreatureKind.Player,
            Damageable: new HealthState(100),
            AnchorProvider: new FixedAnchorProvider(new Vector3(2f, 3f, 4f)));

        hub.Publish(new DamageAppliedEvent(actor, 24, FloatingCombatTextKind.Regular));

        List<FloatingCombatTextRenderState> states = new();
        floatingText.CollectRenderStates(states);

        Assert.Single(states);
        Assert.Equal("24", states[0].Text);
        Assert.Equal(new Vector3(2f, 3f, 4f), states[0].WorldAnchor);
    }

    [Fact]
    public void Non_Positive_Damage_Does_Not_Spawn_Floating_Text()
    {
        SceneEventHub hub = new();
        FloatingCombatTextSystem floatingText = new();
        CombatFeedbackSystem system = new(hub, floatingText);
        CombatActorHandle actor = new(
            Id: 3,
            Kind: CreatureKind.Object,
            Damageable: new HealthState(50),
            AnchorProvider: new FixedAnchorProvider(Vector3.One));

        hub.Publish(new DamageAppliedEvent(actor, 0, FloatingCombatTextKind.Regular));

        List<FloatingCombatTextRenderState> states = new();
        floatingText.CollectRenderStates(states);

        Assert.Empty(states);
    }

    [Fact]
    public void Disposed_System_No_Longer_Reacts_To_Damage_Events()
    {
        SceneEventHub hub = new();
        FloatingCombatTextSystem floatingText = new();
        CombatFeedbackSystem system = new(hub, floatingText);
        CombatActorHandle actor = new(
            Id: 1,
            Kind: CreatureKind.Player,
            Damageable: new HealthState(100),
            AnchorProvider: new FixedAnchorProvider(Vector3.Zero));

        system.Dispose();
        hub.Publish(new DamageAppliedEvent(actor, 9, FloatingCombatTextKind.Regular));

        List<FloatingCombatTextRenderState> states = new();
        floatingText.CollectRenderStates(states);

        Assert.Empty(states);
    }

    private sealed class FixedAnchorProvider : ICombatAnchorProvider
    {
        private readonly Vector3 _anchor;

        public FixedAnchorProvider(Vector3 anchor)
        {
            _anchor = anchor;
        }

        public Vector3 GetCombatAnchorWorldPosition() => _anchor;
    }
}
