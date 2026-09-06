using System.Collections.Generic;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Infrastructure.Logging;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Instances
{
    /// <summary>
    /// The ORDER of the D6/R6.2 destroy sequence, and step 3 of it (connections die), through the
    /// real mod runtime.
    /// </summary>
    /// <remarks>
    /// WHY a separate fixture from R6_2_DestroyEditModeTests: that one asserts the terminal state —
    /// IsDestroyed, Parent nil, Parent locked, record unregistered — and every one of those
    /// assertions stays green if the steps are reordered, because the end state is the same either
    /// way. The order is what a mod's AncestryChanged handler actually observes: detach happens
    /// while the connections are still live, so the handler sees the detach; disconnect first and
    /// the handler is never called. These tests fail on a reordering; the state ones do not.
    /// </remarks>
    [TestFixture]
    public sealed class R6_2_DestroyOrderEditModeTests
    {
        private SynchronizationContext _savedContext;

        [SetUp]
        public void DetachSynchronizationContext()
        {
            _savedContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [TearDown]
        public void RestoreSynchronizationContext()
        {
            SynchronizationContext.SetSynchronizationContext(_savedContext);
        }

        [Test]
        public void Destroy_DetachesWhileConnectionsAreStillLive()
        {
            LuaCsRbxApiBindings roblox = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(roblox, store);

            stack.Runtime.LoadMod("m", @"
                local part = Instance.new('Part')
                part.Parent = workspace
                part.AncestryChanged:Connect(function(child, parent)
                    store_set('detached', tostring(parent == nil))
                end)
                part:Destroy()");

            roblox.Scheduler.Advance(0d);

            // If DisconnectSignals ran before SetParent(null), the detach would be fired into a
            // signal with no connections and this handler would never run at all.
            Assert.AreEqual("true", store.Get("m", "detached"),
                "Destroy must detach the instance while its own connections are still live, so a "
                + "handler watching AncestryChanged observes the detach.");
        }

        [Test]
        public void Destroy_KillsTheConnectionsItsOwnDetachJustUsed()
        {
            LuaCsRbxApiBindings roblox = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(roblox, store);

            stack.Runtime.LoadMod("m", @"
                local part = Instance.new('Part')
                part.Parent = workspace
                local connection = part.ChildAdded:Connect(function() end)
                store_set('before', tostring(connection.Connected))
                part:Destroy()
                store_set('after', tostring(connection.Connected))");

            roblox.Scheduler.Advance(0d);

            Assert.AreEqual("true", store.Get("m", "before"));
            Assert.AreEqual("false", store.Get("m", "after"),
                "D6 step 3: every connection on a destroyed instance's signals is disconnected.");
        }

        [Test]
        public void Negative_ASurvivingSiblingsConnectionsAreUntouched()
        {
            LuaCsRbxApiBindings roblox = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(roblox, store);

            // The discriminating half: a Destroy that disconnected everything would also pass the
            // test above, so prove the teardown is scoped to the destroyed subtree.
            stack.Runtime.LoadMod("m", @"
                local doomed = Instance.new('Part')
                local survivor = Instance.new('Part')
                doomed.Parent = workspace
                survivor.Parent = workspace
                local doomedConnection = doomed.ChildAdded:Connect(function() end)
                local survivorConnection = survivor.ChildAdded:Connect(function()
                    store_set('survivor_fired', 'yes')
                end)
                doomed:Destroy()
                store_set('doomed', tostring(doomedConnection.Connected))
                store_set('survivor', tostring(survivorConnection.Connected))
                local child = Instance.new('Part')
                child.Parent = survivor");

            roblox.Scheduler.Advance(0d);

            Assert.AreEqual("false", store.Get("m", "doomed"));
            Assert.AreEqual("true", store.Get("m", "survivor"));
            Assert.AreEqual("yes", store.Get("m", "survivor_fired"),
                "A sibling's handler must still be delivered after an unrelated Destroy.");
        }

        [Test]
        public void Destroy_DestroysItselfBeforeItsChildren()
        {
            LuaCsRbxApiBindings roblox = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(roblox, store);

            // D6 order: the instance fires Destroying and detaches (steps 1-3) BEFORE the recursive
            // child teardown (step 4). Each Destroying handler appends its own tag, so the order is
            // observable: parent then child. A build that tore the children down first would produce
            // "child,parent" and fail here, while every terminal-state assertion would still pass.
            stack.Runtime.LoadMod("m", @"
                local model = Instance.new('Model')
                local child = Instance.new('Part')
                model.Name = 'Doomed'
                model.Parent = workspace
                child.Parent = model
                local order = ''
                model.Destroying:Connect(function()
                    order = order .. 'parent,'
                    store_set('order', order)
                end)
                child.Destroying:Connect(function()
                    order = order .. 'child'
                    store_set('order', order)
                end)
                model:Destroy()
                store_set('gone', tostring(workspace:FindFirstChild('Doomed') == nil))");

            roblox.Scheduler.Advance(0d);

            Assert.AreEqual("true", store.Get("m", "gone"),
                "The destroyed model must be out of the tree.");
            Assert.AreEqual("parent,child", store.Get("m", "order"),
                "Destroying fires on the instance before its children are torn down (D6 step 4).");
        }

        private static LuaCsModStack BuildStack(LuaCsRbxApiBindings roblox, MemoryStore store)
        {
            return LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = new FakeGameLogger(),
                ModStore = store,
                Capabilities = LuaCapabilities.All,
                OneOffCapabilities = LuaCapabilities.All,
                RbxApi = roblox
            });
        }

        private sealed class MemoryStore : ILuaModStore
        {
            private readonly Dictionary<(string ModId, string Key), string> _values = new();

            public string Get(string modId, string key)
            {
                return _values.TryGetValue((modId, key), out string value) ? value : "";
            }

            public void Set(string modId, string key, string value)
            {
                if (value == null)
                {
                    _values.Remove((modId, key));
                    return;
                }

                _values[(modId, key)] = value;
            }

            public void Clear(string modId)
            {
                List<(string ModId, string Key)> keys = new();
                foreach ((string storedModId, string key) in _values.Keys)
                {
                    if (storedModId == modId)
                    {
                        keys.Add((storedModId, key));
                    }
                }

                foreach ((string ModId, string Key) key in keys)
                {
                    _values.Remove(key);
                }
            }
        }

        private sealed class FakeGameLogger : IGameLogger
        {
            public void LogDebug(GameLogFeature feature, string message,
                UnityEngine.Object context = null)
            {
            }

            public void LogInfo(GameLogFeature feature, string message,
                UnityEngine.Object context = null)
            {
            }

            public void LogWarning(GameLogFeature feature, string message,
                UnityEngine.Object context = null)
            {
            }

            public void LogError(GameLogFeature feature, string message,
                UnityEngine.Object context = null)
            {
            }
        }
    }
}
