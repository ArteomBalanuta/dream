using OpenTK.Mathematics;

public readonly record struct ViewModelResolvedPose(
    float FieldOfViewDegrees,
    Vector3 PositionOffset,
    Vector3 RotationOffsetDegrees,
    float Scale);
