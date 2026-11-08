namespace ImmersiveReading.Core.Abstractions;

public interface IDbMigration
{
    string ModuleId { get; }
    int Version { get; }
    string Name { get; }
    string Sql { get; }
}

