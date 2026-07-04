#!/usr/bin/env python3
from __future__ import annotations

import argparse
import json
import shutil
import sqlite3
import sys
from pathlib import Path


REPO_ROOT = Path(__file__).resolve().parents[3]
DDL_PATH = REPO_ROOT / "dream" / "config" / "player-profiles.init.sql"
DOC_PATH = REPO_ROOT / "docs" / "server-persistence-ddl.md"


def locate_db(explicit: str | None) -> Path:
    if explicit:
        candidate = Path(explicit).expanduser().resolve()
        if not candidate.exists():
            raise FileNotFoundError(f"Database not found: {candidate}")
        return candidate

    candidates = sorted(
        REPO_ROOT.glob("dream/bin/**/config/player-profiles.db"),
        key=lambda path: path.stat().st_mtime,
        reverse=True,
    )
    if not candidates:
        raise FileNotFoundError(
            "Could not locate dream/bin/**/config/player-profiles.db. Build or run the game/server first, "
            "or pass --db with an explicit path.")
    return candidates[0].resolve()


def open_connection(db_path: Path) -> sqlite3.Connection:
    connection = sqlite3.connect(str(db_path))
    connection.row_factory = sqlite3.Row
    return connection


def backup_database(db_path: Path) -> Path:
    backup_path = db_path.with_name(f"{db_path.stem}.backup{db_path.suffix}")
    shutil.copy2(db_path, backup_path)
    return backup_path


def fetch_rows(connection: sqlite3.Connection, sql: str, params: dict[str, object]) -> list[dict[str, object]]:
    cursor = connection.execute(sql, params)
    rows = cursor.fetchall()
    return [dict(row) for row in rows]


def print_json(data: object) -> None:
    json.dump(data, sys.stdout, indent=2)
    sys.stdout.write("\n")


def command_locate(args: argparse.Namespace) -> int:
    db_path = locate_db(args.db)
    print_json(
        {
            "repo_root": str(REPO_ROOT),
            "ddl_path": str(DDL_PATH),
            "ddl_doc_path": str(DOC_PATH),
            "database_path": str(db_path),
        })
    return 0


def command_tables(args: argparse.Namespace) -> int:
    db_path = locate_db(args.db)
    with open_connection(db_path) as connection:
        rows = fetch_rows(
            connection,
            """
            SELECT name, type
            FROM sqlite_master
            WHERE type IN ('table', 'view')
              AND name NOT LIKE 'sqlite_%'
            ORDER BY type, name
            """,
            {},
        )
    print_json({"database_path": str(db_path), "objects": rows})
    return 0


def command_query(args: argparse.Namespace) -> int:
    db_path = locate_db(args.db)
    parameters = json.loads(args.params) if args.params else {}
    with open_connection(db_path) as connection:
        rows = fetch_rows(connection, args.sql, parameters)
    print_json({"database_path": str(db_path), "rows": rows})
    return 0


def command_run(args: argparse.Namespace) -> int:
    db_path = locate_db(args.db)
    parameters = json.loads(args.params) if args.params else {}
    sql = Path(args.file).read_text(encoding="utf-8") if args.file else args.sql
    if not sql:
        raise ValueError("Provide --sql or --file for write operations.")

    backup_path = backup_database(db_path) if args.backup else None
    with open_connection(db_path) as connection:
        cursor = connection.execute(sql, parameters)
        connection.commit()
        changed = cursor.rowcount

    print_json(
        {
            "database_path": str(db_path),
            "backup_path": str(backup_path) if backup_path else None,
            "rows_affected": changed,
        })
    return 0


def command_show_player(args: argparse.Namespace) -> int:
    db_path = locate_db(args.db)
    with open_connection(db_path) as connection:
        profile_rows = fetch_rows(
            connection,
            "SELECT * FROM player_profiles WHERE player_name = $playerName",
            {"playerName": args.name},
        )
        world_rows = fetch_rows(
            connection,
            "SELECT * FROM player_world_state WHERE player_name = $playerName",
            {"playerName": args.name},
        )
        inventory_rows = fetch_rows(
            connection,
            """
            SELECT *
            FROM player_inventory_slots
            WHERE player_name = $playerName
            ORDER BY slot_index
            """,
            {"playerName": args.name},
        )
    print_json(
        {
            "database_path": str(db_path),
            "player_name": args.name,
            "profile": profile_rows,
            "world_state": world_rows,
            "inventory": inventory_rows,
        })
    return 0


def command_update_player_position(args: argparse.Namespace) -> int:
    db_path = locate_db(args.db)
    backup_path = backup_database(db_path) if args.backup else None
    with open_connection(db_path) as connection:
        connection.execute(
            """
            UPDATE player_world_state
            SET position_x = $x,
                position_y = $y,
                position_z = $z
            WHERE player_name = $playerName
            """,
            {
                "playerName": args.name,
                "x": args.x,
                "y": args.y,
                "z": args.z,
            },
        )
        connection.commit()
        rows = fetch_rows(
            connection,
            "SELECT player_name, position_x, position_y, position_z FROM player_world_state WHERE player_name = $playerName",
            {"playerName": args.name},
        )
    print_json(
        {
            "database_path": str(db_path),
            "backup_path": str(backup_path) if backup_path else None,
            "rows": rows,
        })
    return 0


def command_update_player_stats(args: argparse.Namespace) -> int:
    db_path = locate_db(args.db)
    fields = {
        "strength": args.strength,
        "vitality": args.vitality,
        "attack_speed": args.attack_speed,
        "level": args.level,
        "experience": args.experience,
    }
    assignments = [f"{key} = ${key}" for key, value in fields.items() if value is not None]
    if not assignments:
        raise ValueError("Provide at least one stat field to update.")

    parameters: dict[str, object] = {"playerName": args.name}
    for key, value in fields.items():
        if value is not None:
            parameters[key] = value

    backup_path = backup_database(db_path) if args.backup else None
    with open_connection(db_path) as connection:
        connection.execute(
            f"""
            UPDATE player_profiles
            SET {", ".join(assignments)}
            WHERE player_name = $playerName
            """,
            parameters,
        )
        connection.commit()
        rows = fetch_rows(
            connection,
            """
            SELECT player_name, strength, vitality, attack_speed, level, experience
            FROM player_profiles
            WHERE player_name = $playerName
            """,
            {"playerName": args.name},
        )
    print_json(
        {
            "database_path": str(db_path),
            "backup_path": str(backup_path) if backup_path else None,
            "rows": rows,
        })
    return 0


def command_update_enemy(args: argparse.Namespace) -> int:
    db_path = locate_db(args.db)
    fields = {
        "position_x": args.x,
        "position_y": args.y,
        "position_z": args.z,
        "current_hp": args.current_hp,
        "max_hp": args.max_hp,
        "level": args.level,
        "enemy_name": args.name,
        "is_alive": args.is_alive,
    }
    assignments = [f"{key} = ${key}" for key, value in fields.items() if value is not None]
    if not assignments:
        raise ValueError("Provide at least one enemy field to update.")

    parameters: dict[str, object] = {"enemyId": args.enemy_id}
    for key, value in fields.items():
        if value is not None:
            parameters[key] = value

    backup_path = backup_database(db_path) if args.backup else None
    with open_connection(db_path) as connection:
        connection.execute(
            f"""
            UPDATE enemy_instances
            SET {", ".join(assignments)}
            WHERE enemy_id = $enemyId
            """,
            parameters,
        )
        connection.commit()
        rows = fetch_rows(
            connection,
            """
            SELECT enemy_id, enemy_name, position_x, position_y, position_z, current_hp, max_hp, level, is_alive
            FROM enemy_instances
            WHERE enemy_id = $enemyId
            """,
            {"enemyId": args.enemy_id},
        )
    print_json(
        {
            "database_path": str(db_path),
            "backup_path": str(backup_path) if backup_path else None,
            "rows": rows,
        })
    return 0


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Inspect and edit Dream's SQLite persistence state.")
    parser.add_argument("--db", help="Optional explicit path to player-profiles.db")
    subparsers = parser.add_subparsers(dest="command", required=True)

    locate_parser = subparsers.add_parser("locate-db", help="Print the resolved database and DDL paths.")
    locate_parser.set_defaults(func=command_locate)

    tables_parser = subparsers.add_parser("tables", help="List non-SQLite tables/views in the live database.")
    tables_parser.set_defaults(func=command_tables)

    query_parser = subparsers.add_parser("query", help="Run a read-only SQL query and print JSON rows.")
    query_parser.add_argument("--sql", required=True, help="SQL statement to execute.")
    query_parser.add_argument("--params", help="JSON object of named SQLite parameters.")
    query_parser.set_defaults(func=command_query)

    run_parser = subparsers.add_parser("run", help="Run a write query. Prefer targeted commands when possible.")
    run_parser.add_argument("--sql", help="SQL statement to execute.")
    run_parser.add_argument("--file", help="Path to a SQL file to execute.")
    run_parser.add_argument("--params", help="JSON object of named SQLite parameters.")
    run_parser.add_argument("--backup", action="store_true", help="Create player-profiles.backup.db before writing.")
    run_parser.set_defaults(func=command_run)

    show_player_parser = subparsers.add_parser("show-player", help="Show profile, world state, and inventory for one player.")
    show_player_parser.add_argument("--name", required=True, help="Player name.")
    show_player_parser.set_defaults(func=command_show_player)

    position_parser = subparsers.add_parser("update-player-position", help="Move one persisted player.")
    position_parser.add_argument("--name", required=True, help="Player name.")
    position_parser.add_argument("--x", required=True, type=float)
    position_parser.add_argument("--y", required=True, type=float)
    position_parser.add_argument("--z", required=True, type=float)
    position_parser.add_argument("--backup", action="store_true", help="Create player-profiles.backup.db before writing.")
    position_parser.set_defaults(func=command_update_player_position)

    stats_parser = subparsers.add_parser("update-player-stats", help="Update one persisted player's base stats.")
    stats_parser.add_argument("--name", required=True, help="Player name.")
    stats_parser.add_argument("--strength", type=float)
    stats_parser.add_argument("--vitality", type=float)
    stats_parser.add_argument("--attack-speed", dest="attack_speed", type=float)
    stats_parser.add_argument("--level", type=int)
    stats_parser.add_argument("--experience", type=int)
    stats_parser.add_argument("--backup", action="store_true", help="Create player-profiles.backup.db before writing.")
    stats_parser.set_defaults(func=command_update_player_stats)

    enemy_parser = subparsers.add_parser("update-enemy", help="Update one persisted enemy row.")
    enemy_parser.add_argument("--enemy-id", required=True, help="Enemy instance id.")
    enemy_parser.add_argument("--name")
    enemy_parser.add_argument("--x", type=float)
    enemy_parser.add_argument("--y", type=float)
    enemy_parser.add_argument("--z", type=float)
    enemy_parser.add_argument("--current-hp", dest="current_hp", type=float)
    enemy_parser.add_argument("--max-hp", dest="max_hp", type=float)
    enemy_parser.add_argument("--level", type=int)
    enemy_parser.add_argument("--is-alive", dest="is_alive", type=int, choices=[0, 1])
    enemy_parser.add_argument("--backup", action="store_true", help="Create player-profiles.backup.db before writing.")
    enemy_parser.set_defaults(func=command_update_enemy)

    return parser


def main() -> int:
    parser = build_parser()
    args = parser.parse_args()
    return args.func(args)


if __name__ == "__main__":
    raise SystemExit(main())
