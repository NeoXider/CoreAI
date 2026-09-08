using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Infrastructure.Llm;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// <see cref="FileLlmEndpointRegistryStore"/> must never leave the registry file missing or truncated:
    /// a failed swap has to keep the previously persisted endpoints, profiles and role assignments.
    /// </summary>
    [TestFixture]
    public sealed class LlmEndpointRegistryStoreAtomicSaveEditModeTests
    {
        private string _directory;
        private string _path;

        [SetUp]
        public void SetUp()
        {
            _directory = Path.Combine(Path.GetTempPath(), "CoreAiRegistryStore_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            _path = Path.Combine(_directory, "llm-endpoints.json");
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                if (Directory.Exists(_directory))
                {
                    Directory.Delete(_directory, true);
                }
            }
            catch (IOException)
            {
            }
        }

        [Test]
        public void Save_Twice_ReplacesContentAndLeavesNoTempFile()
        {
            FileLlmEndpointRegistryStore store = new(_path);
            store.Save(StateWith("first"));
            store.Save(StateWith("second"));

            Assert.AreEqual("second", store.Load().Endpoints.Single().EndpointId);
            Assert.IsEmpty(Directory.GetFiles(_directory, "*.tmp"), "The temp file must not survive a successful save.");
        }

        [Test]
        public void Save_WhenTheSwapFails_KeepsThePreviouslyPersistedStateAndCleansUp()
        {
            FileLlmEndpointRegistryStore store = new(_path);
            store.Save(StateWith("first"));

            using (new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.Catch(() => store.Save(StateWith("second")));
            }

            Assert.IsTrue(File.Exists(_path), "A failed save must not delete the existing registry file.");
            Assert.AreEqual("first", store.Load().Endpoints.Single().EndpointId);
            Assert.IsEmpty(Directory.GetFiles(_directory, "*.tmp"), "A failed save must clean up its temp file.");
        }

        [Test]
        public void Save_ConcurrentInstancesUsingEquivalentPaths_CommitCompleteSnapshots()
        {
            FileLlmEndpointRegistryStore first = new(_path);
            FileLlmEndpointRegistryStore second = new(Path.Combine(_directory, ".", "llm-endpoints.json"));

            Assert.DoesNotThrow(() => Parallel.For(0, 64, index =>
            {
                FileLlmEndpointRegistryStore writer = index % 2 == 0 ? first : second;
                writer.Save(StateWith("endpoint-" + index));
                Assert.That(writer.Load().Endpoints.Single().EndpointId, Does.StartWith("endpoint-"));
            }));

            Assert.AreEqual(1, new FileLlmEndpointRegistryStore(_path).Load().Endpoints.Count);
            Assert.IsEmpty(Directory.GetFiles(_directory, "*.tmp"));
        }

        private static LlmEndpointRegistryState StateWith(string endpointId)
        {
            return new LlmEndpointRegistryState
            {
                Endpoints = new[]
                {
                    new LlmEndpointDescriptor
                    {
                        EndpointId = endpointId,
                        DisplayName = endpointId,
                        BaseUrl = "http://localhost:1234/v1",
                        Model = "test-model"
                    }
                }
            };
        }
    }
}
