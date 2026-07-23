using System.Collections.Generic;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// EditMode coverage for the model-discovery helpers behind
    /// <see cref="CoreAiBackend.ListModelsAsync"/>: URL building
    /// (<see cref="CoreAiBackend.BuildModelsUrl"/>) and response parsing
    /// (<see cref="CoreAiBackend.ParseModelIds"/>), both pure and network-free.
    /// </summary>
    [TestFixture]
    public sealed class CoreAiBackendModelDiscoveryEditModeTests
    {
        // ===================== BuildModelsUrl =====================

        [Test]
        public void BuildModelsUrl_AppendsModelsSegment()
        {
            Assert.AreEqual(
                "http://127.0.0.1:1234/v1/models",
                CoreAiBackend.BuildModelsUrl("http://127.0.0.1:1234/v1"));
        }

        [Test]
        public void BuildModelsUrl_TrimsTrailingSlashBeforeAppending()
        {
            Assert.AreEqual(
                "http://127.0.0.1:1234/v1/models",
                CoreAiBackend.BuildModelsUrl("http://127.0.0.1:1234/v1/"));
        }

        [Test]
        public void BuildModelsUrl_AlreadyModelsUrl_IsIdempotent()
        {
            Assert.AreEqual(
                "http://127.0.0.1:1234/v1/models",
                CoreAiBackend.BuildModelsUrl("http://127.0.0.1:1234/v1/models"));
        }

        [Test]
        public void BuildModelsUrl_ModelsSuffixMatch_IsCaseInsensitive()
        {
            Assert.AreEqual(
                "http://localhost/v1/MODELS",
                CoreAiBackend.BuildModelsUrl("http://localhost/v1/MODELS"));
        }

        [Test]
        public void BuildModelsUrl_ModelsUrlWithTrailingSlash_IsNormalizedNotDoubled()
        {
            Assert.AreEqual(
                "http://localhost/v1/models",
                CoreAiBackend.BuildModelsUrl("http://localhost/v1/models/"));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        public void BuildModelsUrl_BlankInput_ReturnsEmpty(string baseUrl)
        {
            Assert.AreEqual("", CoreAiBackend.BuildModelsUrl(baseUrl));
        }

        // ===================== ParseModelIds =====================

        [Test]
        public void ParseModelIds_StandardEnvelope_ReturnsIdsInServerOrder()
        {
            IReadOnlyList<string> ids =
                CoreAiBackend.ParseModelIds("{\"data\":[{\"id\":\"a\"},{\"id\":\"b\"}]}");

            CollectionAssert.AreEqual(new[] { "a", "b" }, ids);
        }

        [Test]
        public void ParseModelIds_BareTopLevelArray_IsAccepted()
        {
            IReadOnlyList<string> ids =
                CoreAiBackend.ParseModelIds("[{\"id\":\"x\"},{\"id\":\"y\"}]");

            CollectionAssert.AreEqual(new[] { "x", "y" }, ids);
        }

        [Test]
        public void ParseModelIds_DuplicateIds_AreDeDuplicatedPreservingFirstSeenOrder()
        {
            IReadOnlyList<string> ids = CoreAiBackend.ParseModelIds(
                "{\"data\":[{\"id\":\"b\"},{\"id\":\"a\"},{\"id\":\"b\"},{\"id\":\"a\"}]}");

            CollectionAssert.AreEqual(new[] { "b", "a" }, ids);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        [TestCase("<html>502 Bad Gateway</html>")]
        [TestCase("{\"data\":[{\"id\":\"a\"}")]
        public void ParseModelIds_BlankOrMalformedBody_ReturnsEmptyList(string json)
        {
            Assert.AreEqual(0, CoreAiBackend.ParseModelIds(json).Count);
        }

        [Test]
        public void ParseModelIds_EnvelopeWithoutDataArray_ReturnsEmptyList()
        {
            Assert.AreEqual(0, CoreAiBackend.ParseModelIds("{\"object\":\"list\"}").Count);
        }
    }
}
