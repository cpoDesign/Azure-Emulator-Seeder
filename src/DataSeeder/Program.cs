using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DataSeeder;

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
                    // Validate options based on source type
                    if (options.SourceType.ToLowerInvariant() == "cosmos")
                    {
                        // Reverse seeding: from Cosmos DB to files
                        await HandleReverseSeeding(options, logger);
                    }
                    else
                    {
                        // Forward seeding: from files to Cosmos DB
                        var seeder = provider.GetRequiredService<SeederService>();
                        var targetDatabase = string.IsNullOrEmpty(options.Database) ? null : options.Database;
                        await seeder.RunAsync(options.Path, options.DropAndCreate, targetDatabase);
                    }
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

    public static async Task<bool> CreateContainerIfNotExistsAsync(HttpClient client, string dbName, string containerName, bool needsPartitionKey = true)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"/dbs/{dbName}/colls");
        req.Headers.Add("x-ms-date", DateTime.UtcNow.ToString("r"));
        req.Headers.Add("x-ms-version", "2018-12-31");
        req.Headers.Add("Authorization", GenerateAuthToken("post", "colls", $"dbs/{dbName}"));
        
        // All containers need partition keys in modern Cosmos DB
        // Documents without explicit partition keys will use their document ID as the partition key
        string containerDefinition = $"{{\"id\":\"{containerName}\",\"partitionKey\":{{\"paths\":[\"/pk\"],\"kind\":\"Hash\"}}}}";
        
        req.Content = new StringContent(containerDefinition, Encoding.UTF8, "application/json");
        
        var resp = await client.SendAsync(req);
        if (resp.IsSuccessStatusCode || resp.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            // Give container time to be fully ready
            await Task.Delay(1000);
            return true;
        }
        else
        {
            var errorContent = await resp.Content.ReadAsStringAsync();
            Console.WriteLine($"Failed to create container '{containerName}': {resp.StatusCode} - {errorContent}");
            return false;
        }
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

    public static async Task DropAndCreateContainerBatchAsync(HttpClient client, string dbName, string containerName, bool needsPartitionKey = true)
    {
        var deleteReq = new HttpRequestMessage(HttpMethod.Delete, $"/dbs/{dbName}/colls/{containerName}");
        deleteReq.Headers.Add("x-ms-date", DateTime.UtcNow.ToString("r"));
        deleteReq.Headers.Add("x-ms-version", "2018-12-31");
        deleteReq.Headers.Add("Authorization", GenerateAuthToken("delete", "colls", $"dbs/{dbName}/colls/{containerName}"));
        await client.SendAsync(deleteReq);
        await CreateContainerIfNotExistsAsync(client, dbName, containerName, needsPartitionKey);
    }

    private static async Task HandleReverseSeeding(Options options, ILogger<Program> logger)
    {
        // Validate required options for reverse seeding
        if (string.IsNullOrEmpty(options.Database))
        {
            logger.LogError("Database name (-d) is required for reverse seeding.");
            return;
        }

        string endpoint;
        string key;

        if (options.UseManagedIdentity)
        {
            logger.LogError("Managed identity authentication not yet implemented.");
            return;
        }

        if (!string.IsNullOrEmpty(options.ConnectionString))
        {
            // Parse connection string to extract endpoint and key
            var (parsedEndpoint, parsedKey) = ParseConnectionString(options.ConnectionString);
            if (string.IsNullOrEmpty(parsedEndpoint) || string.IsNullOrEmpty(parsedKey))
            {
                logger.LogError("Invalid connection string format.");
                return;
            }
            endpoint = parsedEndpoint;
            key = parsedKey;
        }
        else
        {
            // Default to emulator
            endpoint = EmulatorEndpoint;
            key = EmulatorKey;
            logger.LogInformation("Using Cosmos DB Emulator for reverse seeding.");
        }

        // Create HTTP client
        using var httpClient = new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        });
        httpClient.BaseAddress = new Uri(endpoint);

        // Create logger for exporter
        var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
        var exporterLogger = loggerFactory.CreateLogger<CosmosDbExporter>();

        // Create exporter with options
        var exporter = new CosmosDbExporter(
            httpClient, 
            endpoint, 
            key, 
            exporterLogger,
            options.PageSize,
            options.MaxRU,
            options.ForceUpdate
        );

        // Start export
        logger.LogInformation("Starting reverse seeding from Cosmos DB...");
        logger.LogInformation("Database: {Database}", options.Database);
        logger.LogInformation("Container: {Container}", string.IsNullOrEmpty(options.Container) ? "All" : options.Container);
        logger.LogInformation("Output Path: {Path}", options.Path);
        logger.LogInformation("Page Size: {PageSize}", options.PageSize);
        logger.LogInformation("Max RU/s: {MaxRU}", options.MaxRU);
        logger.LogInformation("Force Update: {ForceUpdate}", options.ForceUpdate);

        var success = await exporter.ExportDatabaseAsync(options.Database, options.Path, options.Container);
        
        if (success)
        {
            logger.LogInformation("Reverse seeding completed successfully.");
        }
        else
        {
            logger.LogError("Reverse seeding failed.");
        }
    }

    private static (string endpoint, string key) ParseConnectionString(string connectionString)
    {
        try
        {
            var parts = connectionString.Split(';');
            string endpoint = "";
            string key = "";

            foreach (var part in parts)
            {
                var trimmedPart = part.Trim();
                if (trimmedPart.StartsWith("AccountEndpoint=", StringComparison.OrdinalIgnoreCase))
                {
                    endpoint = trimmedPart.Substring("AccountEndpoint=".Length);
                }
                else if (trimmedPart.StartsWith("AccountKey=", StringComparison.OrdinalIgnoreCase))
                {
                    key = trimmedPart.Substring("AccountKey=".Length);
                }
            }

            return (endpoint, key);
        }
        catch (Exception)
        {
            return ("", "");
        }
    }
}
