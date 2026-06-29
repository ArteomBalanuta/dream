using OpenTK.Mathematics;

public readonly record struct WorldActorLabelState(
    Vector3 WorldAnchor,
    string DisplayName,
    string HealthLabel);
