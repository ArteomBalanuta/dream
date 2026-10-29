namespace Dream.Multiplayer.Protocol;

// Protocol v8: announces the stored terrain that follows a join-accepted carrier as
// server.terrain-chunk frames. Wire form in "terrain": {"chunks":49,"fragments":5,"bytes":215996,
// "sha256":"<64 lowercase hex>"}. Bytes is the sum of the chunk blob lengths; Sha256 is
// TerrainTransferDigest over the chunks in (chunk_z, chunk_x) order.
public sealed record TerrainTransferManifest(int Chunks, int Fragments, long Bytes, string Sha256);
