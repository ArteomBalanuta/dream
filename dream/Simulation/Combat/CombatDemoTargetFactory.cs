using OpenTK.Mathematics;
using Dream.Simulation.Players;

namespace Dream.Simulation.Combat;


public static class CombatDemoTargetFactory
{
    public static CombatActorHandle CreateImpactActor(
        Camera camera,
        float distance = 2f,
        float verticalOffset = 0.15f)
    {
        Vector3 worldAnchor =
            camera.Position +
            (camera.Front * distance) +
            (Vector3.UnitY * verticalOffset);

        return new CombatActorHandle(
            Id: -1,
            Kind: CreatureKind.Object,
            Damageable: new HealthState(1),
            AnchorProvider: new FixedAnchorProvider(worldAnchor));
    }

    private sealed class FixedAnchorProvider : ICombatAnchorProvider
    {
        private readonly Vector3 _anchor;

        public FixedAnchorProvider(Vector3 anchor)
        {
            _anchor = anchor;
        }

        public Vector3 GetCombatAnchorWorldPosition()
        {
            return _anchor;
        }
    }
}
