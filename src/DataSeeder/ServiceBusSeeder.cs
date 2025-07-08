using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;

namespace DataSeeder;

public static class ServiceBusSeeder
{
    private static string ServiceBusConnectionString = "Endpoint=sb://localhost/;SharedAccessKeyName=admin;SharedAccessKey=admin;UseDevelopmentEmulator=true;";

    public static async Task RunAsync(string parentPath, ILogger logger)
    {
        var queueDir = Path.Combine(parentPath, "queue");
        var topicDir = Path.Combine(parentPath, "topic");
        if (!Directory.Exists(queueDir) && !Directory.Exists(topicDir))
        {
            logger.LogError("No queue or topic directories found in {Path}", parentPath);
            return;
        }

        var client = new ServiceBusClient(ServiceBusConnectionString);
        if (Directory.Exists(queueDir))
        {
            foreach (var file in Directory.GetFiles(queueDir, "*.json"))
            {
                Console.WriteLine($"processing queue file:{file}");
                await SendMessageFromFile(client, file, isQueue: true, logger);
                Console.WriteLine("message sent!");
            }
        }

        if (Directory.Exists(topicDir))
        {
            foreach (var file in Directory.GetFiles(topicDir, "*.json"))
            {
                Console.WriteLine($"processing topic file:{file}");
                await SendMessageFromFile(client, file, isQueue: false, logger);
                Console.WriteLine("message sent!");
            }
        }
    }

    private static async Task SendMessageFromFile(ServiceBusClient client, string file, bool isQueue, ILogger logger)
    {
        try
        {
            var json = await File.ReadAllTextAsync(file);
            Console.WriteLine("loading configuration");
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var def = root.GetProperty("defintion");
            var name = string.Empty;
            if (isQueue)
            {
                name = def.GetProperty("queueName").GetString() ?? throw new Exception("Missing queueName");
            }
            else
            {
                name = def.GetProperty("topicName").GetString() ?? throw new Exception("Missing topicName");
            }
            var customProps = root.GetProperty("msgCustomProperties");
            var msgData = root.GetProperty("msgData").GetRawText();
            var sender = client.CreateSender(name);
            var msg = new ServiceBusMessage(msgData);
            Console.WriteLine("loading applicationProperties from msgCustomProperties node");
            foreach (var prop in customProps.EnumerateObject())
            {
                msg.ApplicationProperties[prop.Name] = prop.Value.GetString();
            }

            Console.WriteLine($"Trying to send message to {name}");
            await sender.SendMessageAsync(msg);
            logger.LogInformation("Seeded {Type} '{Name}' from {File}", isQueue ? "queue" : "topic", name, file);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to seed message from {File}", file);
            Console.WriteLine(ex.Message);
        }
    }
}
