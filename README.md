# DataSeeder Project

This project provides utilities for seeding data into Azure Cosmos DB and Azure Service Bus (using the Azure Service Bus Emulator for local development).

## Features

- **CosmosDbInserter.cs**: Populates Cosmos DB with sample data from the `AzureCosmosData` folder.
- **ServiceBusSeeder.cs**: Pushes messages to Azure Service Bus queues and topics using data from the `ServiceBusData` folder.

## Usage

### 1. Seeding Cosmos DB

- Place your sample data JSON files in the appropriate subfolders under `AzureCosmosData/` (e.g., `Auctions/`, `Lots/`).
- Each JSON file should contain a `seedConfig` (with `id` and `pk`) and a `seedData` object.
- Run the Cosmos DB seeder utility to insert this data into your local Cosmos DB emulator or a configured Cosmos DB instance.

#### Example Command

**Linux:**

```bash
# Seed Cosmos DB with data from AzureCosmosData, drop and recreate containers
./DataSeeder -t cosmos -p ./AzureCosmosData --drop
```

**Windows:**

```powershell
# Seed Cosmos DB with data from AzureCosmosData, drop and recreate containers
DataSeeder.exe -t cosmos -p .\AzureCosmosData --drop
```

**Development (using dotnet run):**

```bash
# Note: The -- separator is required when using dotnet run to separate dotnet arguments from application arguments
dotnet run -- -t cosmos -p .\AzureCosmosData --drop
```

### 2. Seeding Azure Service Bus

- Place your message JSON files in the `ServiceBusData/queue/` or `ServiceBusData/topic/` folders.
- Each message file should contain a `defintion` (with `queueName` or `topicName`), `msgCustomProperties`, and `msgData`.
- Run the Service Bus seeder utility to send these messages to the configured Azure Service Bus (or emulator).

#### Example Command

**Linux:**

```bash
./DataSeeder --targetType servicebus --path ./ServiceBusData
```

**Windows:**

```powershell
DataSeeder.exe --targetType servicebus --path .\ServiceBusData
```

### 3. Drop and Recreate Cosmos Containers

You can use the `--drop` flag to drop and recreate containers before seeding:

**Linux:**

```bash
./DataSeeder --targetType cosmos --path ./AzureCosmosData --drop
```

**Windows:**

```powershell
DataSeeder.exe --targetType cosmos --path .\AzureCosmosData --drop
```

## Command Line Options

| Option                | Description                                                       |
| --------------------- | ----------------------------------------------------------------- |
| -t, --targetType      | Target to seed: `cosmos`, `servicebus`, or `redis` (not yet impl) |
| -p, --path            | Path to folder containing data or messages                        |
| --drop                | (Optional) Drop and recreate containers (Cosmos only)             |
| -d, --db              | (Optional) Name of the Cosmos DB database                         |

## Configuration

- Ensure your local Cosmos DB emulator and Azure Service Bus emulator are running (see the root `docker-compose.yml` for setup).
- Update connection strings in the code if your emulator is running on a different host or with different credentials.

## Example Data File Structure

**Cosmos DB JSON Example:**

```json
{
  "seedConfig": {
    "id": "item1",
    "pk": "partition1"
  },
  "seedData": {
    "property1": "value1",
    "property2": 123
  }
}
```

**Service Bus JSON Example:**

```json
{
  "defintion": {
    "queueName": "queue.1"
  },
  "msgCustomProperties": {
    "type": "MyType"
  },
  "msgData": "{ \"Id\": 123, \"AuctionId\": 456 }"
}
```

---

For more details, see the comments in each utility class.
