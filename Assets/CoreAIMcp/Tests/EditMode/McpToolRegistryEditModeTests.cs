using System;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Mcp.Protocol;
using CoreAI.Mcp.Tools;
using NUnit.Framework;
using Newtonsoft.Json.Linq;

namespace CoreAI.Mcp.Tests
{
    /// <summary>Presence/lookup logic for <see cref="McpToolRegistry"/>.</summary>
    public sealed class McpToolRegistryEditModeTests
    {
        [Test]
        public void Registry_IndexesToolsByName()
        {
            McpToolRegistry registry = new(new[] { new FakeMcpTool("a"), new FakeMcpTool("b") });

            Assert.AreEqual(2, registry.Count);
            Assert.IsTrue(registry.Contains("a"));
            Assert.IsTrue(registry.Contains("b"));
            Assert.IsFalse(registry.Contains("c"));
            Assert.IsNotNull(registry.Find("a"));
            Assert.IsNull(registry.Find("c"));
        }

        [Test]
        public void Registry_DuplicateNamesAreRejected_WithoutChangingPublishedSet()
        {
            McpToolRegistry registry = new(new[] { new FakeMcpTool("keep") });
            long revision = registry.Revision;
            Assert.Throws<ArgumentException>(() => registry.Replace(new[] { new FakeMcpTool("dup"), new FakeMcpTool("dup") }));
            Assert.AreEqual(revision, registry.Revision);
            Assert.IsTrue(registry.Contains("keep"));
            Assert.IsFalse(registry.Contains("dup"));
        }

        [Test]
        public async Task LiveChanges_PublishSchemaAndBindingTogether_AndKeepPreviousDescriptorsFrozen()
        {
            MutableTool tool = new();
            McpToolRegistry registry = new(new IMcpTool[] { tool, new FakeMcpTool("keep") });
            IMcpTool previous = registry.Find("stage");
            tool.Schema = "{\"type\":\"object\",\"properties\":{\"next\":{\"type\":\"string\"}}}";
            Assert.IsNull(JObject.Parse(previous.InputSchemaJson)["properties"]);
            registry.AddOrReplace(tool, McpToolResidency.Dynamic);
            Assert.IsFalse(registry.IsCurrent(previous));
            Assert.AreEqual(McpToolResidency.Dynamic, registry.ResidencyOf("stage"));
            Assert.IsNotNull(JObject.Parse(registry.Find("stage").InputSchemaJson)["properties"]?["next"]);
            IMcpTool broker = registry.Find(CoreAiToolsBrokerMcpTool.ToolName);
            Assert.IsTrue(registry.Remove("stage"));
            McpToolResult stale = await broker.InvokeAsync(new JObject { ["action"] = "call", ["tool"] = "stage" }, CancellationToken.None);
            Assert.IsTrue(stale.IsError);
            Assert.IsTrue(registry.Contains("keep"));
            registry.Replace(new[] { new FakeMcpTool("next"), new FakeMcpTool("keep") });
            Assert.IsTrue(registry.Contains("next"));
            Assert.IsFalse(registry.Contains("stage"));
        }

        [TestCase("[]")]
        [TestCase("invalid")]
        [TestCase("{\"type\":\"array\"}")]
        public void InvalidSchemaUpdate_IsRejectedAtomically(string schema)
        {
            McpToolRegistry registry = new(new[] { new FakeMcpTool("keep") });
            long revision = registry.Revision;
            Assert.Catch(() => registry.AddOrReplace(new MutableTool { Schema = schema }));
            Assert.AreEqual(revision, registry.Revision);
            Assert.IsTrue(registry.Contains("keep"));
            Assert.IsFalse(registry.Contains("stage"));
        }

        private sealed class MutableTool : IMcpTool
        {
            public string Name => "stage";
            public string Description => "Stage operation";
            public string Schema { get; set; } = "{\"type\":\"object\"}";
            public string InputSchemaJson => Schema;
            public Task<McpToolResult> InvokeAsync(JObject arguments, CancellationToken cancellationToken)
                => Task.FromResult(McpToolResult.Text("ok"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task AdmittedInvocation_AfterReplacement_UsesOnlyCapturedBodyAndArguments(bool throughBroker)
        {
            FakeMcpTool previous = new("run");
            FakeMcpTool replacement = new("run");
            McpToolRegistry registry = new(new[] { previous }, null, true);
            JObject arguments = throughBroker
                ? new JObject { ["action"] = "call", ["tool"] = "run", ["arguments_json"] = "{\"echo\":\"original\"}" }
                : new JObject { ["echo"] = "original" };
            McpToolRegistry.InvocationPlan plan = registry.CaptureInvocation(
                throughBroker ? CoreAiToolsBrokerMcpTool.ToolName : "run", arguments);
            arguments[throughBroker ? "arguments_json" : "echo"] = throughBroker ? "{\"echo\":\"changed\"}" : "changed";
            Assert.IsTrue(plan.TryAdmit());
            registry.Replace(new[] { replacement });

            McpToolResult result = await plan.InvokeAsync(CancellationToken.None);

            Assert.AreEqual("run:original", (string)result.ToJson()["content"][0]["text"]);
            Assert.AreEqual(1, previous.InvocationCount);
            Assert.AreEqual(0, replacement.InvocationCount);
            Assert.IsFalse(plan.TryAdmit(), "One request must not acquire admission twice.");
            Assert.Throws<InvalidOperationException>(() => plan.InvokeAsync(CancellationToken.None));
            Assert.AreEqual(1, previous.InvocationCount, "A captured call must not replay its side effects.");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void InvocationChangedBeforeAdmission_DoesNotAdmitOldOrReplacementBody(bool removeBroker)
        {
            FakeMcpTool previous = new("run");
            FakeMcpTool replacement = new("run");
            McpToolRegistry registry = new(new[] { previous });
            registry.AddOrReplace(previous, McpToolResidency.Dynamic);
            McpToolRegistry.InvocationPlan plan = registry.CaptureInvocation(CoreAiToolsBrokerMcpTool.ToolName,
                new JObject { ["action"] = "call", ["tool"] = "run" });
            registry.AddOrReplace(replacement, removeBroker ? McpToolResidency.Native : McpToolResidency.Dynamic);

            Assert.IsFalse(plan.TryAdmit());
            Assert.Throws<InvalidOperationException>(() => plan.InvokeAsync(CancellationToken.None));
            Assert.AreEqual(0, previous.InvocationCount);
            Assert.AreEqual(0, replacement.InvocationCount);
        }

        [Test]
        public void Registry_NullInput_IsEmpty()
        {
            McpToolRegistry registry = new(null);
            Assert.AreEqual(0, registry.Count);
            Assert.AreEqual(0, ((JArray)registry.ToListJson()).Count);
        }

        [Test]
        public void ToListJson_EmitsNameDescriptionAndSchema()
        {
            McpToolRegistry registry = new(new[] { new FakeMcpTool("t") });
            JArray list = registry.ToListJson();

            Assert.AreEqual("t", list[0]["name"]!.ToString());
            Assert.IsNotEmpty(list[0]["description"]!.ToString());
            Assert.AreEqual("object", list[0]["inputSchema"]!["type"]!.ToString());
        }
    }
}
