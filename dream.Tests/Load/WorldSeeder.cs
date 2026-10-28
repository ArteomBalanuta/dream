using System.Diagnostics;
using Microsoft.Data.Sqlite;
using OpenTK.Mathematics;
using Dream.Server.Persistence;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;

namespace Dream.Tests.Load;

public sealed record WorldSeedResult(
    int PlacedCubes,
    int RubbleCells,
    int FracturePieces,
    int Enemies,
    double SeedMilliseconds,
    long DatabaseBytes);

// Pre-populates a SQLite world through the production schema (the init script plus
// SqliteSchemaBootstrapper) so the server restores it exactly as it restores a real save.
// Placed cubes are stacked on the terrain surface, so they rest instead of falling: the seeder
// builds a throwaway Map of the scenario's floor size and uses Map.TryAddCube, the same occupancy
// rule the runtime's restore uses. Bulk rows go in one transaction with the repository's column
// set; a profile, one cube, one fracture piece and one enemy go through the repositories so the
// row shapes stay checked against production code.
public static class WorldSeeder
{
    public const string OwnerPlayerName = "load-seeder";

    // Seeded cube ids start here, clear of the ids a run allocates. The id is inside today's
    // enemy range (ReplicatedActorIdRanges), like a legacy cube id: the restore reserves it in the
    // enemy counter, so later enemy spawns skip past the seeded cubes.
    public const int FirstSeededCubeId = 100_000;

    // Players spawn near (5, 5); leave that corner clear so nobody spawns inside a stack.
    private const int SpawnClearance = 10;

    private const float RubblePieceSide = 0.25f;

    public static WorldSeedResult Seed(string databasePath, string schemaScriptPath, LoadScenario scenario)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        SqliteSchemaBootstrapper schema = new(databasePath, schemaScriptPath);
        schema.EnsureSchema();
        using (SqlitePlayerProfileRepository profiles = new(schema))
        {
            profiles.GetOrCreate(OwnerPlayerName);
        }

        Random random = new(scenario.Seed);
        Map map = new(physics: null, generateStartupTerrain: true, width: scenario.FloorSize, depth: scenario.FloorSize);
        List<(int X, int Z)> columns = CollectColumns(map);
        Shuffle(columns, random);

        int rubbleColumns = Math.Min(scenario.RubbleCells, columns.Count);
        List<Vector3> rubbleCells = [];
        for (int i = 0; i < rubbleColumns; i++)
        {
            (int x, int z) = columns[i];
            int y = NextFreeY(map, x, z);
            if (y > 0 && y < map.Height)
            {
                rubbleCells.Add(new Vector3(x, y, z));
            }
        }

        List<Vector3> cubeCells = StackCubes(map, columns.Skip(rubbleColumns).ToList(), scenario.Cubes);
        DateTime now = DateTime.UtcNow;
        List<PersistedPlacedCube> cubes = new(cubeCells.Count);
        for (int i = 0; i < cubeCells.Count; i++)
        {
            Vector3 cell = cubeCells[i];
            cubes.Add(new PersistedPlacedCube(
                FirstSeededCubeId + i,
                OwnerPlayerName,
                cell,
                GeometryShapeKind.SolidCube,
                (i % 3) switch { 0 => GeometryMaterialId.Dirt, 1 => GeometryMaterialId.Stone, _ => GeometryMaterialId.Grass },
                CurrentHealth: 5,
                MaxHealth: 5,
                now,
                now,
                CenterPosition: cell + new Vector3(0.5f)));
        }

        List<PersistedCubeFracturePiece> pieces = [];
        for (int i = 0; i < rubbleCells.Count; i++)
        {
            Vector3 cell = rubbleCells[i];
            int sourceCubeId = FirstSeededCubeId + cubes.Count + i;
            // Four quarter-size pieces lying on the cell floor, as left by a shattered cube.
            for (int piece = 0; piece < 4; piece++)
            {
                float offsetX = piece % 2 == 0 ? 0.25f : 0.75f;
                float offsetZ = piece < 2 ? 0.25f : 0.75f;
                pieces.Add(new PersistedCubeFracturePiece(
                    sourceCubeId,
                    piece,
                    OwnerPlayerName,
                    cell,
                    cell + new Vector3(offsetX, RubblePieceSide / 2f, offsetZ),
                    RubblePieceSide,
                    GeometryMaterialId.Stone,
                    now,
                    now));
            }
        }

        List<PersistedEnemyInstance> enemies = [];
        for (int i = 0; i < scenario.Enemies; i++)
        {
            float x = 12f + (float)random.NextDouble() * (scenario.FloorSize - 16f);
            float z = 12f + (float)random.NextDouble() * (scenario.FloorSize - 16f);
            enemies.Add(new PersistedEnemyInstance(
                WorldDefaultsEnemyIdBase + i,
                "wolf",
                "Wolf",
                new Vector3(x, 1f, z),
                Vector3.UnitZ,
                new PersistedCreatureStats(30, 30, 10, 10, 10, 0, 2.8f, 0.95f, 2),
                now,
                now));
        }

        WriteRows(schema, cubes, pieces, enemies);
        stopwatch.Stop();
        long databaseBytes = File.Exists(databasePath) ? new FileInfo(databasePath).Length : 0;
        return new WorldSeedResult(cubes.Count, rubbleCells.Count, pieces.Count, enemies.Count, stopwatch.Elapsed.TotalMilliseconds, databaseBytes);
    }

    private const int WorldDefaultsEnemyIdBase = Dream.Simulation.Runtime.WorldSessionRuntime.FirstEnemyActorId;

    // One row of each kind goes through the production repository; the rest are bulk-inserted
    // with the same columns in one transaction (thousands of single-row repository saves would
    // each open a connection and commit).
    private static void WriteRows(
        SqliteSchemaBootstrapper schema,
        IReadOnlyList<PersistedPlacedCube> cubes,
        IReadOnlyList<PersistedCubeFracturePiece> pieces,
        IReadOnlyList<PersistedEnemyInstance> enemies)
    {
        using (SqliteWorldPersistenceRepository repository = new(schema))
        {
            if (cubes.Count > 0)
            {
                repository.SavePlacedCube(cubes[0]);
            }

            if (pieces.Count > 0)
            {
                repository.SaveCubeFracturePiece(pieces[0]);
            }

            if (enemies.Count > 0)
            {
                repository.SaveEnemyInstance(enemies[0]);
            }
        }

        using SqliteConnection connection = schema.CreateOpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction();
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO placed_cube_instances (
                    cube_id, owner_player_name, cell_x, cell_y, cell_z, center_x, center_y, center_z,
                    shape_kind, material_id, current_health, max_health, created_utc, updated_utc)
                VALUES ($id, $owner, $cx, $cy, $cz, $px, $py, $pz, $shape, $material, $hp, $maxHp, $created, $updated);
                """;
            SqliteParameter[] parameters = AddParameters(
                command,
                "$id", "$owner", "$cx", "$cy", "$cz", "$px", "$py", "$pz", "$shape", "$material", "$hp", "$maxHp", "$created", "$updated");
            for (int i = 1; i < cubes.Count; i++)
            {
                PersistedPlacedCube cube = cubes[i];
                Vector3 center = cube.CenterPosition ?? cube.CellPosition + new Vector3(0.5f);
                SetValues(
                    parameters,
                    cube.CubeId,
                    cube.OwnerPlayerName,
                    cube.CellPosition.X,
                    cube.CellPosition.Y,
                    cube.CellPosition.Z,
                    center.X,
                    center.Y,
                    center.Z,
                    cube.ShapeKind.ToString(),
                    cube.MaterialId.ToString(),
                    cube.CurrentHealth,
                    cube.MaxHealth,
                    SqliteSchemaBootstrapper.FormatUtc(cube.CreatedUtc),
                    SqliteSchemaBootstrapper.FormatUtc(cube.UpdatedUtc));
                command.ExecuteNonQuery();
            }
        }

        using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO cube_fracture_pieces (
                    source_cube_id, piece_index, owner_player_name, parent_cell_x, parent_cell_y, parent_cell_z,
                    center_x, center_y, center_z, side_length, material_id, created_utc, updated_utc)
                VALUES ($source, $index, $owner, $cx, $cy, $cz, $px, $py, $pz, $side, $material, $created, $updated);
                """;
            SqliteParameter[] parameters = AddParameters(
                command,
                "$source", "$index", "$owner", "$cx", "$cy", "$cz", "$px", "$py", "$pz", "$side", "$material", "$created", "$updated");
            for (int i = 1; i < pieces.Count; i++)
            {
                PersistedCubeFracturePiece piece = pieces[i];
                SetValues(
                    parameters,
                    piece.SourceCubeId,
                    piece.PieceIndex,
                    piece.OwnerPlayerName,
                    piece.ParentCell.X,
                    piece.ParentCell.Y,
                    piece.ParentCell.Z,
                    piece.Center.X,
                    piece.Center.Y,
                    piece.Center.Z,
                    piece.SideLength,
                    piece.MaterialId.ToString(),
                    SqliteSchemaBootstrapper.FormatUtc(piece.CreatedUtc),
                    SqliteSchemaBootstrapper.FormatUtc(piece.UpdatedUtc));
                command.ExecuteNonQuery();
            }
        }

        transaction.Commit();

        // Enemies are few; the repository's own insert keeps their (wider) row shape exact.
        using SqliteWorldPersistenceRepository enemyRepository = new(schema);
        for (int i = 1; i < enemies.Count; i++)
        {
            enemyRepository.SaveEnemyInstance(enemies[i]);
        }
    }

    private static SqliteParameter[] AddParameters(SqliteCommand command, params string[] names)
    {
        SqliteParameter[] parameters = new SqliteParameter[names.Length];
        for (int i = 0; i < names.Length; i++)
        {
            parameters[i] = command.Parameters.Add(new SqliteParameter { ParameterName = names[i] });
        }

        return parameters;
    }

    private static void SetValues(SqliteParameter[] parameters, params object[] values)
    {
        for (int i = 0; i < parameters.Length; i++)
        {
            parameters[i].Value = values[i];
        }
    }

    private static List<(int X, int Z)> CollectColumns(Map map)
    {
        List<(int X, int Z)> columns = [];
        for (int x = 1; x < map.Width - 1; x++)
        {
            for (int z = 1; z < map.Depth - 1; z++)
            {
                if (x < SpawnClearance && z < SpawnClearance)
                {
                    continue;
                }

                columns.Add((x, z));
            }
        }

        return columns;
    }

    // Round-robin over the columns, one layer at a time, so stacks stay low and even.
    private static List<Vector3> StackCubes(Map map, List<(int X, int Z)> columns, int count)
    {
        List<Vector3> cells = new(count);
        bool placedAny = true;
        while (cells.Count < count && placedAny)
        {
            placedAny = false;
            foreach ((int x, int z) in columns)
            {
                if (cells.Count >= count)
                {
                    break;
                }

                int y = NextFreeY(map, x, z);
                if (y <= 0 || y >= map.Height - 1)
                {
                    continue;
                }

                Vector3 cell = new(x, y, z);
                if (map.TryAddCube(cell, withPhysics: false) != null)
                {
                    cells.Add(cell);
                    placedAny = true;
                }
            }
        }

        return cells;
    }

    // The cell right above the highest occupied cell of the column, so whatever goes there rests
    // on something. 0 means the column has no support at all.
    private static int NextFreeY(Map map, int x, int z)
    {
        for (int y = map.Height - 1; y >= 0; y--)
        {
            if (map.Cubes[x, y, z] != null)
            {
                return y + 1;
            }
        }

        return 0;
    }

    private static void Shuffle<T>(List<T> items, Random random)
    {
        for (int i = items.Count - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }
}
