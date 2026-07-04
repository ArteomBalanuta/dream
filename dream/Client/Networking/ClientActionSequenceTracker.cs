using Dream.Multiplayer.Protocol;

namespace Dream.Client.Networking;

public sealed class ClientActionSequenceTracker
{
    private ulong _nextActionSequence = 1;

    public ulong NextActionSequence()
    {
        return _nextActionSequence++;
    }

    public void ObserveCommandSequence(ulong commandSequence)
    {
        AdvancePast(commandSequence);
    }

    public void ObserveActionResult(AuthoritativeActionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        AdvancePast(result.ActionSequence);
    }

    private void AdvancePast(ulong sequence)
    {
        if (_nextActionSequence <= sequence)
        {
            _nextActionSequence = sequence == ulong.MaxValue
                ? ulong.MaxValue
                : sequence + 1;
        }
    }
}
