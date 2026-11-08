using System.Data.Common;

namespace ImmersiveReading.Core.Abstractions;

public interface IDbConnectionFactory
{
    DbConnection OpenConnection();
}

