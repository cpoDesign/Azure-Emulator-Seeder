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

    [Option('t', "targetType", Required = true, HelpText = "Target type to seed: cosmos, redis, or serviceBus.")]
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

            // Group documents by container name (from JSON or default to dbName)
            var containerGroups = await GroupDocumentsByContainer(jsonFiles, dbName);
            
            foreach (var containerGroup in containerGroups)
            {
                var containerName = containerGroup.Key;
                var files = containerGroup.Value;
                
                _logger.LogInformation("Processing container '{Container}' with {FileCount} documents", containerName, files.Count);
                
                // Analyze if any documents in this container need partition keys
                bool needsPartitionKey = await DoesContainerNeedPartitionKey(files.ToArray());
                string partitionStrategy = needsPartitionKey ? "with explicit partition keys" : "using document ID as partition key";
                _logger.LogInformation("Container '{Container}' will be created {PartitionKeyStatus}", 
                    containerName, partitionStrategy);
                
                if (dropAndCreate)
                {
                    _logger.LogInformation("Dropping and recreating container '{Container}' in database '{Db}'...", containerName, dbName);
                    await Program.DropAndCreateContainerBatchAsync(httpClient, dbName, containerName, needsPartitionKey);
                }
                else
                {
                    await Program.CreateContainerIfNotExistsAsync(httpClient, dbName, containerName, needsPartitionKey);
                }
                
                var inserter = new CosmosDbInserter(httpClient, dbName, containerName);
                int successCount = 0;
                int failCount = 0;
                int total = files.Count;
                int current = 0;
                
                foreach (var file in files)
                {
                    try
                    {
                        var fileContent = await File.ReadAllTextAsync(file);
                        using var doc = JsonDocument.Parse(fileContent);
                        var root = doc.RootElement;
                        var seedConfig = root.GetProperty("seedConfig");
                        var id = seedConfig.GetProperty("id").GetString() ?? throw new Exception("Missing id in seedConfig");
                        
                        // Make partition key optional - can be null or empty for documents without partitioning
                        string? pk = null;
                        if (seedConfig.TryGetProperty("pk", out var pkProperty))
                        {
                            pk = pkProperty.GetString();
                            // Treat empty string as null for partition key
                            if (string.IsNullOrEmpty(pk))
                            {
                                pk = null;
                            }
                        }
                        
                        var seedData = root.GetProperty("seedData").GetRawText();
                        var pkDisplay = pk ?? "none";
                        _logger.LogInformation("Attempting to insert file: {File} into container '{Container}' (PK: {PK})", 
                            Path.GetFileName(file), containerName, pkDisplay);
                        _logger.LogDebug("File content: {Content}", seedData);
                        var result = await inserter.UpsertDocumentAsync(id, pk, seedData);
                        if (result)
                        {
                            _logger.LogInformation("Seeded: {File}", Path.GetFileName(file));
                            successCount++;
                        }
                        else
                        {
                            _logger.LogError("Failed: {File}", Path.GetFileName(file));
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
                    Console.Write("[" + new string('#', pos) + new string('-', barWidth - pos) + $"] {current}/{total} ({containerName})\r");
                }
                Console.WriteLine();
                _logger.LogInformation("Seeding complete for container '{Container}' in database '{DbName}'. Success: {Success}, Failed: {Failed}, Total: {Total}", 
                    containerName, dbName, successCount, failCount, total);
            }
        }
    }

    /// <summary>
    /// Analyzes JSON files in a directory to determine if any documents have explicit partition keys
    /// </summary>
    /// <param name="jsonFiles">Array of JSON file paths to analyze</param>
    /// <returns>True if any document has an explicit partition key, false if all documents will use document ID as partition key</returns>
    private async Task<bool> DoesContainerNeedPartitionKey(string[] jsonFiles)
    {
        foreach (var file in jsonFiles)
        {
            try
            {
                var fileContent = await File.ReadAllTextAsync(file);
                using var doc = JsonDocument.Parse(fileContent);
                var root = doc.RootElement;
                var seedConfig = root.GetProperty("seedConfig");
                
                // Check if this document has an explicit partition key in seedConfig
                if (seedConfig.TryGetProperty("pk", out var pkProperty))
                {
                    var pk = pkProperty.GetString();
                    // Only consider it an explicit partition key if it's not empty
                    if (!string.IsNullOrEmpty(pk))
                    {
                        return true; // Found at least one document with an explicit partition key
                    }
                }
                
                // Also check the seedData section in case pk was added there
                if (root.TryGetProperty("seedData", out var seedData) && seedData.ValueKind == JsonValueKind.Object)
                {
                    if (seedData.TryGetProperty("pk", out var dataPkProperty))
                    {
                        var dataPk = dataPkProperty.GetString();
                        // Only consider it an explicit partition key if it's not empty
                        if (!string.IsNullOrEmpty(dataPk))
                        {
                            return true; // Found at least one document with an explicit partition key
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Could not analyze file {File} for partition key detection: {Error}", file, ex.Message);
                // If we can't parse a file, assume it might need a partition key to be safe
                return true;
            }
        }
        
        return false; // No documents have explicit partition keys - all will use document ID as partition key
    }

    /// <summary>
    /// Groups documents by their container name, extracting from JSON or using default database name
    /// </summary>
    /// <param name="jsonFiles">Array of JSON file paths to analyze</param>
    /// <param name="defaultContainerName">Default container name to use if not specified in JSON</param>
    /// <returns>Dictionary where key is container name and value is list of file paths</returns>
    private async Task<Dictionary<string, List<string>>> GroupDocumentsByContainer(string[] jsonFiles, string defaultContainerName)
    {
        var containerGroups = new Dictionary<string, List<string>>();
        
        foreach (var file in jsonFiles)
        {
            try
            {
                var fileContent = await File.ReadAllTextAsync(file);
                using var doc = JsonDocument.Parse(fileContent);
                var root = doc.RootElement;
                var seedConfig = root.GetProperty("seedConfig");
                
                // Extract container name from JSON or use default
                string containerName = defaultContainerName;
                if (seedConfig.TryGetProperty("container", out var containerProperty))
                {
                    var customContainer = containerProperty.GetString();
                    if (!string.IsNullOrEmpty(customContainer))
                    {
                        containerName = customContainer;
                        _logger.LogInformation("File {File} specifies custom container: '{Container}'", 
                            Path.GetFileName(file), containerName);
                    }
                }
                
                // Add file to the appropriate container group
                if (!containerGroups.ContainsKey(containerName))
                {
                    containerGroups[containerName] = new List<string>();
                }
                containerGroups[containerName].Add(file);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Could not parse file {File} for container grouping: {Error}. Using default container.", 
                    Path.GetFileName(file), ex.Message);
                // Use default container for files that can't be parsed
                if (!containerGroups.ContainsKey(defaultContainerName))
                {
                    containerGroups[defaultContainerName] = new List<string>();
                }
                containerGroups[defaultContainerName].Add(file);
            }
        }
        
        return containerGroups;
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
}
