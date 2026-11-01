using Dream.Simulation.Players;
using Dream.Simulation.Runtime;

namespace Dream.Client.Presentation;

// Counts the local player's respawn wait for the death overlay (wave I). Display only: the server
// owns the timer and the respawn itself, and nothing here is ever sent. The wait runs from the tick
// of the first snapshot that showed the player dead, so it can be off by a snapshot.
//
// A snapshot older than the newest one observed is ignored, so a stale pre-respawn snapshot that
// arrives late cannot reopen the overlay. Reset starts a new timeline (a join). Allocates nothing.
public sealed class ClientDeathTracker
{
    // The server's wait.
    public const int RespawnDelayTicks = PlayerRespawnRules.DelayTicks;

    private bool _hasObservation;
    private ulong _newestTick;
    private bool _isDead;
    private ulong _deathTick;

    public bool IsDead => _isDead;

    public void Observe(bool isAlive, ulong serverTick)
    {
        if (_hasObservation && serverTick < _newestTick)
        {
            return;
        }

        _hasObservation = true;
        _newestTick = serverTick;
        if (isAlive)
        {
            _isDead = false;
            return;
        }

        if (!_isDead)
        {
            _isDead = true;
            _deathTick = serverTick;
        }
    }

    public void Reset()
    {
        _hasObservation = false;
        _newestTick = 0;
        _isDead = false;
        _deathTick = 0;
    }

    // Whole seconds left, rounded up, never below 0. 0 also means "dead, wait is over, waiting for
    // the respawn snapshot". Zero when not dead.
    public int SecondsRemaining(double estimatedServerTick)
    {
        if (!_isDead)
        {
            return 0;
        }

        double remainingTicks = _deathTick + (double)RespawnDelayTicks - estimatedServerTick;
        if (!(remainingTicks > 0d))
        {
            return 0;
        }

        // Clamped as a double first: an infinite hostile estimate must not reach the int cast.
        double seconds = Math.Ceiling(remainingTicks / SimulationClock.AuthoritativeTicksPerSecond);
        return (int)Math.Min(seconds, RespawnDelayTicks / SimulationClock.AuthoritativeTicksPerSecond);
    }
}
