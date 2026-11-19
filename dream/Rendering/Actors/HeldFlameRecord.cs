using OpenTK.Mathematics;

namespace Dream.Rendering.Actors;

// The held light's flame in model space, as the last draw that posed one player recorded it. It outlives frames in
// which the player is not drawn (culled, model still loading), so the fire stays in the hand instead of jumping to a
// fixed fallback anchor. It is only valid for the session and the held items it was recorded for: a reused actor id
// (a new session) or a swapped item must not inherit it. Pure, no GL.
public sealed class HeldFlameRecord
{
    private bool _has;
    private Vector3 _point;
    private string? _sessionId;
    private string? _mainHand;
    private string? _offHand;

    // Replaces the record with what this draw posed; hasFlame false (no lit item in either hand) clears it.
    public void Record(string sessionId, in PlayerEquipmentVisuals equipment, bool hasFlame, Vector3 modelPoint)
    {
        _has = hasFlame;
        _point = hasFlame ? modelPoint : Vector3.Zero;
        _sessionId = sessionId;
        _mainHand = equipment.MainHand;
        _offHand = equipment.OffHand;
    }

    public bool TryGet(string sessionId, in PlayerEquipmentVisuals equipment, out Vector3 modelPoint)
    {
        if (_has &&
            string.Equals(_sessionId, sessionId, StringComparison.Ordinal) &&
            string.Equals(_mainHand, equipment.MainHand, StringComparison.Ordinal) &&
            string.Equals(_offHand, equipment.OffHand, StringComparison.Ordinal))
        {
            modelPoint = _point;
            return true;
        }

        modelPoint = Vector3.Zero;
        return false;
    }
}
