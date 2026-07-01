using OpenTK.Mathematics;

namespace Dream.Multiplayer.Protocol;


public sealed record ClientCorrectionDelta(
    Vector3? CorrectedPosition,
    Vector3? CorrectedFacing);
