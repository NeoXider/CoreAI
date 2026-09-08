using System.Collections.Generic;
using CoreAI.Ai;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace CoreAI.Core.Tests.EditMode
{
    /// <summary>
    /// <see cref="LlmToolArgumentNormalizer"/> is the single chokepoint every tool-call
    /// shape (native policy, text-extracted, skill resolver) must go through, because
    /// MEAI's <c>AIFunctionFactory</c> cannot bind Newtonsoft tokens to string parameters.
    /// These tests pin the normalization contract so the three call sites cannot drift apart.
    /// </summary>
    public sealed class LlmToolArgumentNormalizerEditModeTests
    {
        [Test]
        public void NormalizeValue_NullToken_ReturnsNull()
        {
            Assert.IsNull(LlmToolArgumentNormalizer.NormalizeValue(null));
        }

        [Test]
        public void NormalizeValue_NullAndUndefinedJson_ReturnsNull()
        {
            Assert.IsNull(LlmToolArgumentNormalizer.NormalizeValue(JToken.Parse("null")));
            Assert.IsNull(LlmToolArgumentNormalizer.NormalizeValue(JToken.Parse("undefined")));
        }

        [Test]
        public void NormalizeValue_ObjectToken_ReturnsCompactJsonString()
        {
            JToken token = JToken.Parse("{\"a\": 1, \"b\": [true]}");

            object result = LlmToolArgumentNormalizer.NormalizeValue(token);

            Assert.AreEqual("{\"a\":1,\"b\":[true]}", result);
        }

        [Test]
        public void NormalizeValue_ArrayToken_ReturnsCompactJsonString()
        {
            JToken token = JToken.Parse("[1, \"x\"]");

            object result = LlmToolArgumentNormalizer.NormalizeValue(token);

            Assert.AreEqual("[1,\"x\"]", result);
        }

        [Test]
        public void NormalizeValue_ScalarToken_UnwrapsClrValue()
        {
            Assert.AreEqual("hi", LlmToolArgumentNormalizer.NormalizeValue(JToken.Parse("\"hi\"")));
            Assert.AreEqual(7L, LlmToolArgumentNormalizer.NormalizeValue(JToken.Parse("7")));
            Assert.AreEqual(true, LlmToolArgumentNormalizer.NormalizeValue(JToken.Parse("true")));
        }

        [Test]
        public void NormalizeDictionaryValues_NullDictionary_DoesNotThrow()
        {
            Assert.DoesNotThrow(() => LlmToolArgumentNormalizer.NormalizeDictionaryValues(null));
        }

        [Test]
        public void NormalizeDictionaryValues_TokenValues_BecomeStringsInPlace()
        {
            Dictionary<string, object> arguments = new()
            {
                ["obj"] = JObject.Parse("{\"a\":1}"),
                ["arr"] = JArray.Parse("[1,2]"),
                ["plain"] = "keep",
                ["num"] = 5,
            };

            LlmToolArgumentNormalizer.NormalizeDictionaryValues(arguments);

            Assert.AreEqual("{\"a\":1}", arguments["obj"]);
            Assert.AreEqual("[1,2]", arguments["arr"]);
            Assert.AreEqual("keep", arguments["plain"]);
            Assert.AreEqual(5, arguments["num"]);
        }

        [Test]
        public void NormalizedCopy_NullSource_ReturnsNull()
        {
            Assert.IsNull(LlmToolArgumentNormalizer.NormalizedCopy(null));
        }

        [Test]
        public void NormalizedCopy_NormalizesCopyAndKeepsSourceTokens()
        {
            JObject rawObject = JObject.Parse("{\"a\":1}");
            Dictionary<string, object> source = new() { ["obj"] = rawObject };

            Dictionary<string, object> copy =
                LlmToolArgumentNormalizer.NormalizedCopy(source);

            Assert.AreEqual("{\"a\":1}", copy["obj"]);
            Assert.AreSame(rawObject, source["obj"]);
        }
    }
}
