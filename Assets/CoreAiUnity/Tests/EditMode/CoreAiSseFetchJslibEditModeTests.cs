using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    public sealed class CoreAiSseFetchJslibEditModeTests
    {
        private static string ReadBridge()
        {
            string path = Path.Combine(
                Application.dataPath,
                "CoreAiUnity",
                "Runtime",
                "Plugins",
                "WebGL",
                "CoreAiSseFetch.jslib");

            return File.ReadAllText(path);
        }

        [Test]
        public void Bridge_CSharpCallbacks_AreCalledThroughSafeWrappers()
        {
            string js = ReadBridge();

            StringAssert.Contains("function callOpen", js);
            StringAssert.Contains("function callChunk", js);
            StringAssert.Contains("function callDone", js);
            StringAssert.Contains("function callError", js);
            StringAssert.Contains("CoreAi_FetchSseSelfTest: function", js);

            Assert.AreEqual(
                7,
                Regex.Matches(js, @"makeDynCall\(").Count,
                "Every direct makeDynCall should live in a safe production or WebGL self-test wrapper.");
        }

        [Test]
        public void Bridge_CancelledFetch_DoesNotCallErrorCallback()
        {
            string js = ReadBridge();

            StringAssert.Contains("if (msg !== 'cancelled')", js);
            StringAssert.Contains("callError(msg, 'read-error')", js);
            StringAssert.Contains("callError(msg, 'fetch-rejected')", js);
        }

        [Test]
        public void Bridge_Abort_IsGuardedAgainstBrowserCallbackFailures()
        {
            string js = ReadBridge();

            StringAssert.Contains("CoreAi_FetchSseAbort: function", js);
            StringAssert.Contains("try {", js);
            StringAssert.Contains("if (c && c.abort) c.abort();", js);
            StringAssert.Contains("abort failed", js);
        }
    }
}