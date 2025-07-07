# Azure Data Seeder

A comprehensive utility for seeding data into Azure Cosmos DB and Azure Service Bus, with advanced partition key management, custom container support, and flexible data modeling.

## Features

- **CosmosDbInserter.cs**: Populates Cosmos DB with intelligent partition key management
- **ServiceBusSeeder.cs**: Sends messages to Azure Service Bus queues and topics
- **Flexible Partition Keys**: Supports both explicit and automatic (document ID) partition key strategies
- **Custom Container Names**: Override default container names using JSON configuration
- **Drop and Recreate**: Optional container recreation for clean testing environments
- **Mixed Data Support**: Handles containers with mixed partition key requirements

## Partition Key Strategy

The seeder automatically adapts to your data patterns:

### 1. Documents with Explicit Partition Keys

Documents that specify a `pk` field use that value as the partition key:

```json
{
  "seedConfig": {
    "id": "order-001",
    "pk": "customer-123",
    "db": "Orders"
  },
  "seedData": {
    "orderId": "order-001",
    "customerId": "customer-123",
    "amount": 100.5
  }
}
```

**Result**: Uses `"customer-123"` as the partition key.

### 2. Documents without Explicit Partition Keys

Documents without a `pk` field automatically use their document ID as the partition key:

```json
{
  "seedConfig": {
    "id": "purchase-6430176",
    "db": "Purchase"
  },
  "seedData": {
    "id": "purchase-6430176",
    "productId": 30193,
    "title": "Purchase 6430176"
  }
}
```

**Result**: Uses `"purchase-6430176"` (the document ID) as the partition key.

### 3. Custom Container Names

Documents can specify a custom container name to override the default (database name):

```json
{
  "seedConfig": {
    "id": "special-order-001",
    "db": "Orders",
    "container": "SpecialOrders"
  },
  "seedData": {
    "id": "special-order-001",
    "type": "priority",
    "status": "processing"
  }
}
```

**Result**: Document is inserted into the "SpecialOrders" container instead of the default "Orders" container.

### Benefits of Document ID as Partition Key

- **No partition size limits**: Each document gets its own partition (10GB per document vs 20GB per partition)
- **Simplified data modeling**: No need to design complex partition strategies
- **Automatic scaling**: Cosmos DB distributes documents across physical partitions automatically

## Usage

### Cosmos DB Seeding

#### Basic Seeding

```bash
# Windows
DataSeeder.exe -t cosmos -p .\SeedData\AzureCosmosData

# Linux/macOS
./DataSeeder -t cosmos -p ./SeedData/AzureCosmosData
```

#### Drop and Recreate Containers

```bash
# Windows
DataSeeder.exe -t cosmos -p .\SeedData\AzureCosmosData --drop

# Linux/macOS
./DataSeeder -t cosmos -p ./SeedData/AzureCosmosData --drop
```

#### Seed Specific Database

```bash
# Windows
DataSeeder.exe -t cosmos -p .\SeedData\AzureCosmosData -d Orders

# Linux/macOS
./DataSeeder -t cosmos -p ./SeedData/AzureCosmosData -d Orders
```

**Development (using dotnet run):**

```bash
# Note: The -- separator is required when using dotnet run to separate dotnet arguments from application arguments
dotnet run -- -t cosmos -p .\SeedData\AzureCosmosData --drop
```

### Service Bus Seeding

#### Basic Message Sending

```bash
# Windows
DataSeeder.exe -t servicebus -p .\SeedData\ServiceBusData

# Linux/macOS
./DataSeeder -t servicebus -p ./SeedData/ServiceBusData
```

## Command Line Options

| Option           | Required | Description                                        | Example                         |
| ---------------- | -------- | -------------------------------------------------- | ------------------------------- |
| -t, --targetType | Yes      | Target to seed: `cosmos`, `servicebus`, or `redis` | `-t cosmos`                     |
| -p, --path       | Yes      | Path to folder containing data or messages         | `-p .\SeedData\AzureCosmosData` |
| --drop           | No       | Drop and recreate containers (Cosmos only)         | `--drop`                        |
| -d, --db         | No       | Name of specific Cosmos DB database to seed        | `-d Orders`                     |

## Data File Structure

### Cosmos DB Data Structure

Your data should be organized in the following directory structure:

```
SeedData/
└── AzureCosmosData/
    ├── Orders/
    │   ├── order-001.json
    │   ├── order-002.json
    │   └── special-order.json  (custom container)
    ├── Products/
    │   ├── product-001.json
    │   └── product-002.json
    └── Documents/
        ├── doc-001.json
        └── doc-002.json
```

### Cosmos DB JSON Format

#### Standard Document (Uses Database Name as Container)

```json
{
  "seedConfig": {
    "id": "order-001",
    "pk": "customer-123",
    "db": "Orders"
  },
  "seedData": {
    "orderId": "order-001",
    "customerId": "customer-123",
    "amount": 100.5,
    "items": [{ "productId": "prod-001", "quantity": 2 }]
  }
}
```

#### Document with Custom Container Name

```json
{
  "seedConfig": {
    "id": "priority-order-001",
    "pk": "premium-customer-456",
    "db": "Orders",
    "container": "PriorityOrders"
  },
  "seedData": {
    "orderId": "priority-order-001",
    "customerId": "premium-customer-456",
    "priority": "high",
    "amount": 2500.0
  }
}
```

#### Document without Explicit Partition Key (Uses Document ID)

```json
{
  "seedConfig": {
    "id": "document-archive-001",
    "db": "DocumentArchive",
    "container": "LegacyDocuments"
  },
  "seedData": {
    "id": "document-archive-001",
    "title": "Important Document",
    "content": "Document content here...",
    "metadata": {
      "type": "pdf",
      "size": 1024000
    }
  }
}
```

### Service Bus Data Structure

```
SeedData/
└── ServiceBusData/
    ├── queue/
    │   ├── message-001.json
    │   └── message-002.json
    └── topic/
        ├── event-001.json
        └── event-002.json
```

### Service Bus JSON Format

#### Queue Message

```json
{
  "definition": {
    "queueName": "orders.processing"
  },
  "msgCustomProperties": {
    "messageType": "OrderCreated",
    "source": "OrderService",
    "correlationId": "abc-123"
  },
  "msgData": "{\"orderId\": \"order-001\", \"customerId\": \"customer-123\", \"amount\": 100.50}"
}
```

#### Topic Message

```json
{
  "definition": {
    "topicName": "events.orders"
  },
  "msgCustomProperties": {
    "eventType": "OrderStatusChanged",
    "version": "1.0"
  },
  "msgData": "{\"orderId\": \"order-001\", \"status\": \"shipped\", \"timestamp\": \"2025-07-06T10:00:00Z\"}"
}
```

## Container Strategy Detection

The seeder analyzes all JSON files in a database directory and groups them by container name:

- **Default Containers**: Documents without a `container` field use the database name as the container name
- **Custom Containers**: Documents with a `container` field create/use the specified container
- **Partition Key Strategy**: Each container independently determines its partition key strategy based on its documents

### Example Output

```
info: File example-withContainerName.json specifies custom container: 'OrderContainer'
info: Processing container 'Orders' with 2 documents
info: Container 'Orders' will be created with explicit partition keys
info: Processing container 'OrderContainer' with 1 documents
info: Container 'OrderContainer' will be created using document ID as partition key
info: Successfully inserted document 'order-001' with explicit partition key (pk='customer-123') into container 'Orders'
info: Successfully inserted document 'special-order' using document ID as partition key (pk='special-order') into container 'OrderContainer'
```

## Configuration

### Cosmos DB Emulator

- Default endpoint: `https://localhost:8081`
- Default key: Built-in emulator key
- Ensure the Cosmos DB emulator is running before seeding

### Service Bus Configuration

- Configure connection strings in `servicebus-config.json`
- Supports both Azure Service Bus and local emulator

### Docker Setup

Use the provided `docker-compose.yml` to run required emulators:

```bash
docker-compose up -d
```

## Examples

### Example 1: E-commerce Data with Mixed Containers and Partition Strategies

**Directory Structure:**

```
SeedData/AzureCosmosData/
└── Orders/
    ├── regular-order.json      (default container: "Orders", pk: "customer-123")
    ├── priority-order.json     (custom container: "PriorityOrders", pk: "premium-456")
    └── guest-order.json        (default container: "Orders", no pk - uses document ID)
```

**Command:**

```bash
DataSeeder.exe -t cosmos -p .\SeedData\AzureCosmosData --drop
```

**Result:** Creates two containers in the "Orders" database:

- **"Orders" container**: Contains regular-order.json and guest-order.json
- **"PriorityOrders" container**: Contains priority-order.json

### Example 2: Multi-Container Document Archive

**Directory Structure:**

```
SeedData/AzureCosmosData/
└── DocumentArchive/
    ├── current-doc.json        (container: "CurrentDocs")
    ├── legacy-doc.json         (container: "LegacyDocs")
    └── temp-doc.json          (default container: "DocumentArchive")
```

**Command:**

```bash
DataSeeder.exe -t cosmos -p .\SeedData\AzureCosmosData
```

**Result:** Creates three containers in the "DocumentArchive" database, each with document ID partitioning.

### Example 3: Service Bus Message Broadcasting

**Directory Structure:**

```
SeedData/ServiceBusData/
├── queue/
│   └── order-processing.json
└── topic/
    └── order-events.json
```

**Command:**

```bash
DataSeeder.exe -t servicebus -p .\SeedData\ServiceBusData
```

**Result:** Messages sent to respective queues and topics.

## Building and Running

### Prerequisites

- .NET 8.0 SDK
- Azure Cosmos DB Emulator or Azure Cosmos DB account
- Azure Service Bus Emulator or Azure Service Bus namespace (for Service Bus features)

### Build

```bash
dotnet build
```

### Run

```bash
dotnet run -- -t cosmos -p .\SeedData\AzureCosmosData --drop
```

## Troubleshooting

### Common Issues

1. **Cosmos DB Connection Failed**

   - Ensure Cosmos DB emulator is running
   - Check if port 8081 is available
   - Verify emulator certificate is trusted

2. **Partition Key Errors**

   - All documents are automatically assigned a partition key
   - Check JSON structure matches expected format
   - Use `--drop` flag to recreate containers with correct schema

3. **Container Name Issues**

   - Ensure custom container names are valid Cosmos DB identifiers
   - Container names are case-sensitive
   - Check that JSON structure includes proper `seedConfig` section

4. **Service Bus Connection Issues**
   - Verify `servicebus-config.json` configuration
   - Ensure Service Bus emulator or Azure namespace is accessible
   - Check connection string format

### Logging

The application provides detailed logging with timestamps including:

- Container grouping and creation strategy
- Document insertion results with container targeting
- Partition key assignments
- Custom container name detection
- Error details with context

**Log Format**: Each log entry includes a timestamp in the format `yyyy-MM-dd HH:mm:ss.fff` followed by the log level and message.

**Example Log Output**:

```text
2025-07-07 23:41:20.512 info: DataSeeder.SeederService[0]
      Seeding database: Orders
2025-07-07 23:41:20.650 info: DataSeeder.SeederService[0]
      Processing container 'Orders' with 2 documents
2025-07-07 23:41:20.651 info: DataSeeder.SeederService[0]
      Container 'Orders' will be created with explicit partition keys
```

Enable verbose logging by setting the log level in your environment or application configuration.

---

For additional support or feature requests, please check the project issues or create a new one.
