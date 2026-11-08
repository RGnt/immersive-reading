using System.Data.Common;
using Microsoft.Data.Sqlite;

namespace ImmersiveReading.Plugins.AiCompanion;

internal static class SqliteExtensions
{
    public static SqliteParameter AddWithValue(this DbParameterCollection parameters, string parameterName, object? value)
    {
        if (parameters is SqliteParameterCollection sqliteParams)
        {
            return sqliteParams.AddWithValue(parameterName, value);
        }

        var p = new SqliteParameter(parameterName, value ?? DBNull.Value);
        parameters.Add(p);
        return p;
    }
}

