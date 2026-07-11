using System.Collections.Generic;
using System.Linq;
using CoreAI.Infrastructure.Llm;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Wire-format tests for the WebGL fetch/SSE bridge protocol helpers. The transport class
    /// itself only compiles on the WebGL player, so this is the C#-side coverage of the bridge:
    /// the newline-flattened header strings crossing the jslib boundary in both directions and
    /// the cancel-message contract. The JS side is covered by
    /// <c>Assets/CoreAiUnity/Tests/Node~/fetch_sse_jslib_test.js</c> (run with node).
    /// </summary>
    public class FetchSseTransportProtocolEditModeTests
    {
        private static KeyValuePair<string, string> H(string k, string v)
        {
            return new KeyValuePair<string, string>(k, v);
        }

        [Test]
        public void BuildHeaderString_AddsContentTypeWhenMissing()
        {
            string flat = FetchSseTransportProtocol.BuildHeaderString(
                new List<KeyValuePair<string, string>> { H("Authorization", "Bearer k") });

            StringAssert.Contains("Authorization:Bearer k", flat);
            StringAssert.Contains("Content-Type:application/json", flat);
        }

        [Test]
        public void BuildHeaderString_NullOrEmptyHeaders_StillEmitsContentType()
        {
            Assert.AreEqual("Content-Type:application/json",
                FetchSseTransportProtocol.BuildHeaderString(null));
            Assert.AreEqual("Content-Type:application/json",
                FetchSseTransportProtocol.BuildHeaderString(new List<KeyValuePair<string, string>>()));
        }

        [Test]
        public void BuildHeaderString_PreservesExistingContentType_CaseInsensitive()
        {
            string flat = FetchSseTransportProtocol.BuildHeaderString(
                new List<KeyValuePair<string, string>> { H("content-type", "text/event-stream") });

            StringAssert.Contains("content-type:text/event-stream", flat);
            StringAssert.DoesNotContain("application/json", flat);
        }

        [Test]
        public void BuildHeaderString_StripsCrLfFromNamesAndValues()
        {
            // A CR/LF inside a value would break the one-header-per-line wire format (header
            // injection) or make the browser's fetch throw synchronously on an invalid header.
            string flat = FetchSseTransportProtocol.BuildHeaderString(
                new List<KeyValuePair<string, string>>
                {
                    H("X-Trace\r\n", "abc\r\ndef"),
                    H("Authorization", "Bearer k\n")
                });

            string[] lines = flat.Split('\n');
            Assert.AreEqual(3, lines.Length, flat);
            Assert.AreEqual("X-Trace:abcdef", lines[0]);
            Assert.AreEqual("Authorization:Bearer k", lines[1]);
            Assert.AreEqual("Content-Type:application/json", lines[2]);
        }

        [Test]
        public void BuildHeaderString_SkipsEmptyHeaderNames()
        {
            string flat = FetchSseTransportProtocol.BuildHeaderString(
                new List<KeyValuePair<string, string>> { H("", "value"), H("\r\n", "x") });

            Assert.AreEqual("Content-Type:application/json", flat);
        }

        [Test]
        public void ParseFlatHeaders_RoundTripsAndIsCaseInsensitive()
        {
            IReadOnlyDictionary<string, IEnumerable<string>> map =
                FetchSseTransportProtocol.ParseFlatHeaders("Content-Type: text/event-stream\nRetry-After: 14");

            Assert.AreEqual("text/event-stream", map["content-type"].Single());
            Assert.AreEqual("14", map["RETRY-AFTER"].Single());
        }

        [Test]
        public void ParseFlatHeaders_CollectsRepeatedHeaderValues()
        {
            IReadOnlyDictionary<string, IEnumerable<string>> map =
                FetchSseTransportProtocol.ParseFlatHeaders("Set-Cookie: a=1\nSet-Cookie: b=2");

            CollectionAssert.AreEqual(new[] { "a=1", "b=2" }, map["Set-Cookie"].ToArray());
        }

        [Test]
        public void ParseFlatHeaders_SkipsMalformedLines()
        {
            IReadOnlyDictionary<string, IEnumerable<string>> map =
                FetchSseTransportProtocol.ParseFlatHeaders("no-colon-line\n:empty-name\nOk: yes\n\n");

            Assert.AreEqual(1, map.Count);
            Assert.AreEqual("yes", map["Ok"].Single());
        }

        [Test]
        public void ParseFlatHeaders_EmptyInput_ReturnsEmptyMap()
        {
            Assert.AreEqual(0, FetchSseTransportProtocol.ParseFlatHeaders(null).Count);
            Assert.AreEqual(0, FetchSseTransportProtocol.ParseFlatHeaders("").Count);
        }

        [Test]
        public void ParseFlatHeaders_ValueMayContainColons()
        {
            IReadOnlyDictionary<string, IEnumerable<string>> map =
                FetchSseTransportProtocol.ParseFlatHeaders("Location: https://a.example:8443/x");

            Assert.AreEqual("https://a.example:8443/x", map["Location"].Single());
        }

        [TestCase("cancelled", true)]
        [TestCase("canceled", true)]
        [TestCase("Cancelled", true)]
        [TestCase("CANCELED", true)]
        [TestCase("Timeout", false)]
        [TestCase("Failed to fetch", false)]
        [TestCase("", false)]
        [TestCase(null, false)]
        public void IsCancelledMessage_MatchesOnlyCancelSpellings(string message, bool expected)
        {
            Assert.AreEqual(expected, FetchSseTransportProtocol.IsCancelledMessage(message));
        }
    }
}
