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
}
