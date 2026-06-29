using OpenTK.Mathematics;

public sealed record ClientCorrectionDelta(
    Vector3? CorrectedPosition,
    Vector3? CorrectedFacing);
