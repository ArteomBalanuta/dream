public interface IPlayerCommandInbox
{
    void Enqueue(PlayerCommandFrame commandFrame);

    IReadOnlyList<PlayerCommandFrame> Drain(IEnumerable<PlayerSessionId> sessionIds);
}
