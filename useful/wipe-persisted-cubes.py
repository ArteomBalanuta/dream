#!/usr/bin/env python3
from __future__ import annotations

import argparse
import datetime as dt
import json
import shutil
import sqlite3
import sys
from pathlib import Path


REPO_ROOT = Path(__file__).resolve().parents[1]
CUBE_TABLES = ("cube_fracture_pieces", "placed_cube_instances")


def locate_database(explicit_path: str | None) -> Path:
    if explicit_path:
        db_path = Path(explicit_path).expanduser().resolve()
        if not db_path.exists():
            raise FileNotFoundError(f"Database not found: {db_path}")
        return db_path

    candidates = sorted(
        REPO_ROOT.glob("dream/bin/**/config/player-profiles.db"),
        key=lambda path: path.stat().st_mtime,
        reverse=True,
    )
    if not candidates:
        raise FileNotFoundError(
            "Could not locate dream/bin/**/config/player-profiles.db. "
            "Run the game/server once or pass --db with an explicit path.")

    return candidates[0].resolve()


def backup_database(db_path: Path) -> Path:
    timestamp = dt.datetime.now().strftime("%Y%m%d-%H%M%S")
    backup_path = db_path.with_name(f"{db_path.stem}.wipe-cubes-{timestamp}.backup{db_path.suffix}")
    shutil.copy2(db_path, backup_path)
    return backup_path


def count_rows(connection: sqlite3.Connection) -> dict[str, int]:
    counts: dict[str, int] = {}
    for table in CUBE_TABLES:
        counts[table] = int(connection.execute(f"SELECT COUNT(*) FROM {table};").fetchone()[0])
    return counts


def wipe_cubes(db_path: Path, dry_run: bool, create_backup: bool) -> dict[str, object]:
    backup_path: Path | None = None
    if not dry_run and create_backup:
        backup_path = backup_database(db_path)

    with sqlite3.connect(str(db_path)) as connection:
        before = count_rows(connection)
        if dry_run:
            after = before.copy()
        else:
            for table in CUBE_TABLES:
                connection.execute(f"DELETE FROM {table};")
            connection.commit()
            after = count_rows(connection)

    return {
        "database_path": str(db_path),
        "backup_path": str(backup_path) if backup_path else None,
        "dry_run": dry_run,
        "before": before,
        "after": after,
        "deleted": {table: before[table] - after[table] for table in CUBE_TABLES},
        "note": "Only persisted player-made cubes and fracture pieces are wiped; authored map geometry is not stored in these tables.",
    }


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Wipe persisted Dream solid cubes and small fracture-piece cubes from player-profiles.db.")
    parser.add_argument("--db", help="Optional explicit path to player-profiles.db.")
    parser.add_argument("--dry-run", action="store_true", help="Show counts without deleting rows.")
    parser.add_argument(
        "--no-backup",
        action="store_true",
        help="Skip creating a timestamped backup before deleting rows.")
    return parser


def main() -> int:
    parser = build_parser()
    args = parser.parse_args()

    db_path = locate_database(args.db)
    result = wipe_cubes(
        db_path,
        dry_run=args.dry_run,
        create_backup=not args.no_backup)
    json.dump(result, sys.stdout, indent=2)
    sys.stdout.write("\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
