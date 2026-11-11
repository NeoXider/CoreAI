using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Authority;
using CoreAI.Mcp.Protocol;
using CoreAI.Mcp.Tools;
using CoreAI.Mods.WorldPackages;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using Newtonsoft.Json.Linq;

namespace CoreAI.Mcp.Tests
{
    /// <summary>
    /// The world save/load tools over MCP: the same validation and refusals as the in-game tools, an honest
    /// <c>isError</c> (a missing slot or a failed listing is a failure, a load staged for the player is not),
    /// and <c>save_world</c> only with the host-admin admission <c>manage_mods</c> also needs.
    /// </summary>
    public sealed class WorldPackageMcpToolsEditModeTests
    {
        private static readonly IActorIdentityProvider Identity = new LocalActorIdentityProvider("mcp-test");

        private static Dictionary<string, IMcpTool> Tools(IRbxWorldRuntimeService service,
            IActorIdentityProvider hostAdmin = null)
        {
            return WorldPackageMcpTools.Create(service, Identity, hostAdmin).ToDictionary(tool => tool.Name);
        }

        [Test]
        public void WithoutHostAdmin_SaveIsAbsent_ReadAndRequestToolsArePresent()
        {
            Dictionary<string, IMcpTool> tools = Tools(new FakeWorldService());

            CollectionAssert.AreEquivalent(new[]
            {
                WorldPackageMcpTools.LoadWorldToolName, WorldPackageMcpTools.ListAutoSavesToolName,
                WorldPackageMcpTools.LoadAutoSaveToolName
            }, tools.Keys, "a slot that survives a restart is written only with host-admin admission.");
        }

        [Test]
        public void WithHostAdmin_SaveIsPresent()
        {
            Dictionary<string, IMcpTool> tools = Tools(new FakeWorldService(), Identity);

            Assert.IsTrue(tools.ContainsKey(WorldPackageMcpTools.SaveWorldToolName));
            Assert.AreEqual(4, tools.Count);
        }

        [Test]
        public void WithoutServiceOrIdentity_NoToolIsCreated()
        {
            Assert.IsEmpty(WorldPackageMcpTools.Create(null, Identity, Identity));
            Assert.IsEmpty(WorldPackageMcpTools.Create(new FakeWorldService(), null, null));
        }

        [Test]
        public void EverySchema_IsAnObjectSchemaTheRegistryAccepts()
        {
            McpToolRegistry registry = new(Tools(new FakeWorldService(), Identity).Values);

            Assert.AreEqual(4, registry.Count);
        }

        [Test]
        public async Task ListAutoSaves_ReturnsTheRing_AsASuccess()
        {
            FakeWorldService service = new()
            {
                AutoSaves = new[] { new RbxAutoSaveInfo("20261111T100000000Z-0000-execute_lua.world", "execute_lua",
                    new DateTime(2026, 11, 11, 10, 0, 0, DateTimeKind.Utc), 2048) }
            };

            McpToolResult result = await Tools(service)[WorldPackageMcpTools.ListAutoSavesToolName]
                .InvokeAsync(new JObject(), CancellationToken.None);

            Assert.IsFalse(result.IsError);
            StringAssert.Contains("execute_lua.world", result.Content[0].Text);
        }

        [Test]
        public async Task ListAutoSaves_FailingStore_IsAnError()
        {
            FakeWorldService service = new() { ListFailure = new IOException("disk unplugged") };

            McpToolResult result = await Tools(service)[WorldPackageMcpTools.ListAutoSavesToolName]
                .InvokeAsync(new JObject(), CancellationToken.None);

            Assert.IsTrue(result.IsError);
            StringAssert.Contains("disk unplugged", result.Content[0].Text);
        }

        [Test]
        public async Task LoadWorld_MissingSlot_IsAnError_ThatSaysWhatToDo()
        {
            FakeWorldService service = new() { LoadFailure = new FileNotFoundException("no such slot") };

            McpToolResult result = await Tools(service)[WorldPackageMcpTools.LoadWorldToolName]
                .InvokeAsync(new JObject { ["slot"] = "missing_slot" }, CancellationToken.None);

            Assert.IsTrue(result.IsError, "a missing slot is a failure, not an empty success.");
            StringAssert.Contains("save_world", result.Content[0].Text);
        }

        [TestCase(null)]
        [TestCase("bad/slot")]
        public async Task LoadWorld_InvalidSlot_IsRefusedBeforeTheService(string slot)
        {
            FakeWorldService service = new();
            JObject arguments = new();
            if (slot != null) arguments["slot"] = slot;

            McpToolResult result = await Tools(service)[WorldPackageMcpTools.LoadWorldToolName]
                .InvokeAsync(arguments, CancellationToken.None);

            Assert.IsTrue(result.IsError);
            Assert.AreEqual(0, service.LoadRequests);
        }

        [Test]
        public async Task SaveWorld_CaptureFailure_IsAnError()
        {
            FakeWorldService service = new() { SaveFailure = new InvalidOperationException("world not ready") };

            McpToolResult result = await Tools(service, Identity)[WorldPackageMcpTools.SaveWorldToolName]
                .InvokeAsync(new JObject { ["slot"] = "mcp_slot" }, CancellationToken.None);

            Assert.IsTrue(result.IsError);
            StringAssert.Contains("world not ready", result.Content[0].Text);
        }

        [Test]
        public void StagedLoad_IsASuccess_ButAStagedLoadWithoutARequestIdIsNot()
        {
            JObject staged = JObject.Parse("{\"success\":false,\"status\":\"player_confirmation_required\"," +
                "\"player_confirmation_required\":true,\"request_id\":\"req-1\",\"slot\":\"s\",\"world_id\":\"w\"}");
            JObject hollow = JObject.Parse("{\"success\":false,\"status\":\"player_confirmation_required\",\"request_id\":\"\"}");
            JObject missing = JObject.Parse("{\"success\":false,\"status\":\"not_found\",\"request_id\":\"\"}");

            Assert.IsFalse(WorldPackageMcpTools.IsLoadFailure(staged), "a request waiting for the player is the expected outcome.");
            Assert.IsTrue(WorldPackageMcpTools.IsLoadFailure(hollow));
            Assert.IsTrue(WorldPackageMcpTools.IsLoadFailure(missing));
        }

        [Test]
        public void ListingWithoutASuccessField_IsASuccess_AndSaveNeedsAnExplicitSuccess()
        {
            Assert.IsFalse(WorldPackageMcpTools.IsListFailure(JObject.Parse("{\"autosaves\":[]}")));
            Assert.IsTrue(WorldPackageMcpTools.IsListFailure(JObject.Parse("{\"success\":false}")));
            Assert.IsFalse(WorldPackageMcpTools.IsSaveFailure(JObject.Parse("{\"success\":true,\"path\":\"p\"}")));
            Assert.IsTrue(WorldPackageMcpTools.IsSaveFailure(JObject.Parse("{\"path\":\"p\"}")));
        }

        private sealed class FakeWorldService : IRbxWorldRuntimeService
        {
            public IReadOnlyList<RbxAutoSaveInfo> AutoSaves { get; set; } = Array.Empty<RbxAutoSaveInfo>();
            public Exception ListFailure { get; set; }
            public Exception LoadFailure { get; set; }
            public Exception SaveFailure { get; set; }
            public int LoadRequests { get; private set; }

            public event Action<RbxPendingWorldLoadRequest> ManualLoadConfirmationRequested { add { } remove { } }

            public RbxWorldPackagePayload CaptureCurrent() => throw new NotSupportedException();

            public IReadOnlyList<RbxPendingWorldLoadRequest> GetPendingManualLoads() =>
                Array.Empty<RbxPendingWorldLoadRequest>();

            public IReadOnlyList<RbxAutoSaveInfo> ListAutoSaves()
            {
                if (ListFailure != null) throw ListFailure;
                return AutoSaves;
            }

            public UniTask<RbxWorldPackageWriteResult> SaveManualAsync(ActorContext caller, string slot,
                CancellationToken cancellationToken = default) =>
                UniTask.FromException<RbxWorldPackageWriteResult>(SaveFailure ?? new NotSupportedException());

            public UniTask<RbxWorldLoadRequest> RequestManualLoadAsync(ActorContext caller, string slot,
                CancellationToken cancellationToken = default)
            {
                LoadRequests++;
                return UniTask.FromException<RbxWorldLoadRequest>(LoadFailure ?? new NotSupportedException());
            }

            public UniTask<RbxWorldLoadRequest> RequestAutoLoadAsync(ActorContext caller, string autoFileName,
                CancellationToken cancellationToken = default)
            {
                LoadRequests++;
                return UniTask.FromException<RbxWorldLoadRequest>(LoadFailure ?? new NotSupportedException());
            }

            public UniTask<RbxWorldLoadResult> ConfirmManualLoadAsync(string requestId, bool playerConfirmed,
                CancellationToken cancellationToken = default) => throw new NotSupportedException();

            public UniTask<RbxWorldLoadResult> LoadConfirmedAsync(RbxWorldPackagePayload payload,
                CancellationToken cancellationToken = default) => throw new NotSupportedException();
        }
    }
}
