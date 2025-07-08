using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Polly;

namespace DataSeeder;

public class CosmosDbExporter
{
    private readonly HttpClient _client;
    private readonly string _endpoint;
    private readonly string _key;
    private readonly ILogger<CosmosDbExporter> _logger;
    private readonly int _pageSize;
    private readonly int _maxRU;
    private readonly bool _forceUpdate;

    public CosmosDbExporter(HttpClient client, string endpoint, string key, ILogger<CosmosDbExporter> logger, 
        int pageSize = 100, int maxRU = 400, bool forceUpdate = false)
    {
        _client = client;
        _endpoint = endpoint;
        _key = key;
        _logger = logger;
        _pageSize = Math.Min(pageSize, 1000); // Cap at 1000 for safety
        _maxRU = maxRU;
        _forceUpdate = forceUpdate;
    }

    public async Task<bool> ExportDatabaseAsync(string databaseName, string outputPath, string? specificContainer = null)
    {
        try
        {
            // Ensure output directory exists
            Directory.CreateDirectory(outputPath);
            
            // Get containers in the database
            var containers = await GetContainersAsync(databaseName);
            if (containers.Count == 0)
            {
                _logger.LogWarning("No containers found in database: {DatabaseName}", databaseName);
                return false;
            }

            // Filter to specific container if specified
            if (!string.IsNullOrEmpty(specificContainer))
            {
                containers = containers.Where(c => c.Equals(specificContainer, StringComparison.OrdinalIgnoreCase)).ToList();
                if (containers.Count == 0)
                {
                    _logger.LogError("Container '{ContainerName}' not found in database '{DatabaseName}'", specificContainer, databaseName);
                    return false;
                }
            }

            bool allSuccessful = true;
            foreach (var container in containers)
            {
                _logger.LogInformation("Exporting container: {ContainerName}", container);
                var success = await ExportContainerAsync(databaseName, container, outputPath);
                if (!success)
                {
                    allSuccessful = false;
                }
            }

            return allSuccessful;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error exporting database: {DatabaseName}", databaseName);
            return false;
        }
    }

    private async Task<List<string>> GetContainersAsync(string databaseName)
    {
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, $"/dbs/{databaseName}/colls");
            req.Headers.Add("x-ms-date", DateTime.UtcNow.ToString("r"));
            req.Headers.Add("x-ms-version", "2018-12-31");
            req.Headers.Add("Authorization", GenerateAuthToken("get", "colls", $"dbs/{databaseName}"));

            var response = await _client.SendAsync(req);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to get containers. Status: {StatusCode}, Response: {Response}", 
                    response.StatusCode, await response.Content.ReadAsStringAsync());
                return new List<string>();
            }

            var content = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(content);
            var containers = new List<string>();

            if (doc.RootElement.TryGetProperty("DocumentCollections", out var collections))
            {
                foreach (var collection in collections.EnumerateArray())
                {
                    if (collection.TryGetProperty("id", out var id))
                    {
                        containers.Add(id.GetString()!);
                    }
                }
            }

            return containers;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting containers for database: {DatabaseName}", databaseName);
            return new List<string>();
        }
    }

    private async Task<bool> ExportContainerAsync(string databaseName, string containerName, string outputPath)
    {
        try
        {
            // Create container output directory
            var containerPath = Path.Combine(outputPath, databaseName, containerName);
            Directory.CreateDirectory(containerPath);

            // Get container metadata to understand partition key
            var partitionKeyPath = await GetPartitionKeyPathAsync(databaseName, containerName);
            _logger.LogInformation("Container '{ContainerName}' partition key path: {PartitionKeyPath}", 
                containerName, partitionKeyPath ?? "None");

            // Get total document count for progress tracking
            var totalCount = await GetDocumentCountAsync(databaseName, containerName);
            _logger.LogInformation("Container '{ContainerName}' contains approximately {TotalCount} documents", 
                containerName, totalCount);

            // Use partition key optimization if available
            if (!string.IsNullOrEmpty(partitionKeyPath))
            {
                return await ExportContainerByPartitionKeyAsync(databaseName, containerName, containerPath, 
                    partitionKeyPath, totalCount);
            }
            else
            {
                return await ExportContainerByPagingAsync(databaseName, containerName, containerPath, 
                    partitionKeyPath, totalCount);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error exporting container: {ContainerName}", containerName);
            return false;
        }
    }

    private async Task<bool> ExportContainerByPartitionKeyAsync(string databaseName, string containerName, 
        string containerPath, string partitionKeyPath, int totalCount)
    {
        try
        {
            _logger.LogInformation("Using partition key optimization for container: {ContainerName}", containerName);
            
            // Get partition key ranges for optimal querying
            var partitionKeyRanges = await GetPartitionKeyRangesAsync(databaseName, containerName);
            _logger.LogInformation("Found {Count} partition key ranges", partitionKeyRanges.Count);

            int exportedCount = 0;
            int updatedCount = 0;
            int skippedCount = 0;
            double totalRUConsumed = 0;

            // Create progress tracking
            var progress = new Progress<ExportProgress>(p => 
            {
                var percentage = totalCount > 0 ? (p.ProcessedCount * 100.0) / totalCount : 0;
                _logger.LogInformation("Progress: {Percentage:F1}% ({ProcessedCount}/{TotalCount}) - " +
                    "Exported: {ExportedCount}, Updated: {UpdatedCount}, Skipped: {SkippedCount}, " +
                    "Total RU Consumed: {TotalRUConsumed:F2}", 
                    percentage, p.ProcessedCount, totalCount, p.ExportedCount, p.UpdatedCount, p.SkippedCount, p.RUConsumed);
            });

            // Process each partition key range
            foreach (var pkRange in partitionKeyRanges)
            {
                _logger.LogDebug("Processing partition key range: {MinInclusive} to {MaxExclusive}", 
                    pkRange.MinInclusive, pkRange.MaxExclusive);

                var rangeResult = await ExportPartitionKeyRangeAsync(databaseName, containerName, containerPath, 
                    partitionKeyPath, pkRange);

                if (rangeResult != null)
                {
                    exportedCount += rangeResult.ExportedCount;
                    updatedCount += rangeResult.UpdatedCount;
                    skippedCount += rangeResult.SkippedCount;
                    totalRUConsumed += rangeResult.RUConsumed;

                    // Report progress
                    ((IProgress<ExportProgress>)progress).Report(new ExportProgress
                    {
                        ProcessedCount = exportedCount + updatedCount + skippedCount,
                        ExportedCount = exportedCount,
                        UpdatedCount = updatedCount,
                        SkippedCount = skippedCount,
                        RUConsumed = totalRUConsumed
                    });
                }
            }

            _logger.LogInformation("Container '{ContainerName}' export completed. " +
                "Exported: {ExportedCount}, Updated: {UpdatedCount}, Skipped: {SkippedCount}, " +
                "Total RU Consumed: {TotalRUConsumed:F2}", 
                containerName, exportedCount, updatedCount, skippedCount, totalRUConsumed);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error exporting container by partition key: {ContainerName}", containerName);
            return false;
        }
    }

    private async Task<bool> ExportContainerByPagingAsync(string databaseName, string containerName, 
        string containerPath, string? partitionKeyPath, int totalCount)
    {
        try
        {
            _logger.LogInformation("Using standard paging for container: {ContainerName}", containerName);

            int exportedCount = 0;
            int updatedCount = 0;
            int skippedCount = 0;
            double totalRUConsumed = 0;
            string? continuationToken = null;
            
            // Adaptive page size based on RU consumption
            int currentPageSize = _pageSize;

            // Create progress tracking
            var progress = new Progress<ExportProgress>(p => 
            {
                var percentage = totalCount > 0 ? (p.ProcessedCount * 100.0) / totalCount : 0;
                _logger.LogInformation("Progress: {Percentage:F1}% ({ProcessedCount}/{TotalCount}) - " +
                    "Exported: {ExportedCount}, Updated: {UpdatedCount}, Skipped: {SkippedCount}, " +
                    "Current Page Size: {PageSize}, Total RU Consumed: {TotalRUConsumed:F2}", 
                    percentage, p.ProcessedCount, totalCount, p.ExportedCount, p.UpdatedCount, p.SkippedCount, 
                    currentPageSize, p.RUConsumed);
            });

            do
            {
                var pageResult = await ExportDocumentPageAsync(databaseName, containerName, containerPath, 
                    partitionKeyPath, continuationToken, currentPageSize);
                
                if (pageResult == null)
                {
                    _logger.LogError("Failed to export page for container: {ContainerName}", containerName);
                    return false;
                }

                exportedCount += pageResult.ExportedCount;
                updatedCount += pageResult.UpdatedCount;
                skippedCount += pageResult.SkippedCount;
                totalRUConsumed += pageResult.RUConsumed;
                continuationToken = pageResult.ContinuationToken;

                // Report progress
                ((IProgress<ExportProgress>)progress).Report(new ExportProgress
                {
                    ProcessedCount = exportedCount + updatedCount + skippedCount,
                    ExportedCount = exportedCount,
                    UpdatedCount = updatedCount,
                    SkippedCount = skippedCount,
                    RUConsumed = totalRUConsumed
                });

                // Adaptive RU management and page size adjustment
                var adaptiveResult = await ManageRUConsumptionAsync(pageResult.RUConsumed, currentPageSize);
                currentPageSize = adaptiveResult.NewPageSize;
                if (adaptiveResult.DelayMs > 0)
                {
                    await Task.Delay(adaptiveResult.DelayMs);
                }

            } while (!string.IsNullOrEmpty(continuationToken));

            _logger.LogInformation("Container '{ContainerName}' export completed. " +
                "Exported: {ExportedCount}, Updated: {UpdatedCount}, Skipped: {SkippedCount}, " +
                "Total RU Consumed: {TotalRUConsumed:F2}", 
                containerName, exportedCount, updatedCount, skippedCount, totalRUConsumed);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error exporting container by paging: {ContainerName}", containerName);
            return false;
        }
    }

    private async Task<string?> GetPartitionKeyPathAsync(string databaseName, string containerName)
    {
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, $"/dbs/{databaseName}/colls/{containerName}");
            req.Headers.Add("x-ms-date", DateTime.UtcNow.ToString("r"));
            req.Headers.Add("x-ms-version", "2018-12-31");
            req.Headers.Add("Authorization", GenerateAuthToken("get", "colls", $"dbs/{databaseName}/colls/{containerName}"));

            var response = await _client.SendAsync(req);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var content = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(content);
            
            if (doc.RootElement.TryGetProperty("partitionKey", out var partitionKey) &&
                partitionKey.TryGetProperty("paths", out var paths) &&
                paths.GetArrayLength() > 0)
            {
                return paths[0].GetString()?.TrimStart('/');
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting partition key for container: {ContainerName}", containerName);
            return null;
        }
    }

    private async Task<int> GetDocumentCountAsync(string databaseName, string containerName)
    {
        try
        {
            var query = "SELECT VALUE COUNT(1) FROM c";
            var req = new HttpRequestMessage(HttpMethod.Post, $"/dbs/{databaseName}/colls/{containerName}/docs");
            req.Headers.Add("x-ms-date", DateTime.UtcNow.ToString("r"));
            req.Headers.Add("x-ms-version", "2018-12-31");
            req.Headers.Add("x-ms-documentdb-isquery", "True");
            req.Headers.Add("Authorization", GenerateAuthToken("post", "docs", $"dbs/{databaseName}/colls/{containerName}"));

            var queryObj = new { query = query, parameters = new object[0] };
            req.Content = new StringContent(JsonSerializer.Serialize(queryObj), Encoding.UTF8, "application/json");

            var response = await _client.SendAsync(req);
            if (!response.IsSuccessStatusCode)
            {
                return 0;
            }

            var content = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(content);
            
            if (doc.RootElement.TryGetProperty("Documents", out var documents) &&
                documents.GetArrayLength() > 0)
            {
                return documents[0].GetInt32();
            }

            return 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting document count for container: {ContainerName}", containerName);
            return 0;
        }
    }

    private async Task<ExportPageResult?> ExportDocumentPageAsync(string databaseName, string containerName, 
        string containerPath, string? partitionKeyPath, string? continuationToken, int pageSize = 0)
    {
        try
        {
            int effectivePageSize = pageSize > 0 ? pageSize : _pageSize;
            
            var req = new HttpRequestMessage(HttpMethod.Get, $"/dbs/{databaseName}/colls/{containerName}/docs");
            req.Headers.Add("x-ms-date", DateTime.UtcNow.ToString("r"));
            req.Headers.Add("x-ms-version", "2018-12-31");
            req.Headers.Add("x-ms-max-item-count", effectivePageSize.ToString());
            req.Headers.Add("Authorization", GenerateAuthToken("get", "docs", $"dbs/{databaseName}/colls/{containerName}"));

            if (!string.IsNullOrEmpty(continuationToken))
            {
                req.Headers.Add("x-ms-continuation", continuationToken);
            }

            var response = await _client.SendAsync(req);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to get documents. Status: {StatusCode}, Response: {Response}", 
                    response.StatusCode, await response.Content.ReadAsStringAsync());
                return null;
            }

            var content = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(content);

            var result = new ExportPageResult
            {
                ContinuationToken = response.Headers.TryGetValues("x-ms-continuation", out var contValues) ? contValues.FirstOrDefault() : null,
                RUConsumed = double.Parse(response.Headers.TryGetValues("x-ms-request-charge", out var ruValues) ? ruValues.FirstOrDefault() ?? "0" : "0")
            };

            if (doc.RootElement.TryGetProperty("Documents", out var documents))
            {
                foreach (var document in documents.EnumerateArray())
                {
                    var exportResult = await ProcessDocumentAsync(document, containerPath, databaseName, 
                        containerName, partitionKeyPath);
                    
                    switch (exportResult)
                    {
                        case DocumentExportResult.Exported:
                            result.ExportedCount++;
                            break;
                        case DocumentExportResult.Updated:
                            result.UpdatedCount++;
                            break;
                        case DocumentExportResult.Skipped:
                            result.SkippedCount++;
                            break;
                    }
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error exporting document page for container: {ContainerName}", containerName);
            return null;
        }
    }

    private async Task<DocumentExportResult> ProcessDocumentAsync(JsonElement document, string containerPath, 
        string databaseName, string containerName, string? partitionKeyPath)
    {
        try
        {
            // Extract document ID
            if (!document.TryGetProperty("id", out var idElement))
            {
                _logger.LogWarning("Document without ID found in container: {ContainerName}", containerName);
                return DocumentExportResult.Skipped;
            }

            var documentId = idElement.GetString()!;
            var fileName = $"{documentId}.json";
            var filePath = Path.Combine(containerPath, fileName);

            // Extract partition key if available
            string? partitionKey = null;
            if (!string.IsNullOrEmpty(partitionKeyPath))
            {
                if (document.TryGetProperty(partitionKeyPath, out var pkElement))
                {
                    partitionKey = pkElement.GetString();
                }
            }

            // Create seed data structure
            var seedData = CreateSeedDataStructure(document, documentId, partitionKey, databaseName, containerName);

            // Check if file exists and compare content
            if (File.Exists(filePath) && !_forceUpdate)
            {
                var existingContent = await File.ReadAllTextAsync(filePath);
                if (string.Equals(existingContent.Trim(), seedData.Trim(), StringComparison.Ordinal))
                {
                    return DocumentExportResult.Skipped;
                }
            }

            // Write the file
            await File.WriteAllTextAsync(filePath, seedData);

            return File.Exists(filePath) && !_forceUpdate ? DocumentExportResult.Updated : DocumentExportResult.Exported;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing document in container: {ContainerName}", containerName);
            return DocumentExportResult.Skipped;
        }
    }

    private string CreateSeedDataStructure(JsonElement document, string documentId, string? partitionKey, 
        string databaseName, string containerName)
    {
        // Remove system properties from the document
        var cleanDocument = new Dictionary<string, JsonElement>();
        foreach (var property in document.EnumerateObject())
        {
            if (!property.Name.StartsWith("_")) // Skip system properties like _rid, _ts, _etag, etc.
            {
                cleanDocument[property.Name] = property.Value;
            }
        }

        // Create seed config
        var seedConfig = new Dictionary<string, object>
        {
            ["id"] = documentId,
            ["db"] = databaseName,
            ["container"] = containerName
        };

        if (!string.IsNullOrEmpty(partitionKey))
        {
            seedConfig["pk"] = partitionKey;
        }

        // Create the full seed data structure
        var seedDataStructure = new Dictionary<string, object>
        {
            ["seedConfig"] = seedConfig,
            ["seedData"] = cleanDocument
        };

        return JsonSerializer.Serialize(seedDataStructure, new JsonSerializerOptions 
        { 
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
    }

    private string GenerateAuthToken(string verb, string resourceType, string resourceId)
    {
        var key = Convert.FromBase64String(_key);
        var utcDate = DateTime.UtcNow.ToString("r");
        var payload = $"{verb.ToLowerInvariant()}\n{resourceType.ToLowerInvariant()}\n{resourceId}\n{utcDate.ToLowerInvariant()}\n\n";
        using var hmacSha256 = new HMACSHA256(key);
        var hashPayload = hmacSha256.ComputeHash(Encoding.UTF8.GetBytes(payload));
        var signature = Convert.ToBase64String(hashPayload);
        var auth = Uri.EscapeDataString($"type=master&ver=1.0&sig={signature}");
        return auth;
    }

    private async Task<List<PartitionKeyRange>> GetPartitionKeyRangesAsync(string databaseName, string containerName)
    {
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, $"/dbs/{databaseName}/colls/{containerName}/pkranges");
            req.Headers.Add("x-ms-date", DateTime.UtcNow.ToString("r"));
            req.Headers.Add("x-ms-version", "2018-12-31");
            req.Headers.Add("Authorization", GenerateAuthToken("get", "pkranges", $"dbs/{databaseName}/colls/{containerName}"));

            var response = await _client.SendAsync(req);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to get partition key ranges. Status: {StatusCode}", response.StatusCode);
                return new List<PartitionKeyRange>();
            }

            var content = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(content);
            var ranges = new List<PartitionKeyRange>();

            if (doc.RootElement.TryGetProperty("PartitionKeyRanges", out var pkRanges))
            {
                foreach (var range in pkRanges.EnumerateArray())
                {
                    if (range.TryGetProperty("minInclusive", out var minInclusive) &&
                        range.TryGetProperty("maxExclusive", out var maxExclusive) &&
                        range.TryGetProperty("id", out var id))
                    {
                        ranges.Add(new PartitionKeyRange
                        {
                            Id = id.GetString()!,
                            MinInclusive = minInclusive.GetString()!,
                            MaxExclusive = maxExclusive.GetString()!
                        });
                    }
                }
            }

            return ranges;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting partition key ranges for container: {ContainerName}", containerName);
            return new List<PartitionKeyRange>();
        }
    }

    private async Task<ExportPageResult?> ExportPartitionKeyRangeAsync(string databaseName, string containerName,
        string containerPath, string partitionKeyPath, PartitionKeyRange pkRange)
    {
        try
        {
            var totalResult = new ExportPageResult { RUConsumed = 0 };
            string? continuationToken = null;
            int currentPageSize = _pageSize;

            do
            {
                // Use a targeted query for this partition key range
                var query = $"SELECT * FROM c";
                var req = new HttpRequestMessage(HttpMethod.Post, $"/dbs/{databaseName}/colls/{containerName}/docs");
                req.Headers.Add("x-ms-date", DateTime.UtcNow.ToString("r"));
                req.Headers.Add("x-ms-version", "2018-12-31");
                req.Headers.Add("x-ms-documentdb-isquery", "True");
                req.Headers.Add("x-ms-max-item-count", currentPageSize.ToString());
                req.Headers.Add("x-ms-documentdb-partitionkeyrangeid", pkRange.Id);
                req.Headers.Add("Authorization", GenerateAuthToken("post", "docs", $"dbs/{databaseName}/colls/{containerName}"));

                if (!string.IsNullOrEmpty(continuationToken))
                {
                    req.Headers.Add("x-ms-continuation", continuationToken);
                }

                var queryObj = new { query = query, parameters = new object[0] };
                req.Content = new StringContent(JsonSerializer.Serialize(queryObj), Encoding.UTF8, "application/json");

                var response = await _client.SendAsync(req);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError("Failed to query partition key range {RangeId}. Status: {StatusCode}", 
                        pkRange.Id, response.StatusCode);
                    break;
                }

                var content = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(content);

                var ruConsumed = double.Parse(response.Headers.TryGetValues("x-ms-request-charge", out var ruValues) ? ruValues.FirstOrDefault() ?? "0" : "0");
                totalResult.RUConsumed += ruConsumed;

                continuationToken = response.Headers.TryGetValues("x-ms-continuation", out var contValues) ? contValues.FirstOrDefault() : null;

                if (doc.RootElement.TryGetProperty("Documents", out var documents))
                {
                    foreach (var document in documents.EnumerateArray())
                    {
                        var exportResult = await ProcessDocumentAsync(document, containerPath, databaseName, 
                            containerName, partitionKeyPath);
                        
                        switch (exportResult)
                        {
                            case DocumentExportResult.Exported:
                                totalResult.ExportedCount++;
                                break;
                            case DocumentExportResult.Updated:
                                totalResult.UpdatedCount++;
                                break;
                            case DocumentExportResult.Skipped:
                                totalResult.SkippedCount++;
                                break;
                        }
                    }
                }

                // Adaptive RU management
                var adaptiveResult = await ManageRUConsumptionAsync(ruConsumed, currentPageSize);
                currentPageSize = adaptiveResult.NewPageSize;
                if (adaptiveResult.DelayMs > 0)
                {
                    await Task.Delay(adaptiveResult.DelayMs);
                }

            } while (!string.IsNullOrEmpty(continuationToken));

            return totalResult;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error exporting partition key range {RangeId} for container: {ContainerName}", 
                pkRange.Id, containerName);
            return null;
        }
    }

    private async Task<RUManagementResult> ManageRUConsumptionAsync(double ruConsumed, int currentPageSize)
    {
        // Simulate async operation (in real implementation, this might check external throttling APIs)
        await Task.Delay(1);
        
        // Adaptive RU management algorithm
        if (ruConsumed > _maxRU)
        {
            // Reduce page size to consume fewer RUs per request
            var newPageSize = Math.Max(10, (int)(currentPageSize * 0.7)); // Reduce by 30%, minimum 10
            
            // Calculate delay based on excess RUs
            var excessRU = ruConsumed - _maxRU;
            var delayMs = (int)(excessRU * 100); // 100ms per excess RU
            
            _logger.LogDebug("RU throttling: consumed {RUConsumed} RUs (limit: {MaxRU}), " +
                "reducing page size to {PageSize}, waiting {DelayMs}ms", 
                ruConsumed, _maxRU, newPageSize, delayMs);
            
            return new RUManagementResult { NewPageSize = newPageSize, DelayMs = delayMs };
        }
        else if (ruConsumed < _maxRU * 0.5 && currentPageSize < _pageSize)
        {
            // If we're consuming less than 50% of max RUs, we can increase page size
            var newPageSize = Math.Min(_pageSize, (int)(currentPageSize * 1.2)); // Increase by 20%
            _logger.LogDebug("RU optimization: consumed {RUConsumed} RUs (limit: {MaxRU}), " +
                "increasing page size to {PageSize}", 
                ruConsumed, _maxRU, newPageSize);
                
            return new RUManagementResult { NewPageSize = newPageSize, DelayMs = 0 };
        }

        return new RUManagementResult { NewPageSize = currentPageSize, DelayMs = 0 };
    }
}

public class ExportProgress
{
    public int ProcessedCount { get; set; }
    public int ExportedCount { get; set; }
    public int UpdatedCount { get; set; }
    public int SkippedCount { get; set; }
    public double RUConsumed { get; set; }
}

public class ExportPageResult
{
    public string? ContinuationToken { get; set; }
    public double RUConsumed { get; set; }
    public int ExportedCount { get; set; }
    public int UpdatedCount { get; set; }
    public int SkippedCount { get; set; }
}

public enum DocumentExportResult
{
    Exported,
    Updated,
    Skipped
}

public class PartitionKeyRange
{
    public string Id { get; set; } = string.Empty;
    public string MinInclusive { get; set; } = string.Empty;
    public string MaxExclusive { get; set; } = string.Empty;
}

public class RUManagementResult
{
    public int NewPageSize { get; set; }
    public int DelayMs { get; set; }
}
