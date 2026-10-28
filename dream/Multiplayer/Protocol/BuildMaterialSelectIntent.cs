using Dream.Simulation.World.Geometry;

namespace Dream.Multiplayer.Protocol;

// The building menu's choice ("build.material"), sent by the client. Null means Auto (the
// supporting cell's material). The server keeps it per session, not persisted and not
// checksummed, and applies it in tick order with the session's other actions; later builds by
// that session place the selected material (WorldSessionRuntime.BuildMaterial.cs).
public sealed record BuildMaterialSelectIntent(GeometryMaterialId? MaterialId) : IPlayerActionPayload;
