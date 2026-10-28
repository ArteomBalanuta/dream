using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Dream.Tests.Server.Persistence.Migrations;

// A normalized, comparable description of a database's schema: every sqlite_master entry (tables,
// indexes, triggers, with whitespace-normalized SQL) plus, per table, PRAGMA table_info,
// index_list/index_xinfo and foreign_key_list. schema_version is the migrator's own bookkeeping and is
// left out, so a migrated database compares equal to one the old bootstrapper built.
//
// ignoreColumnOrder compares a table's columns as a set (sorted by name, without cid) and drops the
// table's CREATE statement, whose text lists the columns in order. Use it for databases whose columns
// were appended by ALTER TABLE; everything else (types, defaults, NOT NULL, keys, indexes, triggers)
// is still compared.
internal static partial class SqliteSchemaSnapshot
{
    public static IReadOnlyList<string> Capture(SqliteConnection connection, bool ignoreColumnOrder = false)
    {
        List<string> lines = [];
        List<string> tables = [];
        using (SqliteCommand master = connection.CreateCommand())
        {
            master.CommandText =
                "SELECT type, name, tbl_name, sql FROM sqlite_master " +
                "WHERE name <> 'schema_version' AND tbl_name <> 'schema_version' ORDER BY type, name;";
            using SqliteDataReader reader = master.ExecuteReader();
            while (reader.Read())
            {
                string type = reader.GetString(0);
                string name = reader.GetString(1);
                string table = reader.GetString(2);
                string sql = reader.IsDBNull(3) ? "<auto>" : Normalize(reader.GetString(3));
                if (type == "table")
                {
                    tables.Add(name);
                    if (ignoreColumnOrder)
                    {
                        sql = "<columns compared as a set>";
                    }
                }

                lines.Add($"master {type} {name} on {table}: {sql}");
            }
        }

        foreach (string table in tables)
        {
            List<string> columns = Rows(connection, $"PRAGMA table_info(\"{table}\");", reader =>
                ignoreColumnOrder
                    ? $"column {table}.{reader.GetString(1)} {Cells(reader, 2)}"
                    : $"column {table} {Cells(reader, 0)}");
            if (ignoreColumnOrder)
            {
                columns.Sort(StringComparer.Ordinal);
            }

            lines.AddRange(columns);

            List<string> indexNames = [];
            foreach (string index in Rows(connection, $"PRAGMA index_list(\"{table}\");", reader =>
                     {
                         indexNames.Add(reader.GetString(1));
                         // seq, name, unique, origin, partial; seq is creation order, so leave it out.
                         return $"index {table} {Cells(reader, 1)}";
                     }).Order(StringComparer.Ordinal))
            {
                lines.Add(index);
            }

            foreach (string index in indexNames.Order(StringComparer.Ordinal))
            {
                // seqno, cid, name, desc, coll, key. cid depends on column order, so name identifies it.
                lines.AddRange(Rows(connection, $"PRAGMA index_xinfo(\"{index}\");", reader =>
                    ignoreColumnOrder
                        ? $"index-column {index} seq={reader.GetInt64(0)} name={Cell(reader, 2)} desc={Cell(reader, 3)} coll={Cell(reader, 4)} key={Cell(reader, 5)}"
                        : $"index-column {index} {Cells(reader, 0)}"));
            }

            lines.AddRange(Rows(connection, $"PRAGMA foreign_key_list(\"{table}\");", reader =>
                $"foreign-key {table} {Cells(reader, 0)}"));
        }

        return lines;
    }

    // Every row of every user table (schema_version excluded), as text with each value's storage
    // class, in a stable order. Used to prove a migration left the data alone.
    public static IReadOnlyList<string> DumpRows(SqliteConnection connection)
    {
        List<string> tables = Rows(connection,
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite\\_%' ESCAPE '\\' " +
            "AND name <> 'schema_version' ORDER BY name;",
            reader => reader.GetString(0));
        List<string> lines = [];
        foreach (string table in tables)
        {
            List<string> rows = Rows(connection, $"SELECT * FROM \"{table}\";", reader =>
            {
                StringBuilder row = new(table);
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    row.Append(' ').Append(reader.GetName(i)).Append('=').Append(Cell(reader, i));
                }

                return row.ToString();
            });
            rows.Sort(StringComparer.Ordinal);
            lines.AddRange(rows);
        }

        return lines;
    }

    private static List<string> Rows(SqliteConnection connection, string sql, Func<SqliteDataReader, string> format)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        using SqliteDataReader reader = command.ExecuteReader();
        List<string> rows = [];
        while (reader.Read())
        {
            rows.Add(format(reader));
        }

        return rows;
    }

    private static string Cells(SqliteDataReader reader, int first)
    {
        StringBuilder cells = new();
        for (int i = first; i < reader.FieldCount; i++)
        {
            cells.Append(reader.GetName(i)).Append('=').Append(Cell(reader, i)).Append(' ');
        }

        return cells.ToString().TrimEnd();
    }

    private static string Cell(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return "NULL";
        }

        object value = reader.GetValue(ordinal);
        string text = value switch
        {
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            byte[] bytes => Convert.ToHexString(bytes),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
        };
        return $"{value.GetType().Name}:{text}";
    }

    private static string Normalize(string sql)
    {
        return Whitespace().Replace(sql, " ").Trim();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
