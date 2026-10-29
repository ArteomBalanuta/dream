namespace Dream.Simulation.World.Terrain;

// A stored world's terrain blobs failed to decode when the server built the map (a corrupt body, an
// exceeded decode budget). ServerBootstrap catches this type, and only this type, to say "the stored
// terrain is invalid"; any other InvalidDataException at startup gets a neutral exit-5 message.
public sealed class StoredTerrainInvalidException(string message, Exception innerException)
    : Exception(message, innerException)
{
}
