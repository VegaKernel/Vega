using System.Data.Common;

namespace Vega.Persistence.Relational;

internal interface IRelationalConnectionFactory
{
    ValueTask<DbConnection> OpenAsync(CancellationToken cancellationToken = default);

    DbConnection Open();
}
