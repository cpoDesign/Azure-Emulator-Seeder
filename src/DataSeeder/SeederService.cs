using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace DataSeeder;

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
            
            // Analyze if any documents need partition keys
            bool needsPartitionKey = await DoesContainerNeedPartitionKey(jsonFiles);
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
                    _logger.LogInformation("Attempting to insert file: {File} (PK: {PK})", file, pkDisplay);
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
}
