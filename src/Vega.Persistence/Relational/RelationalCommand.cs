using System.Data.Common;

namespace Vega.Persistence.Relational;

internal static class RelationalCommand
{
    public static DbParameter AddParameter(
        this DbCommand command,
        string name,
        object? value)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
        return parameter;
    }
}
