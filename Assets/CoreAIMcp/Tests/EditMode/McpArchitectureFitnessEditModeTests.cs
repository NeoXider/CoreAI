using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Mcp.Tests
{
    /// <summary>
    /// Architecture-fitness test (ARCHITECTURE_RULES §5): the protocol + routing core of the MCP package
    /// stays engine-free, so the JSON-RPC/MCP framing is portable and unit-testable without Unity. Only
    /// the server adapters (HTTP listener, screenshot capture, the MonoBehaviour) may touch UnityEngine.
    /// Grep-based, mirroring the seam-honesty test template.
    /// </summary>
    public sealed class McpArchitectureFitnessEditModeTests
    {
        // Files that MUST NOT reference UnityEngine (the engine-free protocol/routing core).
        private static readonly string[] EngineFreeRelativePaths =
        {
            "CoreAIMcp/Runtime/Protocol/JsonRpc.cs",
            "CoreAIMcp/Runtime/Protocol/McpContent.cs",
            "CoreAIMcp/Runtime/Protocol/McpMethods.cs",
            "CoreAIMcp/Runtime/Tools/IMcpTool.cs",
            "CoreAIMcp/Runtime/Tools/McpToolRegistry.cs",
            "CoreAIMcp/Runtime/Tools/IScreenshotSource.cs",
            "CoreAIMcp/Runtime/Server/McpRpcDispatcher.cs",
            "CoreAIMcp/Runtime/Server/McpSessionStore.cs",
            "CoreAIMcp/Runtime/Server/IMainThreadDispatcher.cs"
        };

        [Test]
        public void ProtocolAndRoutingCore_DoNotReferenceUnityEngine()
        {
            List<string> offenders = new();
            foreach (string relative in EngineFreeRelativePaths)
            {
                string path = Path.Combine(Application.dataPath, relative);
                if (!File.Exists(path))
                {
                    offenders.Add($"MISSING: {relative}");
                    continue;
                }

                // WHY: strip // comments before scanning - a doc comment MENTIONING UnityEngine
                // (e.g. "does not depend on UnityEngine") is not an engine reference.
                foreach (string rawLine in File.ReadAllLines(path))
                {
                    string line = rawLine;
                    int comment = line.IndexOf("//", System.StringComparison.Ordinal);
                    if (comment >= 0)
                    {
                        line = line.Substring(0, comment);
                    }

                    if (line.Contains("UnityEngine"))
                    {
                        offenders.Add($"references UnityEngine: {relative}");
                        break;
                    }
                }
            }

            Assert.IsEmpty(offenders,
                "The MCP protocol/routing core must stay engine-free:\n" + string.Join("\n", offenders));
        }
    }
}
