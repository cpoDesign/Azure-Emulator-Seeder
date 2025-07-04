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

    public async Task<bool> UpsertDocumentAsync(string id, string partitionKey, string jsonDoc)
    {
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
            // If pk property was missing, add it
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
                Console.WriteLine($"Upsert failed for document in container '{_containerName}':");
                Console.WriteLine($"Status: {resp.StatusCode} {resp.ReasonPhrase}");
                Console.WriteLine($"Response: {responseContent}");
                Console.ResetColor();
            }
        }
        return resp.IsSuccessStatusCode || (resp.StatusCode == System.Net.HttpStatusCode.Conflict && responseContent.Contains("Resource with specified id or name already exists."));
    }
}
