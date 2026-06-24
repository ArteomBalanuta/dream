using OpenTK.Mathematics;

public readonly record struct MovementCommand(Vector3 WorldDirection, bool JumpPressed, bool FreezePressed);

public readonly record struct PlayerInteractionCommand(bool PlaceCubePressed);
