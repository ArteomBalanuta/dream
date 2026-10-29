-- Migration 0006: terrain as data (persistence wave D). New tables only; no existing row changes.
CREATE TABLE terrain_cell_overrides (
    world_id INTEGER NOT NULL,
    object_id INTEGER NOT NULL,
    cell_x INTEGER NOT NULL,
    cell_y INTEGER NOT NULL,
    cell_z INTEGER NOT NULL,
    kind INTEGER NOT NULL,
    cell_data BLOB NULL,
    created_utc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
    updated_utc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
    PRIMARY KEY (world_id, object_id),
    FOREIGN KEY (world_id) REFERENCES worlds(world_id) ON DELETE CASCADE,
    CHECK (object_id >= 1),
    CHECK (cell_x >= 0 AND cell_y >= 1 AND cell_z >= 0),
    CHECK (kind IN (0, 1)),
    CHECK ((kind = 0 AND cell_data IS NULL) OR (kind = 1 AND cell_data IS NOT NULL))
);

CREATE UNIQUE INDEX ux_terrain_cell_overrides_cell
    ON terrain_cell_overrides (world_id, cell_x, cell_y, cell_z);

-- Same counter rule as the four object tables of 0005: the counter is never behind a committed id.
CREATE TRIGGER trg_terrain_cell_overrides_advance_object_id AFTER INSERT ON terrain_cell_overrides BEGIN
    UPDATE worlds SET next_object_id = NEW.object_id + 1
    WHERE world_id = NEW.world_id AND next_object_id <= NEW.object_id;
END;

CREATE TABLE terrain_chunks (
    world_id INTEGER NOT NULL,
    chunk_x INTEGER NOT NULL,
    chunk_z INTEGER NOT NULL,
    format_version INTEGER NOT NULL,
    cell_count INTEGER NOT NULL,
    data BLOB NOT NULL,
    updated_utc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
    PRIMARY KEY (world_id, chunk_x, chunk_z),
    FOREIGN KEY (world_id) REFERENCES worlds(world_id) ON DELETE CASCADE,
    CHECK (chunk_x >= 0 AND chunk_z >= 0),
    CHECK (format_version >= 1),
    CHECK (cell_count >= 0),
    CHECK (length(data) >= 23)
) WITHOUT ROWID;
