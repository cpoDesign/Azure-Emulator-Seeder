using System.Text.Json;
using Microsoft.Extensions.Logging;
using Moq;

namespace DataSeeder.Unit.Tests
{
    public class SeederServiceTests : IDisposable
    {
        private readonly Mock<ILogger<SeederService>> _mockLogger;
        private readonly SeederService _seederService;
        private readonly string _testDirectory;

        public SeederServiceTests()
        {
            _mockLogger = new Mock<ILogger<SeederService>>();
            _seederService = new SeederService(_mockLogger.Object);
            _testDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(_testDirectory);
        }

        [Fact]
        public async Task GroupFilesByContainer_ShouldUseFallbackContainerName_WhenNoContainerSpecified()
        {
            // Arrange
            var jsonContent = """
                {
                  "seedConfig": {
                    "id": "test-001",
                    "db": "TestDB"
                  },
                  "seedData": {
                    "id": "test-001",
                    "title": "Test Document"
                  }
                }
                """;

            var testFile = Path.Combine(_testDirectory, "test1.json");
            await File.WriteAllTextAsync(testFile, jsonContent);
            var jsonFiles = new[] { testFile };
            var fallbackContainerName = "TestDB";

            // Act
            var result = await _seederService.GroupFilesByContainer(jsonFiles, fallbackContainerName);

            // Assert
            Assert.Single(result);
            Assert.True(result.ContainsKey("TestDB"));
            Assert.Single(result["TestDB"]);
            Assert.Equal(testFile, result["TestDB"][0]);

            // Cleanup
            File.Delete(testFile);
        }

        [Fact]
        public async Task GroupFilesByContainer_ShouldUseSpecifiedContainerName_WhenContainerProvided()
        {
            // Arrange
            var jsonContent = """
                {
                  "seedConfig": {
                    "id": "test-001",
                    "db": "TestDB",
                    "container": "SpecificContainer"
                  },
                  "seedData": {
                    "id": "test-001",
                    "title": "Test Document"
                  }
                }
                """;

            var testFile = Path.Combine(_testDirectory, "test2.json");
            await File.WriteAllTextAsync(testFile, jsonContent);
            var jsonFiles = new[] { testFile };
            var fallbackContainerName = "TestDB";

            // Act
            var result = await _seederService.GroupFilesByContainer(jsonFiles, fallbackContainerName);

            // Assert
            Assert.Single(result);
            Assert.True(result.ContainsKey("SpecificContainer"));
            Assert.Single(result["SpecificContainer"]);
            Assert.Equal(testFile, result["SpecificContainer"][0]);

            // Cleanup
            File.Delete(testFile);
        }

        [Fact]
        public async Task GroupFilesByContainer_ShouldIgnoreEmptyContainerName_AndUseFallback()
        {
            // Arrange
            var jsonContent = """
                {
                  "seedConfig": {
                    "id": "test-001",
                    "db": "TestDB",
                    "container": ""
                  },
                  "seedData": {
                    "id": "test-001",
                    "title": "Test Document"
                  }
                }
                """;

            var testFile = Path.Combine(_testDirectory, "test3.json");
            await File.WriteAllTextAsync(testFile, jsonContent);
            var jsonFiles = new[] { testFile };
            var fallbackContainerName = "TestDB";

            // Act
            var result = await _seederService.GroupFilesByContainer(jsonFiles, fallbackContainerName);

            // Assert
            Assert.Single(result);
            Assert.True(result.ContainsKey("TestDB"));
            Assert.Single(result["TestDB"]);
            Assert.Equal(testFile, result["TestDB"][0]);

            // Cleanup
            File.Delete(testFile);
        }

        [Fact]
        public async Task GroupFilesByContainer_ShouldGroupFilesByDifferentContainers()
        {
            // Arrange
            var jsonContent1 = """
                {
                  "seedConfig": {
                    "id": "test-001",
                    "db": "TestDB",
                    "container": "Container1"
                  },
                  "seedData": {
                    "id": "test-001",
                    "title": "Test Document 1"
                  }
                }
                """;

            var jsonContent2 = """
                {
                  "seedConfig": {
                    "id": "test-002",
                    "db": "TestDB",
                    "container": "Container2"
                  },
                  "seedData": {
                    "id": "test-002",
                    "title": "Test Document 2"
                  }
                }
                """;

            var jsonContent3 = """
                {
                  "seedConfig": {
                    "id": "test-003",
                    "db": "TestDB"
                  },
                  "seedData": {
                    "id": "test-003",
                    "title": "Test Document 3"
                  }
                }
                """;

            var testFile1 = Path.Combine(_testDirectory, "test1.json");
            var testFile2 = Path.Combine(_testDirectory, "test2.json");
            var testFile3 = Path.Combine(_testDirectory, "test3.json");

            await File.WriteAllTextAsync(testFile1, jsonContent1);
            await File.WriteAllTextAsync(testFile2, jsonContent2);
            await File.WriteAllTextAsync(testFile3, jsonContent3);

            var jsonFiles = new[] { testFile1, testFile2, testFile3 };
            var fallbackContainerName = "DefaultContainer";

            // Act
            var result = await _seederService.GroupFilesByContainer(jsonFiles, fallbackContainerName);

            // Assert
            Assert.Equal(3, result.Count);
            Assert.True(result.ContainsKey("Container1"));
            Assert.True(result.ContainsKey("Container2"));
            Assert.True(result.ContainsKey("DefaultContainer"));

            Assert.Single(result["Container1"]);
            Assert.Single(result["Container2"]);
            Assert.Single(result["DefaultContainer"]);

            Assert.Equal(testFile1, result["Container1"][0]);
            Assert.Equal(testFile2, result["Container2"][0]);
            Assert.Equal(testFile3, result["DefaultContainer"][0]);

            // Cleanup
            File.Delete(testFile1);
            File.Delete(testFile2);
            File.Delete(testFile3);
        }

        [Fact]
        public async Task GroupFilesByContainer_ShouldGroupMultipleFilesInSameContainer()
        {
            // Arrange
            var jsonContent1 = """
                {
                  "seedConfig": {
                    "id": "test-001",
                    "db": "TestDB",
                    "container": "SharedContainer"
                  },
                  "seedData": {
                    "id": "test-001",
                    "title": "Test Document 1"
                  }
                }
                """;

            var jsonContent2 = """
                {
                  "seedConfig": {
                    "id": "test-002",
                    "db": "TestDB",
                    "container": "SharedContainer"
                  },
                  "seedData": {
                    "id": "test-002",
                    "title": "Test Document 2"
                  }
                }
                """;

            var testFile1 = Path.Combine(_testDirectory, "test1.json");
            var testFile2 = Path.Combine(_testDirectory, "test2.json");

            await File.WriteAllTextAsync(testFile1, jsonContent1);
            await File.WriteAllTextAsync(testFile2, jsonContent2);

            var jsonFiles = new[] { testFile1, testFile2 };
            var fallbackContainerName = "DefaultContainer";

            // Act
            var result = await _seederService.GroupFilesByContainer(jsonFiles, fallbackContainerName);

            // Assert
            Assert.Single(result);
            Assert.True(result.ContainsKey("SharedContainer"));
            Assert.Equal(2, result["SharedContainer"].Length);
            Assert.Contains(testFile1, result["SharedContainer"]);
            Assert.Contains(testFile2, result["SharedContainer"]);

            // Cleanup
            File.Delete(testFile1);
            File.Delete(testFile2);
        }

        [Fact]
        public async Task GroupFilesByContainer_ShouldUseFallbackForInvalidJson_AndLogWarning()
        {
            // Arrange
            var invalidJsonContent = "{ invalid json }";
            var testFile = Path.Combine(_testDirectory, "invalid.json");
            await File.WriteAllTextAsync(testFile, invalidJsonContent);

            var jsonFiles = new[] { testFile };
            var fallbackContainerName = "FallbackContainer";

            // Act
            var result = await _seederService.GroupFilesByContainer(jsonFiles, fallbackContainerName);

            // Assert
            Assert.Single(result);
            Assert.True(result.ContainsKey("FallbackContainer"));
            Assert.Single(result["FallbackContainer"]);
            Assert.Equal(testFile, result["FallbackContainer"][0]);

            // Verify that a warning was logged
            _mockLogger.Verify(
                x => x.Log(
                    LogLevel.Warning,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Could not parse file") && v.ToString()!.Contains("for container name detection")),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);

            // Cleanup
            File.Delete(testFile);
        }

        public void Dispose()
        {
            if (Directory.Exists(_testDirectory))
            {
                Directory.Delete(_testDirectory, true);
            }
        }
    }
}