using OpenTK.Mathematics;

namespace Dream.Simulation.Combat;


public interface ICombatAnchorProvider
{
    Vector3 GetCombatAnchorWorldPosition();
}
