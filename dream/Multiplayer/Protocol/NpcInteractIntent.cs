namespace Dream.Multiplayer.Protocol;

// M3: talk to an NPC. The server checks that the actor is an NPC and in range, and answers with
// VendorOpenedResult.
public sealed record NpcInteractIntent(int NpcActorId) : IPlayerActionPayload;
