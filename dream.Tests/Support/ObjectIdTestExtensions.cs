using Dream.Simulation.World;

// Wave C0 shim for tests: while ObjectId is new ObjectId(actorId), tests read it back as the int id
// they used before. Removed by the wave C glue together with the production shims.
internal static class ObjectIdTestExtensions
{
    public static int AsInt(this ObjectId id) => checked((int)id.Value);
}
