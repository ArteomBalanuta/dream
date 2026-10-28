using Microsoft.Data.Sqlite;
using OpenTK.Mathematics;

namespace Dream.Server.Persistence.Store;

// Parameter and reader helpers. A Bind* call reuses the command's parameter when it already exists
// (a prepared command bound once per row) and adds it otherwise, so it costs no more than
// AddWithValue does today: the boxing of the value.
public static class SqliteCommandExtensions
{
    // A typed parameter to keep and set per row on a reused, prepared command.
    public static SqliteParameter AddParameter(this SqliteCommand c, string name, SqliteType type)
    {
        return c.Parameters.Add(name, type);
    }

    public static void BindText(this SqliteCommand c, string name, string value) => Bind(c, name, value);

    public static void BindInt64(this SqliteCommand c, string name, long value) => Bind(c, name, value);

    public static void BindReal(this SqliteCommand c, string name, double value) => Bind(c, name, value);

    public static void BindNullableText(this SqliteCommand c, string name, string? value) =>
        Bind(c, name, (object?)value ?? DBNull.Value);

    // Stored as SqliteSchemaBootstrapper.FormatUtc text.
    public static void BindUtc(this SqliteCommand c, string name, DateTime value) =>
        Bind(c, name, SqliteSchemaBootstrapper.FormatUtc(value));

    // Binds the components as floats, as AddWithValue(float) does today.
    public static void BindVector3(this SqliteCommand c, string x, string y, string z, Vector3 v)
    {
        Bind(c, x, v.X);
        Bind(c, y, v.Y);
        Bind(c, z, v.Z);
    }

    // NULL in all three columns when v is null.
    public static void BindNullableVector3(this SqliteCommand c, string x, string y, string z, Vector3? v)
    {
        if (v is Vector3 value)
        {
            BindVector3(c, x, y, z, value);
            return;
        }

        Bind(c, x, DBNull.Value);
        Bind(c, y, DBNull.Value);
        Bind(c, z, DBNull.Value);
    }

    // Floats at firstOrdinal, firstOrdinal + 1 and firstOrdinal + 2.
    public static Vector3 GetVector3(this SqliteDataReader r, int firstOrdinal)
    {
        return new Vector3(r.GetFloat(firstOrdinal), r.GetFloat(firstOrdinal + 1), r.GetFloat(firstOrdinal + 2));
    }

    // Null when the column at firstOrdinal is NULL. Also null when either of the other two is, as
    // the placed-cube center read treats a partly NULL triple today.
    public static Vector3? GetNullableVector3(this SqliteDataReader r, int firstOrdinal)
    {
        if (r.IsDBNull(firstOrdinal) || r.IsDBNull(firstOrdinal + 1) || r.IsDBNull(firstOrdinal + 2))
        {
            return null;
        }

        return r.GetVector3(firstOrdinal);
    }

    // SqliteSchemaBootstrapper.ParseUtc of the text column.
    public static DateTime GetUtc(this SqliteDataReader r, int ordinal)
    {
        return SqliteSchemaBootstrapper.ParseUtc(r.GetString(ordinal));
    }

    public static string? GetNullableString(this SqliteDataReader r, int ordinal)
    {
        return r.IsDBNull(ordinal) ? null : r.GetString(ordinal);
    }

    private static void Bind(SqliteCommand c, string name, object value)
    {
        SqliteParameterCollection parameters = c.Parameters;
        int index = parameters.IndexOf(name);
        if (index >= 0)
        {
            parameters[index].Value = value;
            return;
        }

        parameters.AddWithValue(name, value);
    }
}
