using CommandLine;

namespace DataSeeder;

public class Options
{
    [Option('d', "db", Required = false, HelpText = "Name of the Cosmos DB database.")]
    public string Database { get; set; } = string.Empty;

    [Option('p', "path", Required = true, HelpText = "Path to folder containing .json files or parent folder.")]
    public string Path { get; set; } = string.Empty;

    [Option("drop", Required = false, HelpText = "Drop and recreate containers.")]
    public bool DropAndCreate { get; set; } = false;

    [Option('t', "targetType", Required = true, HelpText = "Target type to seed: cosmos, redis, or serviceBus.")]
    public string TargetType { get; set; } = "cosmos";

    // Reverse seeding options
    [Option('s', "sourceType", Required = false, HelpText = "Source type: files (default) or cosmos (reverse seeding).")]
    public string SourceType { get; set; } = "files";

    [Option('c', "connectionString", Required = false, HelpText = "Cosmos DB connection string for reverse seeding.")]
    public string ConnectionString { get; set; } = string.Empty;

    [Option("container", Required = false, HelpText = "Specific container name to export (optional, exports all if not specified).")]
    public string Container { get; set; } = string.Empty;

    [Option("managed-identity", Required = false, HelpText = "Use managed identity for authentication (Azure environments only).")]
    public bool UseManagedIdentity { get; set; } = false;

    [Option("page-size", Required = false, Default = 100, HelpText = "Page size for document retrieval (default: 100, max: 1000).")]
    public int PageSize { get; set; } = 100;

    [Option("max-ru", Required = false, Default = 400, HelpText = "Maximum RU/s to consume during export (default: 400).")]
    public int MaxRU { get; set; } = 400;

    [Option("force-update", Required = false, HelpText = "Force update all files even if no changes detected.")]
    public bool ForceUpdate { get; set; } = false;
}
