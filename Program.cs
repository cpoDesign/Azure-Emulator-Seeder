using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DataSeeder;

public class Options
{
    [Option('d', "db", Required = false, HelpText = "Name of the Cosmos DB database.")]
    public string Database { get; set; } = string.Empty;

    [Option('p', "path", Required = true, HelpText = "Path to folder containing .json files or parent folder.")]
    public string Path { get; set; } = string.Empty;

    [Option("drop", Required = false, HelpText = "Drop and recreate containers.")]
    public bool DropAndCreate { get; set; } = false;

    [Option("targetType", Required = true, HelpText = "Target type to seed: cosmos, redis, or serviceBus.")]
    public string TargetType { get; set; } = "cosmos";
}

public class SeederService
{
    private readonly ILogger<SeederService> _logger;
    public SeederService(ILogger<SeederService> logger)
    {
        _logger = logger;
    }

    public async Task RunAsync(string parentPath, bool dropAndCreate = false)
    {
        if (!Directory.Exists(parentPath))
        {
            _logger.LogError("Directory not found: {Path}", parentPath);
            return;
        }
        var dbDirs = Directory.GetDirectories(parentPath);
        if (dbDirs.Length == 0)
        {
            _logger.LogWarning("No subdirectories found in the specified parent folder.");
            return;
        }
        foreach (var dbDir in dbDirs)
        {
            var dbName = Path.GetFileName(dbDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            _logger.LogInformation("Seeding database: {DbName}", dbName);
            using var httpClient = new HttpClient(new HttpClientHandler {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            });
            httpClient.BaseAddress = new Uri(Program.EmulatorEndpoint);
            var dbCreated = await Program.CreateDatabaseIfNotExistsAsync(httpClient, dbName);
            _logger.LogInformation(dbCreated ? "Database '{Db}' created." : "Database '{Db}' already exists.", dbName);
            var jsonFiles = Directory.GetFiles(dbDir, "*.json");
            if (jsonFiles.Length == 0)
            {
                _logger.LogWarning("No .json files found in {DbDir}.", dbDir);
                continue;
            }
            var containerName = dbName; // Use dbName as container name
            if (dropAndCreate)
            {
                _logger.LogInformation("Dropping and recreating container '{Container}' in database '{Db}'...", containerName, dbName);
                await Program.DropAndCreateContainerBatchAsync(httpClient, dbName, containerName);
            }
            else
            {
                await Program.CreateContainerIfNotExistsAsync(httpClient, dbName, containerName);
            }
            var inserter = new CosmosDbInserter(httpClient, dbName, containerName);
            int successCount = 0;
            int failCount = 0;
            int total = jsonFiles.Length;
            int current = 0;
            foreach (var file in jsonFiles)
            {
                try
                {
                    var fileContent = await File.ReadAllTextAsync(file);
                    using var doc = JsonDocument.Parse(fileContent);
                    var root = doc.RootElement;
                    var seedConfig = root.GetProperty("seedConfig");
                    var id = seedConfig.GetProperty("id").GetString() ?? throw new Exception("Missing id in seedConfig");
                    var pk = seedConfig.GetProperty("pk").GetString() ?? throw new Exception("Missing pk in seedConfig");
                    var seedData = root.GetProperty("seedData").GetRawText();
                    _logger.LogInformation("Attempting to insert file: {File} (PK: {PK})", file, pk);
                    _logger.LogDebug("File content: {Content}", seedData);
                    var result = await inserter.UpsertDocumentAsync(id, pk, seedData);
                    if (result)
                    {
                        _logger.LogInformation("Seeded: {File}", file);
                        successCount++;
                    }
                    else
                    {
                        _logger.LogError("Failed: {File}", file);
                        failCount++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Exception while processing file: {File}", file);
                    failCount++;
                }
                current++;
                // Progress bar
                int barWidth = 40;
                double percent = (double)current / total;
                int pos = (int)(barWidth * percent);
                Console.Write("[" + new string('#', pos) + new string('-', barWidth - pos) + $"] {current}/{total} ({dbName})\r");
            }
            Console.WriteLine();
            _logger.LogInformation("Seeding complete for {DbName}. Success: {Success}, Failed: {Failed}, Total: {Total}", dbName, successCount, failCount, total);
        }
    }
}

class Program
{
    public const string EmulatorEndpoint = "https://localhost:8081";
    public const string EmulatorKey = "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";

    static async Task<int> Main(string[] args)
    {
        var services = new ServiceCollection();
        services.AddLogging(configure => configure.AddConsole());
        services.AddTransient<SeederService>();
        var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<Program>>();
        var parser = new Parser(with => with.HelpWriter = Console.Out);
        var result = parser.ParseArguments<Options>(args);
        int exitCode = 0;
        await result.WithParsedAsync(async options =>
        {
            switch (options.TargetType.ToLowerInvariant())
            {
                case "cosmos":
                    var seeder = provider.GetRequiredService<SeederService>();
                    await seeder.RunAsync(options.Path, options.DropAndCreate);
                    break;
                case "servicebus":
                    await ServiceBusSeeder.RunAsync(options.Path, logger);
                    break;
                case "redis":
                    logger.LogError("Redis seeding not implemented yet.");
                    break;
                default:
                    logger.LogError("Unknown targetType: {TargetType}", options.TargetType);
                    break;
            }
        });
        result.WithNotParsed(errors =>
        {
            logger.LogError("Invalid arguments. Use --help for usage.");
            exitCode = 1;
        });
        return exitCode;
    }

    public static async Task<bool> CreateDatabaseIfNotExistsAsync(HttpClient client, string dbName)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"/dbs");
        req.Headers.Add("x-ms-date", DateTime.UtcNow.ToString("r"));
        req.Headers.Add("x-ms-version", "2018-12-31");
        req.Headers.Add("Authorization", GenerateAuthToken("post", "dbs", string.Empty));
        req.Content = new StringContent($"{{\"id\":\"{dbName}\"}}", Encoding.UTF8, "application/json");
        var resp = await client.SendAsync(req);
        return resp.IsSuccessStatusCode || resp.StatusCode == System.Net.HttpStatusCode.Conflict;
    }

    public static async Task<bool> CreateContainerIfNotExistsAsync(HttpClient client, string dbName, string containerName)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"/dbs/{dbName}/colls");
        req.Headers.Add("x-ms-date", DateTime.UtcNow.ToString("r"));
        req.Headers.Add("x-ms-version", "2018-12-31");
        req.Headers.Add("Authorization", GenerateAuthToken("post", "colls", $"dbs/{dbName}"));
        // Partition key is now /pk
        req.Content = new StringContent($"{{\"id\":\"{containerName}\",\"partitionKey\":{{\"paths\":[\"/pk\"],\"kind\":\"Hash\"}}}}", Encoding.UTF8, "application/json");
        var resp = await client.SendAsync(req);
        return resp.IsSuccessStatusCode || resp.StatusCode == System.Net.HttpStatusCode.Conflict;
    }

    public static string GenerateAuthToken(string verb, string resourceType, string resourceId)
    {
        // Cosmos DB REST API HMAC auth implementation for emulator
        var key = Convert.FromBase64String(EmulatorKey);
        var utcDate = DateTime.UtcNow.ToString("r");
        var payload = $"{verb.ToLowerInvariant()}\n{resourceType.ToLowerInvariant()}\n{resourceId}\n{utcDate.ToLowerInvariant()}\n\n";
        using var hmacSha256 = new HMACSHA256(key);
        var hashPayload = hmacSha256.ComputeHash(Encoding.UTF8.GetBytes(payload));
        var signature = Convert.ToBase64String(hashPayload);
        var auth = Uri.EscapeDataString($"type=master&ver=1.0&sig={signature}");
        return auth;
    }

    public static async Task DropAndCreateContainerBatchAsync(HttpClient client, string dbName, string containerName)
    {
        var deleteReq = new HttpRequestMessage(HttpMethod.Delete, $"/dbs/{dbName}/colls/{containerName}");
        deleteReq.Headers.Add("x-ms-date", DateTime.UtcNow.ToString("r"));
        deleteReq.Headers.Add("x-ms-version", "2018-12-31");
        deleteReq.Headers.Add("Authorization", GenerateAuthToken("delete", "colls", $"dbs/{dbName}/colls/{containerName}"));
        await client.SendAsync(deleteReq);
        await CreateContainerIfNotExistsAsync(client, dbName, containerName);
    }
}
