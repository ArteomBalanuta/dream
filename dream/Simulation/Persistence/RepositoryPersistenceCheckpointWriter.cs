using Dream.Simulation.World;

namespace Dream.Simulation.Persistence;

// Fallback writer for repositories without a shared SQLite connection (for example in-memory
// test repositories). It follows the same update-only rules and the same capture-time guard on
// player rows through the repository interfaces.
public sealed class RepositoryPersistenceCheckpointWriter : IPersistenceCheckpointWriter
{
    private readonly IPlayerProfileRepository? _playerProfiles;
    private readonly IWorldPersistenceRepository _world;

    public RepositoryPersistenceCheckpointWriter(
        IPlayerProfileRepository? playerProfiles,
        IWorldPersistenceRepository world)
    {
        _playerProfiles = playerProfiles;
        _world = world ?? throw new ArgumentNullException(nameof(world));
    }

    public void Write(PersistenceCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);

        if (_playerProfiles != null)
        {
            foreach (PlayerCheckpointVitals vitals in checkpoint.PlayerVitals)
            {
                if (_playerProfiles.TryGet(vitals.PlayerName) is not { } profile)
                {
                    continue;
                }

                // The capture-time guard (see IPersistenceCheckpointWriter): a world state stored
                // after the capture means the player left after it, so the profile the leave saved
                // stands. Default CapturedUtc is unguarded.
                if (vitals.CapturedUtc != default &&
                    _world.TryGetPlayerWorldState(vitals.PlayerName) is { } left &&
                    left.UpdatedUtc > vitals.CapturedUtc)
                {
                    continue;
                }

                _playerProfiles.Save(profile with
                {
                    InteractionMode = vitals.InteractionMode,
                    Stats = profile.Stats with
                    {
                        CurrentHealth = vitals.CurrentHealth,
                        MaxHealth = vitals.MaxHealth
                    }
                });
            }
        }

        foreach (PersistedPlayerWorldState state in checkpoint.PlayerWorldStates)
        {
            // A stored state saved at or after this capture (a leave) is newer: keep it.
            if (_world.TryGetPlayerWorldState(state.PlayerName) is { } stored && stored.UpdatedUtc >= state.UpdatedUtc)
            {
                continue;
            }

            _world.SavePlayerWorldState(state);
        }

        // An incremental checkpoint often holds no rows of a kind; skip that table's load then.
        HashSet<ObjectId> enemyIds = checkpoint.Enemies.Count == 0
            ? []
            : _world.LoadActiveEnemies().Select(enemy => enemy.ObjectId).ToHashSet();
        foreach (PersistedEnemyInstance enemy in checkpoint.Enemies.Where(enemy => enemyIds.Contains(enemy.ObjectId)))
        {
            _world.SaveEnemyInstance(enemy);
        }

        HashSet<ObjectId> cubeIds = checkpoint.PlacedCubes.Count == 0
            ? []
            : _world.LoadPlacedCubes().Select(cube => cube.ObjectId).ToHashSet();
        foreach (PersistedPlacedCube cube in checkpoint.PlacedCubes.Where(cube => cubeIds.Contains(cube.ObjectId)))
        {
            _world.SavePlacedCube(cube);
        }

        HashSet<(ObjectId, int)> pieceKeys = checkpoint.FracturePieces.Count == 0 ? [] : _world.LoadCubeFracturePieces()
            .Select(piece => (piece.OriginObjectId, piece.PieceIndex))
            .ToHashSet();
        foreach (PersistedCubeFracturePiece piece in checkpoint.FracturePieces
            .Where(piece => pieceKeys.Contains((piece.OriginObjectId, piece.PieceIndex))))
        {
            _world.SaveCubeFracturePiece(piece);
        }
    }
}
