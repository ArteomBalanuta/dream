using OpenTK.Mathematics;

namespace Dream.Simulation.Players;


public interface IReplicatedPlayerTarget
{
    void SetWorldPosition(Vector3 position);

    void SetFacing(Vector3 facing);

    void SetHealth(int currentHealth, int maxHealth);
}
