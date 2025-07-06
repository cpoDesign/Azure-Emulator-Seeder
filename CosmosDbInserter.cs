using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace DataSeeder;

public class CosmosDbInserter
{
    private readonly HttpClient _client;
    private readonly string _dbName;
    private readonly string _containerName;

    public CosmosDbInserter(HttpClient client, string dbName, string containerName)
    {
        _client = client;
        _dbName = dbName;
        _containerName = containerName;
    }

    /// <summary>
    /// Inserts or updates a document in the Cosmos DB container.
    /// </summary>
    /// <param name="id">The document ID (required)</param>
    /// <param name="partitionKey">The partition key value. If null or empty, the document ID will be used as the partition key.</param>
    /// <param name="jsonDoc">The JSON document content to insert</param>
    /// <returns>True if the operation was successful, false otherwise</returns>
    public async Task<bool> UpsertDocumentAsync(string id, string? partitionKey, string jsonDoc)
    {
        // Use document ID as partition key if none provided (each document gets its own partition)
        bool wasPartitionKeyProvided = !string.IsNullOrEmpty(partitionKey);
        if (!wasPartitionKeyProvided)
        {
            partitionKey = id; // Use document ID as partition key
        }

        // Ensure id and pk are strings in the document
        using var doc = JsonDocument.Parse(jsonDoc);
        var root = doc.RootElement;
        string? docId = null;
        string? docPk = null;
        
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            foreach (var property in root.EnumerateObject())
            {
                if (property.NameEquals("id"))
                {
                    docId = id;
                    writer.WriteString("id", id);
                }
                else if (property.NameEquals("pk"))
                {
                    docPk = partitionKey;
                    writer.WriteString("pk", partitionKey);
                }
                else
                {
                    property.WriteTo(writer);
                }
            }
            
            // Add pk property if not already present
            if (docPk == null)
            {
                writer.WriteString("pk", partitionKey);
            }
            
            // If id property was missing, add it
            if (docId == null)
            {
                writer.WriteString("id", id);
            }
            writer.WriteEndObject();
        }
        
        jsonDoc = Encoding.UTF8.GetString(ms.ToArray());
        var req = new HttpRequestMessage(HttpMethod.Post, $"/dbs/{_dbName}/colls/{_containerName}/docs");
        req.Headers.Add("x-ms-date", DateTime.UtcNow.ToString("r"));
        req.Headers.Add("x-ms-version", "2018-12-31");
        req.Headers.Add("Authorization", Program.GenerateAuthToken("post", "docs", $"dbs/{_dbName}/colls/{_containerName}"));
        
        // Always add partition key headers (using document ID if none was provided)
        req.Headers.Add("x-ms-partitionkey", $"[\"{partitionKey}\"]");
        req.Headers.Add("x-ms-documentdb-partitionkey", $"[\"{partitionKey}\"]");
        
        req.Content = new StringContent(jsonDoc, Encoding.UTF8, "application/json");
        var resp = await _client.SendAsync(req);
        string responseContent = string.Empty;
        
        if (!resp.IsSuccessStatusCode)
        {
            responseContent = await resp.Content.ReadAsStringAsync();
            if (resp.StatusCode == System.Net.HttpStatusCode.Conflict && responseContent.Contains("Resource with specified id or name already exists."))
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"Warning: Document with id '{id}' already exists in container '{_containerName}'.");
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Insert failed for document in container '{_containerName}':");
                Console.WriteLine($"Document ID: {id}");
                Console.WriteLine($"Original Partition Key Provided: {(wasPartitionKeyProvided ? "Yes" : "No (using document ID)")}");
                Console.WriteLine($"Actual Partition Key Used: {partitionKey}");
                Console.WriteLine($"Status: {resp.StatusCode} {resp.ReasonPhrase}");
                Console.WriteLine($"Response: {responseContent}");
                Console.ResetColor();
            }
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Green;
            var insertType = wasPartitionKeyProvided ? "with explicit partition key" : "using document ID as partition key";
            Console.WriteLine($"Successfully inserted document '{id}' {insertType} (pk='{partitionKey}') into container '{_containerName}'");
            Console.ResetColor();
        }
        return resp.IsSuccessStatusCode || (resp.StatusCode == System.Net.HttpStatusCode.Conflict && responseContent.Contains("Resource with specified id or name already exists."));
    }
}
