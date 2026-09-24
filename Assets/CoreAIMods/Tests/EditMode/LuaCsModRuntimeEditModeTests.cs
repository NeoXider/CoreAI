using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Ai.Logging;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.Infrastructure.Logging;
using CoreAI.Messaging;
using CoreAI.Mods.Rbx.Instances;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// End-to-end EditMode proof of the additive Lua-CSharp mod stack, wired exactly as a future DI scope
    /// would wire it — through <see cref="LuaCsModRuntimeFactory"/>, exercising the managed
    /// (Lua-CSharp) runtime end to end.
    /// The test assembly does not reference the Lua-CSharp package (Lua.dll), so Lua-side failures are
    /// caught via the non-generic <see cref="Assert.Catch(TestDelegate)"/> rather than by exception type.
    /// </summary>
    public sealed class LuaCsModRuntimeEditModeTests
    {
        private SynchronizationContext _savedContext;

        /// <summary>
        /// The Lua-CSharp runtime bridges its async VM to a synchronous call site via
        /// <c>state.ExecuteAsync(...).GetAwaiter().GetResult()</c> inside the execution guard. On Unity's
        /// main thread a <see cref="SynchronizationContext"/> is installed, so any continuation the VM
        /// posts back to it would deadlock the blocked main thread (a sync-over-async hazard — this is
        /// why the interactive Unity Test Runner freezes on these paths, and why batchmode is the
        /// reliable way to run them). Detaching the context for the duration of each test lets those
        /// continuations complete on the thread pool, exercising the runtime's logic deterministically.
        /// </summary>
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

        /// <summary>In-memory <see cref="ILuaModStore"/> used by the runtime fixtures.</summary>
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

                foreach ((string storedModId, string key) in keys)
                {
                    _values.Remove((storedModId, key));
                }
            }
        }

        /// <summary>Collects every command a WorldEdit-tier binding routes through the sink.</summary>
        private sealed class FakeCommandSink : IAiGameCommandSink
        {
            public readonly List<ApplyAiGameCommand> Commands = new();

            public void Publish(ApplyAiGameCommand command)
            {
                Commands.Add(command);
            }
        }

        /// <summary>No-op Unity logger so the ported gameplay bindings have a non-null sink.</summary>
        private sealed class FakeGameLogger : IGameLogger
        {
        private SynchronizationContext _previousSynchronizationContext;

        /// <summary>
        /// WHY: a test here waits on a Task from the calling thread (Assert.ThrowsAsync/CatchAsync, or
        /// a blocking read of a Task local). Under Unity's SynchronizationContext the awaited
        /// continuation is posted back to the very thread the wait is holding, and the EditMode batch
        /// stops with no results file - silence, not a failure.
        /// </summary>
        [SetUp]
        public void DetachSynchronizationContext()
        {
            _previousSynchronizationContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [TearDown]
        public void RestoreSynchronizationContext()
        {
            SynchronizationContext.SetSynchronizationContext(_previousSynchronizationContext);
        }

            public void LogDebug(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }

            public void LogInfo(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }

            public void LogWarning(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }

            public void LogError(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }
        }

        /// <summary>Builds the fully-wired stack the way the DI scope will, with test fakes.</summary>
        private static LuaCsModStack BuildStack(
            ILuaModStore store = null,
            IAiGameCommandSink sink = null,
            LuaCapabilities caps = LuaCapabilities.All,
            int handlerMaxSteps = 0,
            int handlerTimeoutMs = 0,
            ILuaLogService logService = null)
        {
            LuaCsModStackOptions options = new()
            {
                Logger = new FakeGameLogger(),
                CommandSink = sink,
                ModStore = store,
                Capabilities = caps,
                OneOffCapabilities = caps,
                LogService = logService
            };
            if (handlerMaxSteps > 0)
            {
                options.HandlerMaxSteps = handlerMaxSteps;
            }

            if (handlerTimeoutMs > 0)
            {
                options.HandlerTimeoutMs = handlerTimeoutMs;
            }

            return LuaCsModRuntimeFactory.Create(options);
        }

        /// <summary>Builds the shipped one-off executor over a supplied strict Rbx world.</summary>
        private static LuaCsModStack BuildMutationStack(LuaCsRbxApiBindings bindings)
        {
            return LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = new FakeGameLogger(),
                Capabilities = LuaCapabilities.All,
                OneOffCapabilities = LuaCapabilities.All,
                RbxApi = bindings
            });
        }

        /// <summary>Issues a reconnectable restricted actor with an explicit connection id.</summary>
        private static ActorContext MutationActor(string actorId, string sessionId)
        {
            return new LocalActorIdentityProvider(
                    actorId, sessionId, "", ActorGrantSet.None, AgentMemoryScope.Empty)
                .GetActorContext(BuiltInAgentRoleIds.Programmer);
        }

        /// <summary>Creates shared content after the Rbx bindings bootstrap Workspace.</summary>
        private static RbxInstance CreateMutationTarget(InstanceRegistry registry, string name)
        {
            RbxInstance target = registry.Create(
                "Folder", accessScope: InstanceAccessScope.SharedWritable);
            target.Name = name;
            target.Parent = registry.WorldRoot;
            return target;
        }

        private static InstanceRecord MutationRecord(
            InstanceRegistry registry, RbxInstance target)
        {
            Assert.IsTrue(registry.TryGetRecord(target.Id, out InstanceRecord record));
            return record;
        }

        private static LuaTool.LuaResult ExecuteMutation(LuaCsModStack stack,
            ActorContext actorContext, MutationEnvelope envelope, string source)
        {
            return stack.ToolExecutor.ExecuteAsync(
                    source, actorContext, envelope, CancellationToken.None)
                .GetAwaiter().GetResult();
        }

        [Test]
        public void LuaCs_StructuralQuotaDefaults_AreVisibleOnOptionsAndRuntime()
        {
            LuaCsModStackOptions options = new();
            LuaCsModStack stack = LuaCsModRuntimeFactory.Create(options);

            Assert.AreEqual(LuaCsModRuntime.DefaultMaxMods, options.MaxMods);
            Assert.AreEqual(options.MaxMods, stack.Runtime.MaxMods);
            Assert.AreEqual(
                options.MaxSchedulerThreadsPerActor,
                stack.Runtime.MaxSchedulerThreadsPerActor);
            Assert.AreEqual(
                LuaCsModRuntime.DefaultMaxRegisteredInstancesPerActor,
                options.MaxRegisteredInstancesPerActor);
            Assert.AreEqual(
                options.MaxRegisteredInstancesPerActor,
                stack.Runtime.MaxRegisteredInstancesPerActor);
            Assert.AreEqual(
                LuaCsModRuntime.DefaultMaxEventSubscriptionsPerActor,
                options.MaxEventSubscriptionsPerActor);
            Assert.AreEqual(
                options.MaxEventSubscriptionsPerActor,
                stack.Runtime.MaxEventSubscriptionsPerActor);
        }

        [Test]
        public void LuaCs_ProductionDefaultCapacity_IsPerActorAtNAndNPlusOne()
        {
            LuaCsModStackOptions options = new();
            LuaCsModStack stack = LuaCsModRuntimeFactory.Create(options);
            ActorContext actor = new LocalActorIdentityProvider("production-actor")
                .GetActorContext(BuiltInAgentRoleIds.Programmer);

            Assert.AreEqual(32, LuaCsModRuntime.DefaultMaxMods);
            Assert.AreEqual(LuaCsModRuntime.DefaultMaxMods, options.MaxMods);
            Assert.AreEqual(LuaCsModRuntime.DefaultMaxMods, stack.Runtime.MaxMods);

            for (int i = 0; i < LuaCsModRuntime.DefaultMaxMods; i++)
            {
                stack.Runtime.LoadMod(actor, $"production-mod-{i}", "return true", persistToStore: false);
            }

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
                stack.Runtime.LoadMod(
                    actor,
                    "production-mod-over-limit",
                    "return true",
                    persistToStore: false));

            StringAssert.Contains("loaded mods quota", exception.Message);
            StringAssert.Contains(actor.ActorId, exception.Message);
            StringAssert.Contains(LuaCsModRuntime.DefaultMaxMods.ToString(), exception.Message);

            ActorContext secondActor = new LocalActorIdentityProvider("production-actor-2")
                .GetActorContext(BuiltInAgentRoleIds.Programmer);
            Assert.DoesNotThrow(() => stack.Runtime.LoadMod(
                secondActor, "production-mod-second-actor", "return true", persistToStore: false));
        }

        [Test]
        public void LuaCs_BenchmarkCapacity_AcceptsLimitAndRejectsNextActorLoudly()
        {
            Assert.Less(LuaCsModRuntime.BenchmarkMaxMods, LuaCsModRuntime.EmergencyMaxMods);

            LuaCsModStack stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                MaxMods = LuaCsModRuntime.BenchmarkMaxMods
            });
            ActorContext actor = new LocalActorIdentityProvider("benchmark-actor")
                .GetActorContext(BuiltInAgentRoleIds.Programmer);

            for (int i = 0; i < LuaCsModRuntime.BenchmarkMaxMods; i++)
            {
                stack.Runtime.LoadMod(actor, $"benchmark-mod-{i}", "return true", persistToStore: false);
            }

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
                stack.Runtime.LoadMod(
                    actor,
                    "benchmark-mod-over-limit",
                    "return true",
                    persistToStore: false));

            StringAssert.Contains("loaded mods quota", exception.Message);
            StringAssert.Contains(actor.ActorId, exception.Message);
            StringAssert.Contains(LuaCsModRuntime.BenchmarkMaxMods.ToString(), exception.Message);

            ActorContext secondActor = new LocalActorIdentityProvider("benchmark-actor-2")
                .GetActorContext(BuiltInAgentRoleIds.Programmer);
            Assert.DoesNotThrow(() => stack.Runtime.LoadMod(
                secondActor, "benchmark-mod-second-actor", "return true", persistToStore: false));
        }

        [Test]
        public void LuaCs_EmergencyCeiling_RejectsEvenWhenConfiguredLimitIsHigher()
        {
            LuaCsModStack stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                MaxMods = LuaCsModRuntime.EmergencyMaxMods + 1
            });

            for (int i = 0; i < LuaCsModRuntime.EmergencyMaxMods; i++)
            {
                ActorContext actor = new LocalActorIdentityProvider($"emergency-actor-{i}")
                    .GetActorContext(BuiltInAgentRoleIds.Programmer);
                stack.Runtime.LoadMod(actor, $"emergency-mod-{i}", "return true", persistToStore: false);
            }

            ActorContext refusedActor = new LocalActorIdentityProvider("emergency-actor-over-limit")
                .GetActorContext(BuiltInAgentRoleIds.Programmer);
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
                stack.Runtime.LoadMod(
                    refusedActor,
                    "emergency-mod-over-limit",
                    "return true",
                    persistToStore: false));

            StringAssert.Contains("emergency mod ceiling", exception.Message);
            StringAssert.Contains(refusedActor.ActorId, exception.Message);
            StringAssert.Contains(LuaCsModRuntime.EmergencyMaxMods.ToString(), exception.Message);
        }

        [Test]
        public void LuaCs_SchedulerThreadQuota_IsPerActorAtNAndNPlusOne()
        {
            LuaCsModStack stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                RbxApi = new LuaCsRbxApiBindings(),
                MaxSchedulerThreadsPerActor = 2
            });
            ActorContext actor = new LocalActorIdentityProvider("thread-actor")
                .GetActorContext(BuiltInAgentRoleIds.Programmer);

            Assert.DoesNotThrow(() => stack.Runtime.LoadMod(
                actor, "thread-a-1", "task.wait(1000)", persistToStore: false));
            Assert.DoesNotThrow(() => stack.Runtime.LoadMod(
                actor, "thread-a-2", "task.wait(1000)", persistToStore: false));

            Exception exception = Assert.Catch(() => stack.Runtime.LoadMod(
                actor, "thread-a-3", "task.wait(1000)", persistToStore: false));
            StringAssert.Contains(actor.ActorId, exception.Message);
            StringAssert.Contains("live scheduler threads quota", exception.Message);
            StringAssert.Contains("2", exception.Message);

            ActorContext secondActor = new LocalActorIdentityProvider("thread-actor-2")
                .GetActorContext(BuiltInAgentRoleIds.Programmer);
            Assert.DoesNotThrow(() => stack.Runtime.LoadMod(
                secondActor, "thread-b-1", "task.wait(1000)", persistToStore: false));
        }

        [Test]
        public void LuaCs_RegisteredInstanceQuota_IsPerActorAtNAndNPlusOne()
        {
            const int quota = 2;
            LuaCsRbxApiBindings rbxApi = new();
            LuaCsModStack stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                RbxApi = rbxApi,
                MaxRegisteredInstancesPerActor = quota
            });
            ActorContext actor = new LocalActorIdentityProvider("instance-actor")
                .GetActorContext(BuiltInAgentRoleIds.Programmer);

            Assert.DoesNotThrow(() => stack.Runtime.LoadMod(
                actor,
                "instance-a-1",
                "Instance.new('Folder')\nInstance.new('Folder')",
                persistToStore: false));
            Assert.AreEqual(2, rbxApi.Registry.GetOwnedBy("instance-a-1").Count,
                "the per-mod Script proxy must not replace a user-content quota slot");

            Exception exception = Assert.Catch(() => stack.Runtime.LoadMod(
                actor,
                "instance-a-2",
                "Instance.new('Folder')",
                persistToStore: false));
            StringAssert.Contains(actor.ActorId, exception.Message);
            StringAssert.Contains("registered instances quota", exception.Message);
            StringAssert.Contains(quota.ToString(), exception.Message);

            for (int actorNumber = 2; actorNumber <= quota + 1; actorNumber++)
            {
                ActorContext independentActor = new LocalActorIdentityProvider(
                        "instance-actor-" + actorNumber)
                    .GetActorContext(BuiltInAgentRoleIds.Programmer);
                Assert.DoesNotThrow(() => stack.Runtime.LoadMod(
                    independentActor,
                    "instance-independent-" + actorNumber,
                    "Instance.new('Folder')",
                    persistToStore: false),
                    "actor N+1 must retain its own quota even after N runtime Players exist");
            }
        }

        /// <summary>
        /// Captures a bootstrapped world whose Workspace holds <paramref name="ownedByActor"/> folders
        /// owned by <paramref name="ownerActorId"/> and <paramref name="hostOwned"/> unattributed host
        /// folders, then restores it into a fresh registry the way a world load stages it: the whole
        /// tree is registered before any runtime exists.
        /// </summary>
        private static LuaCsRbxApiBindings RestoreWorldBeforeTheRuntime(
            string ownerActorId, int ownedByActor, int hostOwned = 0)
        {
            InstanceRegistry source = new();
            RbxDataModel sourceGame = DataModelBootstrap.CreateGame(source);
            for (int index = 0; index < ownedByActor; index++)
            {
                RbxInstance folder = source.Create("Folder", ownerActorId: ownerActorId);
                folder.Name = "Owned" + index;
                folder.Parent = source.WorldRoot;
            }

            for (int index = 0; index < hostOwned; index++)
            {
                RbxInstance folder = source.Create("Folder");
                folder.Name = "Host" + index;
                folder.Parent = source.WorldRoot;
            }

            InstanceTreeSnapshot snapshot = InstanceTreeSerializer.Capture(sourceGame);
            InstanceRegistry restored = new();
            RbxDataModel game = (RbxDataModel)InstanceTreeSerializer.Restore(snapshot, restored);
            DataModelBootstrap.AttachWorldRoot(restored, game);
            return new LuaCsRbxApiBindings(restored, game);
        }

        /// <summary>Counts the live non-infrastructure records whose durable owner is <paramref name="ownerActorId"/>.</summary>
        private static int CountLiveOwnedBy(InstanceRegistry registry, string ownerActorId)
        {
            int count = 0;
            foreach (RbxInstance instance in registry.GetLiveInstances())
            {
                if (registry.TryGetRecord(instance.Id, out InstanceRecord record)
                    && !record.IsRuntimeInfrastructure
                    && string.Equals(record.OwnerActorId, ownerActorId, StringComparison.Ordinal))
                {
                    count++;
                }
            }

            return count;
        }

        private static ActorContext QuotaActor(string actorId)
        {
            return new LocalActorIdentityProvider(actorId)
                .GetActorContext(BuiltInAgentRoleIds.Programmer);
        }

        [Test]
        public void LuaCs_RegisteredInstanceQuota_ChargesInstancesRestoredBeforeTheRuntimeExisted()
        {
            const int quota = 2;
            ActorContext actor = QuotaActor("restored-actor");
            LuaCsRbxApiBindings rbxApi = RestoreWorldBeforeTheRuntime(actor.ActorId, quota);
            LuaCsModStack stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                RbxApi = rbxApi,
                MaxRegisteredInstancesPerActor = quota
            });

            Exception exception = Assert.Catch(() => stack.Runtime.LoadMod(
                    actor, "restored-a-1", "Instance.new('Folder')", persistToStore: false),
                "the restored folders already fill this actor's quota, so a load must not grant a fresh one");
            StringAssert.Contains(actor.ActorId, exception.Message);
            StringAssert.Contains(
                "registered instances quota reached (limit " + quota + ")", exception.Message);
            Assert.AreEqual(quota, CountLiveOwnedBy(rbxApi.Registry, actor.ActorId),
                "the refused creation must leave nothing behind and must not touch the restored folders");
        }

        [Test]
        public void LuaCs_RegisteredInstanceQuota_RestoredInstancesOfOneActorLeaveAnotherActorsQuotaWhole()
        {
            const int quota = 2;
            ActorContext restoredOwner = QuotaActor("restored-owner");
            ActorContext otherActor = QuotaActor("other-actor");
            LuaCsRbxApiBindings rbxApi = RestoreWorldBeforeTheRuntime(restoredOwner.ActorId, quota);
            LuaCsModStack stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                RbxApi = rbxApi,
                MaxRegisteredInstancesPerActor = quota
            });

            Assert.DoesNotThrow(() => stack.Runtime.LoadMod(
                    otherActor,
                    "restored-b-1",
                    "Instance.new('Folder')\nInstance.new('Folder')",
                    persistToStore: false),
                "another actor's restored records and the restored world skeleton must not be charged to this actor");
            Assert.AreEqual(quota, CountLiveOwnedBy(rbxApi.Registry, otherActor.ActorId));
        }

        [Test]
        public void LuaCs_RegisteredInstanceQuota_DestroyingARestoredInstanceFreesExactlyOneSlot()
        {
            const int quota = 2;
            ActorContext actor = QuotaActor("restored-destroyer");
            LuaCsRbxApiBindings rbxApi = RestoreWorldBeforeTheRuntime(actor.ActorId, quota);
            LuaCsModStack stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                RbxApi = rbxApi,
                MaxRegisteredInstancesPerActor = quota
            });

            rbxApi.Registry.WorldRoot.FindFirstChild("Owned0").Destroy();

            Assert.DoesNotThrow(() => stack.Runtime.LoadMod(
                    actor, "restored-freed-1", "Instance.new('Folder')", persistToStore: false),
                "destroying a restored record must release the slot it was charged");
            Exception exception = Assert.Catch(() => stack.Runtime.LoadMod(
                    actor, "restored-freed-2", "Instance.new('Folder')", persistToStore: false),
                "one destroyed restored record frees exactly one slot, not the whole quota");
            StringAssert.Contains(actor.ActorId, exception.Message);
            StringAssert.Contains(
                "registered instances quota reached (limit " + quota + ")", exception.Message);
        }

        [Test]
        public void LuaCs_RegisteredInstanceQuota_ARestoredWorldOverTheLimitStaysWholeAndRefusesOnlyNewCreations()
        {
            const int quota = 2;
            const int restoredCount = quota + 1;
            ActorContext actor = QuotaActor("restored-over-limit");
            LuaCsRbxApiBindings rbxApi = RestoreWorldBeforeTheRuntime(actor.ActorId, restoredCount);
            List<RbxInstance> restoredFolders = new();
            for (int index = 0; index < restoredCount; index++)
            {
                restoredFolders.Add(rbxApi.Registry.WorldRoot.FindFirstChild("Owned" + index));
            }

            LuaCsModStack stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                RbxApi = rbxApi,
                MaxRegisteredInstancesPerActor = quota
            });

            foreach (RbxInstance folder in restoredFolders)
            {
                Assert.IsFalse(folder.IsDestroyed,
                    "charging a restored world must never destroy what it restored, even over the quota");
                Assert.IsTrue(rbxApi.Registry.TryGet(folder.Id, out RbxInstance _));
            }

            Assert.AreEqual(restoredCount, CountLiveOwnedBy(rbxApi.Registry, actor.ActorId));
            Assert.Catch(() => stack.Runtime.LoadMod(
                    actor, "over-limit-1", "Instance.new('Folder')", persistToStore: false),
                "an actor restored over its quota cannot create more");

            restoredFolders[0].Destroy();
            Assert.Catch(() => stack.Runtime.LoadMod(
                    actor, "over-limit-2", "Instance.new('Folder')", persistToStore: false),
                "one destroy takes the actor from quota + 1 to exactly the quota, which is still full");

            restoredFolders[1].Destroy();
            Assert.DoesNotThrow(() => stack.Runtime.LoadMod(
                    actor, "over-limit-3", "Instance.new('Folder')", persistToStore: false),
                "a second destroy leaves one free slot");
        }

        [Test]
        public void LuaCs_EmergencyInstanceCeiling_ChargesInstancesRestoredBeforeTheRuntimeExisted()
        {
            ActorContext actor = QuotaActor("ceiling-actor");
            LuaCsRbxApiBindings rbxApi = RestoreWorldBeforeTheRuntime(
                actor.ActorId, 0, LuaCsModRuntime.EmergencyMaxRegisteredInstances);
            LuaCsModStack stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                RbxApi = rbxApi
            });

            Exception exception = Assert.Catch(() => stack.Runtime.LoadMod(
                    actor, "ceiling-a-1", "Instance.new('Folder')", persistToStore: false),
                "restored records must count toward the emergency ceiling");
            StringAssert.Contains(
                "emergency registered instances ceiling reached ("
                + LuaCsModRuntime.EmergencyMaxRegisteredInstances + ")",
                exception.Message);
            Assert.AreEqual(0, CountLiveOwnedBy(rbxApi.Registry, actor.ActorId));
        }

        [Test]
        public void LuaCs_RegisteredInstanceQuota_ChargesRestoredHostContentButNotTheWorldSkeleton()
        {
            const int quota = 2;
            LuaCsRbxApiBindings rbxApi = RestoreWorldBeforeTheRuntime("unused-actor", 0, 1);
            LuaCsModStack stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                RbxApi = rbxApi,
                MaxRegisteredInstancesPerActor = quota
            });
            Assert.IsNotNull(stack.Runtime);

            Assert.DoesNotThrow(() => rbxApi.Registry.Create("Folder"),
                "the restored DataModel, services and Camera are not charged; only the restored host folder is");
            RbxError refused = Assert.Throws<RbxError>(
                () => rbxApi.Registry.Create("Folder"),
                "the restored host folder plus one new one fill the host/system quota");
            Assert.AreEqual(RbxErrorCode.BudgetExceeded, refused.Code);
            StringAssert.Contains("host/system", refused.Message);
            StringAssert.Contains(
                "registered instances quota reached (limit " + quota + ")", refused.Message);
        }

        [Test]
        public void LuaCs_RegisteredInstanceQuota_AFreshWorldsSkeletonIsNotCharged()
        {
            LuaCsRbxApiBindings rbxApi = new();
            LuaCsModStack stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                RbxApi = rbxApi,
                MaxRegisteredInstancesPerActor = 1
            });
            Assert.IsNotNull(stack.Runtime);

            Assert.DoesNotThrow(() => rbxApi.Registry.Create("Folder"),
                "a world that was not restored keeps its whole host/system quota");
            RbxError refused = Assert.Throws<RbxError>(
                () => rbxApi.Registry.Create("Folder"));
            Assert.AreEqual(RbxErrorCode.BudgetExceeded, refused.Code);
            StringAssert.Contains("host/system", refused.Message);
            StringAssert.Contains("registered instances quota reached (limit 1)", refused.Message);
        }

        /// <summary>
        /// WHY: the quota refusal used to be thrown from inside the registry's Registered multicast
        /// and cleaned up with Destroy, so a subscriber added after the runtime never heard Registered
        /// for the refused record yet heard its Unregistered. The runtime now refuses through a
        /// registry admission, before the record exists for anyone.
        /// </summary>
        [Test]
        public void LuaCs_RegisteredInstanceQuota_ARefusedCreationIsNeverAnnouncedToALaterSubscriber()
        {
            LuaCsRbxApiBindings rbxApi = new();
            InstanceRegistry registry = rbxApi.Registry;
            RecordingRegistrationAdmission earlier = new();
            registry.AddRegistrationAdmission(earlier);
            LuaCsModRuntime runtime = new(rbxApi: rbxApi, maxRegisteredInstancesPerActor: 1);
            List<InstanceRecord> registered = new();
            List<InstanceRecord> unregistered = new();
            registry.Registered += record => registered.Add(record);
            registry.Unregistered += record => unregistered.Add(record);

            RbxInstance admitted = registry.Create("Folder");

            Assert.AreEqual(1, registered.Count, "the positive twin: an admitted creation is announced");
            Assert.AreSame(admitted, registered[0].Instance);
            int liveBeforeRefusal = registry.Count;

            RbxError refused = Assert.Throws<RbxError>(
                () => registry.Create("Folder"));

            Assert.AreEqual(
                "BUDGET_EXCEEDED: creating Folder: actor 'host/system' cannot register instance '"
                + earlier.Admitted[1].Id.Value
                + "': registered instances quota reached (limit 1) | fix: destroy instances you no longer "
                + "need with Instance:Destroy() before creating more",
                refused.Message,
                "the refusal is a coded budget error that names what was being created (A3-05)");
            Assert.IsNull(refused.InnerException, "nothing was built, so there is no cleanup to fail");
            Assert.AreEqual(1, registered.Count, "the refused record is never announced");
            Assert.IsEmpty(unregistered,
                "a record that was never registered must never be unregistered");
            Assert.AreEqual(liveBeforeRefusal, registry.Count);
            Assert.IsFalse(registry.TryGetRecord(earlier.Admitted[1].Id, out InstanceRecord _),
                "the registry holds no record of the refused creation");
            CollectionAssert.AreEqual(new[] { earlier.Admitted[1] }, earlier.Revoked,
                "a check installed before the runtime is told the record it admitted will never exist");

            admitted.Destroy();
            Assert.AreEqual(1, unregistered.Count, "an admitted record is unregistered exactly once");
            Assert.DoesNotThrow(() => registry.Create("Folder"),
                "destroying the admitted record frees its slot, and the refusal consumed none");

            Assert.AreEqual(2, registry.RegistrationAdmissionCount);
            runtime.ShutdownWithoutPersistence();
            Assert.AreEqual(1, registry.RegistrationAdmissionCount,
                "a stopped runtime removes its own admission and leaves the other one installed");
            Assert.DoesNotThrow(() => registry.Create("Folder"),
                "a stopped runtime no longer bounds the world");
        }

        /// <summary>An admission check that admits everything and records what it was told.</summary>
        private sealed class RecordingRegistrationAdmission : IInstanceRegistrationAdmission
        {
            public List<InstanceRecord> Admitted { get; } = new();

            public List<InstanceRecord> Revoked { get; } = new();

            public string Admit(InstanceRecord record)
            {
                Admitted.Add(record);
                return null;
            }

            public void Revoke(InstanceRecord record)
            {
                Revoked.Add(record);
            }
        }

        [Test]
        public void LuaCs_EmergencyInstanceCeiling_InjectedValueIsEnforcedAndCanOnlyLowerTheCeiling()
        {
            const int ceiling = 3;
            LuaCsRbxApiBindings rbxApi = new();
            LuaCsModRuntime runtime = new(
                rbxApi: rbxApi,
                maxRegisteredInstancesPerActor: 100,
                emergencyMaxRegisteredInstances: ceiling);
            Assert.AreEqual(ceiling, runtime.EmergencyRegisteredInstanceCeiling);

            List<RbxInstance> created = new();
            for (int index = 0; index < ceiling; index++)
            {
                created.Add(rbxApi.Registry.Create("Folder"));
            }

            int liveBeforeRefusal = rbxApi.Registry.Count;
            RbxError refused = Assert.Throws<RbxError>(
                () => rbxApi.Registry.Create("Folder"),
                "the injected ceiling, not the 16384 constant, bounds the world");
            Assert.AreEqual(RbxErrorCode.BudgetExceeded, refused.Code);
            StringAssert.Contains("destroy instances you no longer need", refused.Fix);
            StringAssert.Contains(
                "emergency registered instances ceiling reached (" + ceiling + ")", refused.Message);
            Assert.AreEqual(liveBeforeRefusal, rbxApi.Registry.Count,
                "the refused creation must leave nothing behind");

            created[0].Destroy();
            Assert.DoesNotThrow(() => rbxApi.Registry.Create("Folder"),
                "destroying a charged instance frees one slot under the injected ceiling");

            LuaCsModRuntime lifted = new(
                rbxApi: new LuaCsRbxApiBindings(),
                emergencyMaxRegisteredInstances: LuaCsModRuntime.EmergencyMaxRegisteredInstances * 2);
            Assert.AreEqual(LuaCsModRuntime.DefaultEmergencyMaxRegisteredInstances,
                lifted.EmergencyRegisteredInstanceCeiling,
                "a host may lower the emergency ceiling but never lift it above the platform bound");
            Assert.AreEqual(1, new LuaCsModRuntime(emergencyMaxRegisteredInstances: 0)
                .EmergencyRegisteredInstanceCeiling);
            Assert.AreEqual(LuaCsModRuntime.DefaultEmergencyMaxRegisteredInstances,
                new LuaCsModRuntime().EmergencyRegisteredInstanceCeiling);
            Assert.AreEqual(LuaCsModRuntime.EmergencyMaxRegisteredInstances,
                LuaCsModRuntime.DefaultEmergencyMaxRegisteredInstances,
                "outside a WebGL player the default emergency ceiling stays 16384");
        }

        [Test]
        public void LuaCs_WebGlEmergencyCeiling_AWorldGrownToItStillFitsTheWebGlSaveBudget()
        {
            const int webGlSaveBudget =
                CoreAI.Mods.WorldPackages.FileRbxWorldPackageStore.MaximumWebGlSafeInstances;
            LuaCsRbxApiBindings rbxApi = new();
            LuaCsModRuntime runtime = new(
                rbxApi: rbxApi,
                maxRegisteredInstancesPerActor: LuaCsModRuntime.EmergencyMaxRegisteredInstances,
                emergencyMaxRegisteredInstances: LuaCsModRuntime.WebGlEmergencyMaxRegisteredInstances);
            Assert.AreEqual(LuaCsModRuntime.WebGlEmergencyMaxRegisteredInstances,
                runtime.EmergencyRegisteredInstanceCeiling);

            int created = 0;
            RbxError refusal = null;
            for (int attempt = 0;
                 attempt <= LuaCsModRuntime.WebGlEmergencyMaxRegisteredInstances && refusal == null;
                 attempt++)
            {
                try
                {
                    RbxInstance folder = rbxApi.Registry.Create("Folder");
                    folder.Parent = rbxApi.Registry.WorldRoot;
                    created++;
                }
                catch (RbxError exception)
                {
                    refusal = exception;
                }
            }

            Assert.IsNotNull(refusal, "growth must stop at the WebGL emergency ceiling");
            StringAssert.Contains(
                "emergency registered instances ceiling reached ("
                + LuaCsModRuntime.WebGlEmergencyMaxRegisteredInstances + ")",
                refusal.Message);
            int capturedNodes = InstanceTreeSerializer.Capture(rbxApi.Game).Instances.Count;
            Assert.LessOrEqual(capturedNodes, webGlSaveBudget,
                "a world grown to the WebGL ceiling must still pass the WebGL package instance budget, "
                + "or the pre-mutation autosave would refuse it and lock every gated tool");
            Assert.LessOrEqual(capturedNodes - created, LuaCsModRuntime.UnchargedWorldSkeletonAllowance,
                "the uncharged world skeleton must fit the allowance kept under the save budget");
            Assert.Greater(LuaCsModRuntime.EmergencyMaxRegisteredInstances, webGlSaveBudget,
                "the desktop ceiling alone would let a script outgrow the WebGL save budget");
        }

        [Test]
        public void LuaCs_EventSubscriptionQuota_IsPerActorAtNAndNPlusOne()
        {
            LuaCsModStack stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                MaxEventSubscriptionsPerActor = 2
            });
            ActorContext actor = new LocalActorIdentityProvider("subscription-actor")
                .GetActorContext(BuiltInAgentRoleIds.Programmer);

            Assert.DoesNotThrow(() => stack.Runtime.LoadMod(
                actor,
                "subscription-a-1",
                "hooks_on('one', function() end)\nhooks_on('two', function() end)",
                persistToStore: false));

            Exception exception = Assert.Catch(() => stack.Runtime.LoadMod(
                actor,
                "subscription-a-2",
                "hooks_on('three', function() end)",
                persistToStore: false));
            StringAssert.Contains(actor.ActorId, exception.Message);
            StringAssert.Contains("event subscriptions quota", exception.Message);
            StringAssert.Contains("2", exception.Message);

            ActorContext secondActor = new LocalActorIdentityProvider("subscription-actor-2")
                .GetActorContext(BuiltInAgentRoleIds.Programmer);
            Assert.DoesNotThrow(() => stack.Runtime.LoadMod(
                secondActor,
                "subscription-b-1",
                "hooks_on('one', function() end)",
                persistToStore: false));
        }

        [Test]
        public void LuaCs_PrintAndReport_AreAppendedToLogService()
        {
            LuaLogService logService = new();
            LuaCsModStack stack = BuildStack(logService: logService);

            stack.Runtime.LoadMod("log_mod", "print('p1')\nreport('r1')", persistToStore: false);

            IReadOnlyList<LuaLogEntry> entries = logService.Query(new LuaLogQuery { ModId = "log_mod" });
            Assert.AreEqual(2, entries.Count,
                "print and report at load time must each produce one log-service entry.");
            Assert.IsTrue(entries.All(e => e.Level == LuaLogLevel.Print),
                "print/report emissions must be captured at the Print level.");
            Assert.IsTrue(entries.All(e => e.ModId == "log_mod"));
            Assert.AreEqual("p1", entries[0].Message);
            Assert.AreEqual("r1", entries[1].Message);
        }

        [Test]
        public void LuaCs_Warn_IsAppendedToLogServiceAtWarnLevel_WhenTheFactoryWiresTheRbxApi()
        {
            // WHY: warn belongs in the mod's own log next to its print output, where get_mod_logs and
            // the repair loop read it; the factory left the Rbx surface unattached, so every mod's
            // warn went to the host's log sink instead.
            LuaLogService logService = new();
            List<string> hostLog = new();
            LuaCsRbxApiBindings bindings = new(log: hostLog.Add);
            try
            {
                LuaCsModStack stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
                {
                    Logger = new FakeGameLogger(),
                    ModStore = new MemoryStore(),
                    Capabilities = LuaCapabilities.All,
                    OneOffCapabilities = LuaCapabilities.All,
                    LogService = logService,
                    RbxApi = bindings
                });

                stack.Runtime.LoadMod("warn_mod", "print('p')\nwarn('x', 1)", persistToStore: false);

                IReadOnlyList<LuaLogEntry> entries = logService.Query(new LuaLogQuery { ModId = "warn_mod" });
                Assert.AreEqual(2, entries.Count, "print and warn each append one entry");
                Assert.AreEqual(LuaLogLevel.Print, entries[0].Level);
                Assert.AreEqual(LuaLogLevel.Warn, entries[1].Level, "warn is appended at the Warn level");
                Assert.AreEqual("warn_mod", entries[1].ModId);
                Assert.AreEqual("x 1", entries[1].Message, "arguments are joined by a space, as on Roblox");
                Assert.IsFalse(hostLog.Any(line => line.Contains("warn from")),
                    "a warn that reached the mod log is not written to the host's log sink as well; host log: "
                    + string.Join(" || ", hostLog));
            }
            finally
            {
                bindings.Dispose();
            }
        }

        [Test]
        public void LuaCs_Warn_WithoutALogService_StaysOnTheRbxApiLogSink()
        {
            List<string> hostLog = new();
            LuaCsRbxApiBindings bindings = new(log: hostLog.Add);
            try
            {
                LuaCsModStack stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
                {
                    Logger = new FakeGameLogger(),
                    ModStore = new MemoryStore(),
                    Capabilities = LuaCapabilities.All,
                    OneOffCapabilities = LuaCapabilities.All,
                    RbxApi = bindings
                });

                stack.Runtime.LoadMod("warn_mod", "warn('x')", persistToStore: false);

                Assert.IsTrue(hostLog.Any(line => line.Contains("warn from mod 'warn_mod'")
                                                  && line.EndsWith(": x", StringComparison.Ordinal)),
                    "with no mod log configured the stack attaches none, so warn keeps its log sink; host log: "
                    + string.Join(" || ", hostLog));
            }
            finally
            {
                bindings.Dispose();
            }
        }

        [Test]
        public void LuaCs_HandlerError_IsAppendedToLogServiceAsRuntimeError()
        {
            LuaLogService logService = new();
            LuaCsModStack stack = BuildStack(logService: logService);
            stack.Runtime.LoadMod("err_mod",
                "hooks_every(0.05, function() error('kaput') end)", persistToStore: false);

            stack.Runtime.Tick(0.1);

            IReadOnlyList<LuaLogEntry> entries = logService.Query(
                new LuaLogQuery { ModId = "err_mod", MinLevel = LuaLogLevel.RuntimeError });
            Assert.AreEqual(1, entries.Count);
            Assert.AreEqual(LuaLogLevel.RuntimeError, entries[0].Level);
            StringAssert.Contains("kaput", entries[0].Message);
        }

        [Test]
        public void LuaCs_LoadParseError_IsAppendedToLogService_AndStillThrows()
        {
            LuaLogService logService = new();
            LuaCsModStack stack = BuildStack(logService: logService);

            Assert.Catch(() => stack.Runtime.LoadMod("bad_mod", "this is not lua ((", persistToStore: false));

            Assert.IsFalse(stack.Runtime.IsLoaded("bad_mod"),
                "A failed load must still leave no mod behind — the log entry is additive.");
            IReadOnlyList<LuaLogEntry> entries = logService.Query(
                new LuaLogQuery { ModId = "bad_mod", MinLevel = LuaLogLevel.RuntimeError });
            Assert.AreEqual(1, entries.Count);
            StringAssert.Contains("load failed", entries[0].Message);
        }

        [Test]
        public void LuaCs_Quarantine_IsAppendedToLogServiceAsError()
        {
            LuaLogService logService = new();
            LuaCsModStack stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = new FakeGameLogger(),
                ModStore = new MemoryStore(),
                LogService = logService,
                MaxErrorsBeforeQuarantine = 1
            });
            stack.Runtime.LoadMod("q_mod",
                "hooks_every(0.05, function() error('boom') end)", persistToStore: false);

            stack.Runtime.Tick(0.1);

            Assert.IsTrue(stack.Runtime.ListMods()[0].Quarantined);
            IReadOnlyList<LuaLogEntry> entries = logService.Query(
                new LuaLogQuery { ModId = "q_mod", MinLevel = LuaLogLevel.Error });
            bool hasRuntimeError = false;
            bool hasQuarantineEntry = false;
            foreach (LuaLogEntry entry in entries)
            {
                if (entry.Level == LuaLogLevel.RuntimeError && entry.Message.Contains("boom"))
                {
                    hasRuntimeError = true;
                }

                if (entry.Level == LuaLogLevel.Error && entry.Message.Contains("quarantined"))
                {
                    hasQuarantineEntry = true;
                }
            }

            Assert.IsTrue(hasRuntimeError, "The handler failure itself must be logged as RuntimeError.");
            Assert.IsTrue(hasQuarantineEntry, "The quarantine transition must be logged as Error.");
        }

        [Test]
        public void LuaCs_NullLogService_ReportAndErrorPipelinesStillWork()
        {
            LuaCsModStack stack = BuildStack();

            Assert.DoesNotThrow(() => stack.Runtime.LoadMod("plain_mod",
                "print('ok')\nreport('ok2')\nhooks_every(0.05, function() error('x') end)",
                persistToStore: false));
            Assert.DoesNotThrow(() => stack.Runtime.Tick(0.1));
            Assert.AreEqual(2, stack.Runtime.GetRecentReports("plain_mod").Count);
            Assert.AreEqual(1, stack.Runtime.GetRecentHandlerErrors("plain_mod").Count);
        }

        [Test]
        public void LuaCs_Factory_BuildsFullyWiredStack()
        {
            LuaCsModStack stack = BuildStack(new MemoryStore(), new FakeCommandSink());

            Assert.IsNotNull(stack.Runtime);
            Assert.IsNotNull(stack.ToolExecutor);
            Assert.IsNotNull(stack.GameplayBindings);
            Assert.IsTrue(LuaCsModRuntime.IsSupported, "Lua-CSharp runtime must report supported.");
            Assert.IsTrue(LuaCsGameToolExecutor.IsSupported, "Lua-CSharp one-off executor must report supported.");
            Assert.AreEqual(LuaCapabilities.All, stack.GameplayBindings.Capabilities);
        }

        [Test]
        public void LuaCs_MutationEnvelope_DuplicateOperationId_ReturnsFirstProductionResult()
        {
            InstanceRegistry registry = new(
                worldAclVersion: InstanceRegistry.CurrentWorldAclVersion);
            LuaCsRbxApiBindings bindings = new(registry: registry);
            LuaCsModStack stack = BuildMutationStack(bindings);
            RbxInstance target = CreateMutationTarget(registry, "DuplicateTarget");
            ActorContext actor = MutationActor("duplicate-actor", "session-1");
            long initialRevision = MutationRecord(registry, target).Revision;
            MutationEnvelope envelope = new(
                actor.ActorId, target.Id, "duplicate-operation", initialRevision);
            const string source = @"
                local target = workspace:FindFirstChild('DuplicateTarget')
                local count = target:GetAttribute('Count') or 0
                target:SetAttribute('Count', count + 1)
                return target:GetAttribute('Count')";

            LuaTool.LuaResult first = ExecuteMutation(stack, actor, envelope, source);
            LuaTool.LuaResult replay = ExecuteMutation(stack, actor, envelope, source);

            Assert.IsTrue(first.Success, first.Error);
            Assert.AreSame(first, replay, "A duplicate must return the first production result object.");
            Assert.AreEqual("1", replay.Output);
            Assert.AreEqual(1d, target.GetAttribute("Count"));
            Assert.AreEqual(initialRevision + 1L, MutationRecord(registry, target).Revision);
            Assert.AreEqual(1, registry.RetainedMutationOperationCount);
        }

        [Test]
        public void LuaCs_MutationEnvelope_StaleRevision_IsRefusedBeforeProductionMutation()
        {
            InstanceRegistry registry = new(
                worldAclVersion: InstanceRegistry.CurrentWorldAclVersion);
            LuaCsRbxApiBindings bindings = new(registry: registry);
            LuaCsModStack stack = BuildMutationStack(bindings);
            RbxInstance target = CreateMutationTarget(registry, "StaleTarget");
            ActorContext actor = MutationActor("stale-actor", "session-1");
            long currentRevision = MutationRecord(registry, target).Revision;
            MutationEnvelope envelope = new(
                actor.ActorId, target.Id, "stale-operation", currentRevision - 1L);

            LuaTool.LuaResult result = ExecuteMutation(
                stack, actor, envelope,
                "workspace:FindFirstChild('StaleTarget').Name = 'AppliedUnexpectedly'");

            Assert.IsFalse(result.Success);
            StringAssert.Contains("actor 'stale-actor'", result.Error);
            StringAssert.Contains("stale expected revision", result.Error);
            StringAssert.Contains("current revision is " + currentRevision, result.Error);
            Assert.AreEqual("StaleTarget", target.Name);
            Assert.AreEqual(currentRevision, MutationRecord(registry, target).Revision);
            Assert.AreEqual(0, registry.RetainedMutationOperationCount);
        }

        [Test]
        public void LuaCs_MutationEnvelope_UnparentedInstanceNew_AdvancesCreationAnchorRevision()
        {
            InstanceRegistry registry = new(
                worldAclVersion: InstanceRegistry.CurrentWorldAclVersion);
            LuaCsRbxApiBindings bindings = new(registry: registry);
            LuaCsModStack stack = BuildMutationStack(bindings);
            RbxInstance anchor = CreateMutationTarget(registry, "CreationAnchor");
            ActorContext actor = MutationActor("create-actor", "session-1");
            long initialRevision = MutationRecord(registry, anchor).Revision;
            int initialCount = registry.Count;
            MutationEnvelope firstEnvelope = new(
                actor.ActorId, anchor.Id, "create-operation-1", initialRevision);

            LuaTool.LuaResult first = ExecuteMutation(
                stack, actor, firstEnvelope,
                "local created=Instance.new('Folder'); return created.ClassName");

            Assert.IsTrue(first.Success, first.Error);
            Assert.AreEqual("Folder", first.Output);
            Assert.Greater(registry.Count, initialCount);
            int countAfterFirst = registry.Count;
            Assert.AreEqual(initialRevision + 1L,
                MutationRecord(registry, anchor).Revision);

            MutationEnvelope staleEnvelope = new(
                actor.ActorId, anchor.Id, "create-operation-2", initialRevision);
            LuaTool.LuaResult stale = ExecuteMutation(
                stack, actor, staleEnvelope,
                "Instance.new('Folder'); return 'unexpected'");

            Assert.IsFalse(stale.Success);
            StringAssert.Contains("stale expected revision", stale.Error);
            Assert.AreEqual(countAfterFirst, registry.Count);
        }

        [Test]
        public void LuaCs_MutationEnvelope_Clone_AdvancesSourceRevision()
        {
            InstanceRegistry registry = new(
                worldAclVersion: InstanceRegistry.CurrentWorldAclVersion);
            LuaCsRbxApiBindings bindings = new(registry: registry);
            LuaCsModStack stack = BuildMutationStack(bindings);
            RbxInstance source = CreateMutationTarget(registry, "CloneRevisionSource");
            ActorContext actor = MutationActor("clone-actor", "session-1");
            long initialRevision = MutationRecord(registry, source).Revision;
            int initialCount = registry.Count;
            MutationEnvelope firstEnvelope = new(
                actor.ActorId, source.Id, "clone-operation-1", initialRevision);
            const string cloneSource = @"
                local source = workspace:FindFirstChild('CloneRevisionSource')
                local copy = source:Clone()
                return copy.Name";

            LuaTool.LuaResult first = ExecuteMutation(
                stack, actor, firstEnvelope, cloneSource);

            Assert.IsTrue(first.Success, first.Error);
            Assert.AreEqual("CloneRevisionSource", first.Output);
            Assert.Greater(registry.Count, initialCount);
            int countAfterFirst = registry.Count;
            Assert.AreEqual(initialRevision + 1L,
                MutationRecord(registry, source).Revision);

            MutationEnvelope staleEnvelope = new(
                actor.ActorId, source.Id, "clone-operation-2", initialRevision);
            LuaTool.LuaResult stale = ExecuteMutation(
                stack, actor, staleEnvelope, cloneSource);

            Assert.IsFalse(stale.Success);
            StringAssert.Contains("stale expected revision", stale.Error);
            Assert.AreEqual(countAfterFirst, registry.Count);
        }

        [Test]
        public void LuaCs_MutationEnvelope_ConcurrentSameRevision_DeterministicallyOrdersFirstEntrant()
        {
            InstanceRegistry registry = new(
                worldAclVersion: InstanceRegistry.CurrentWorldAclVersion);
            LuaCsRbxApiBindings bindings = new(registry: registry);
            ManualResetEventSlim firstEntered = new(false);
            ManualResetEventSlim releaseFirst = new(false);
            ManualResetEventSlim secondStarted = new(false);
            LuaCsModStack firstStack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = new FakeGameLogger(),
                Capabilities = LuaCapabilities.All,
                OneOffCapabilities = LuaCapabilities.All,
                RbxApi = bindings,
                AdditionalGameplayBindings = (apiRegistry, capabilities) =>
                    apiRegistry.Register("test_hold_mutation", new Action(() =>
                    {
                        firstEntered.Set();
                        releaseFirst.Wait();
                    }))
            });
            LuaCsModStack secondStack = BuildMutationStack(bindings);
            RbxInstance target = CreateMutationTarget(registry, "ConcurrentTarget");
            ActorContext firstActor = MutationActor("concurrent-a", "session-a");
            ActorContext secondActor = MutationActor("concurrent-b", "session-b");
            long initialRevision = MutationRecord(registry, target).Revision;
            MutationEnvelope firstEnvelope = new(
                firstActor.ActorId, target.Id, "concurrent-a-operation", initialRevision);
            MutationEnvelope secondEnvelope = new(
                secondActor.ActorId, target.Id, "concurrent-b-operation", initialRevision);
            Task<LuaTool.LuaResult> firstTask = Task.Run(() =>
            {
                return ExecuteMutation(firstStack, firstActor, firstEnvelope,
                    "test_hold_mutation(); "
                    + "local t=workspace:FindFirstChild('ConcurrentTarget'); "
                    + "t:SetAttribute('Winner','a'); return 'a'");
            });
            bool firstDidEnter = firstEntered.Wait(TimeSpan.FromSeconds(5));
            if (!firstDidEnter)
            {
                releaseFirst.Set();
            }

            Assert.IsTrue(firstDidEnter,
                "Actor A did not enter the production mutation before the timeout.");
            Task<LuaTool.LuaResult> secondTask = Task.Run(() =>
            {
                secondStarted.Set();
                return ExecuteMutation(secondStack, secondActor, secondEnvelope,
                    "local t=workspace:FindFirstChild('ConcurrentTarget'); "
                    + "t:SetAttribute('Winner','b'); return 'b'");
            });

            bool secondDidStart = secondStarted.Wait(TimeSpan.FromSeconds(5));
            releaseFirst.Set();
            Assert.IsTrue(secondDidStart,
                "Actor B did not start its concurrent production mutation before the timeout.");
            Task.WaitAll(firstTask, secondTask);
            LuaTool.LuaResult first = firstTask.Result;
            LuaTool.LuaResult second = secondTask.Result;

            Assert.IsTrue(first.Success, first.Error);
            Assert.IsFalse(second.Success);
            Assert.AreEqual("a", first.Output);
            Assert.AreEqual("a", target.GetAttribute("Winner"));
            StringAssert.Contains("stale expected revision", second.Error);
            Assert.AreEqual(initialRevision + 1L, MutationRecord(registry, target).Revision);
            Assert.AreEqual(1, registry.RetainedMutationOperationCount);
        }

        [Test]
        public void LuaCs_MutationEnvelope_ReconnectingDurableActor_DoesNotDoubleApplyOrForkState()
        {
            InstanceRegistry registry = new(
                worldAclVersion: InstanceRegistry.CurrentWorldAclVersion);
            LuaCsRbxApiBindings bindings = new(registry: registry);
            LuaCsModStack firstStack = BuildMutationStack(bindings);
            LuaCsModStack reconnectedStack = BuildMutationStack(bindings);
            RbxInstance target = CreateMutationTarget(registry, "ReconnectTarget");
            ActorContext firstConnection = MutationActor("durable-actor", "session-before");
            ActorContext reconnected = MutationActor("durable-actor", "session-after");
            long initialRevision = MutationRecord(registry, target).Revision;
            MutationEnvelope envelope = new(
                firstConnection.ActorId, target.Id, "reconnect-operation", initialRevision);
            const string source = @"
                local target = workspace:FindFirstChild('ReconnectTarget')
                local count = target:GetAttribute('Count') or 0
                target:SetAttribute('Count', count + 1)
                return target:GetAttribute('Count')";

            LuaTool.LuaResult first = ExecuteMutation(
                firstStack, firstConnection, envelope, source);
            LuaTool.LuaResult replay = ExecuteMutation(
                reconnectedStack, reconnected, envelope, source);

            Assert.AreNotEqual(firstConnection.SessionId, reconnected.SessionId);
            Assert.AreEqual(firstConnection.ActorId, reconnected.ActorId);
            Assert.AreNotSame(firstStack.ToolExecutor, reconnectedStack.ToolExecutor);
            Assert.IsTrue(first.Success, first.Error);
            Assert.AreSame(first, replay);
            Assert.AreEqual(1d, target.GetAttribute("Count"));
            Assert.AreEqual(initialRevision + 1L, MutationRecord(registry, target).Revision);
            Assert.AreEqual(1, registry.RetainedMutationOperationCount);
        }

        [Test]
        public void LuaCs_MutationEnvelope_WorldTeardown_ClearsRetainedOperationState()
        {
            InstanceRegistry registry = new(
                worldAclVersion: InstanceRegistry.CurrentWorldAclVersion);
            LuaCsRbxApiBindings bindings = new(registry: registry);
            LuaCsModStack stack = BuildMutationStack(bindings);
            RbxInstance target = CreateMutationTarget(registry, "TeardownTarget");
            ActorContext actor = MutationActor("teardown-actor", "session-1");
            MutationEnvelope envelope = new(
                actor.ActorId, target.Id, "teardown-operation",
                MutationRecord(registry, target).Revision);

            LuaTool.LuaResult first = ExecuteMutation(
                stack, actor, envelope,
                "local t=workspace:FindFirstChild('TeardownTarget'); "
                + "t:SetAttribute('Touched',true); return 'applied'");
            Assert.IsTrue(first.Success, first.Error);
            Assert.AreEqual(1, registry.RetainedMutationOperationCount);

            registry.MarkDetached();

            Assert.AreEqual(0, registry.RetainedMutationOperationCount);
            LuaTool.LuaResult afterTeardown = ExecuteMutation(
                stack, actor, envelope, "return 'must not replay'");
            Assert.IsFalse(afterTeardown.Success);
            StringAssert.Contains("WORLD_DETACHED", afterTeardown.Error);
            StringAssert.Contains("actor 'teardown-actor'", afterTeardown.Error);
        }

        [Test]
        public void LuaCs_HooksOnAndHooksEvery_TickFiresTimer_EmitEventFiresHandler()
        {
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(store);
            stack.Runtime.LoadMod("m", @"
                hooks_on('hit', function(name, payload) store_set('last', payload) end)
                hooks_every(0.1, function()
                    local n = tonumber(store_get('ticks')) or 0
                    store_set('ticks', tostring(n + 1))
                end)");

            Assert.IsTrue(stack.Runtime.IsLoaded("m"));

            stack.Runtime.Tick(0.05);
            Assert.AreEqual("", store.Get("m", "ticks"), "Timer must not fire before its interval elapses.");

            stack.Runtime.Tick(0.06);
            Assert.AreEqual("1", store.Get("m", "ticks"), "Timer fires once the 0.1s interval elapses.");

            stack.Runtime.EmitEvent("hit", "42");
            stack.Runtime.Tick(0);
            Assert.AreEqual("42", store.Get("m", "last"), "EmitEvent + Tick dispatches the hooks_on handler.");
        }

        [Test]
        public void LuaCs_EmitEvent_RoutesOnlyToSubscribers_AndTouchesOnlySubscriberEntries()
        {
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(store);
            stack.Runtime.LoadMod("subscriber",
                "hooks_on('target', function() store_set('ran', 'yes') end)");
            stack.Runtime.LoadMod("non-subscriber",
                "hooks_on('other', function() store_set('ran', 'yes') end)");

            FieldInfo touchedField = typeof(LuaCsModRuntime).GetField(
                "_subscriptionEntriesTouched", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(touchedField);
            long touchedBefore = (long)touchedField.GetValue(stack.Runtime);
            stack.Runtime.EmitEvent("target", "");
            long touched = (long)touchedField.GetValue(stack.Runtime) - touchedBefore;
            stack.Runtime.Tick(0);

            Assert.AreEqual("yes", store.Get("subscriber", "ran"));
            Assert.AreEqual("", store.Get("non-subscriber", "ran"));
            Assert.AreEqual(1L, touched,
                "Routing must touch exactly the subscribed mod entry, not every loaded mod.");
        }

        private static void AssertNoSynchronizationBeforeBoundary(MethodInfo root, MethodInfo boundary)
        {
            HashSet<MethodBase> visited = new();
            bool reachedBoundary = InspectCallsBeforeBoundary(root, root, boundary, visited);
            Assert.IsTrue(reachedBoundary,
                $"Emit entry '{root.Name}' must reach the per-subscriber '{boundary.Name}' boundary.");
        }

        private static bool InspectCallsBeforeBoundary(
            MethodInfo root,
            MethodInfo method,
            MethodInfo boundary,
            HashSet<MethodBase> visited)
        {
            if (!visited.Add(method))
            {
                return false;
            }

            List<MethodBase> calls = ReadCalledMethods(method);
            foreach (MethodBase call in calls)
            {
                if (call.Module == boundary.Module && call.MetadataToken == boundary.MetadataToken)
                {
                    return true;
                }

                Assert.IsFalse(IsSynchronizationAcquisition(call),
                    $"Emit entry '{root.Name}' acquires synchronization through " +
                    $"'{call.DeclaringType?.FullName}.{call.Name}' before '{boundary.Name}'.");

                MethodInfo runtimeCall = call as MethodInfo;
                if (runtimeCall != null && runtimeCall.DeclaringType == typeof(LuaCsModRuntime) &&
                    InspectCallsBeforeBoundary(root, runtimeCall, boundary, visited))
                {
                    return true;
                }
            }

            return false;
        }

        private static void AssertNoSynchronizationOutsideBoundary(MethodInfo root, MethodInfo boundary)
        {
            HashSet<MethodBase> visited = new();
            bool reachedBoundary = false;
            InspectCallsOutsideBoundary(root, root, boundary, visited, ref reachedBoundary);
            Assert.IsTrue(reachedBoundary,
                $"Emit route '{root.Name}' must reach the per-subscriber '{boundary.Name}' boundary.");
        }

        private static void InspectCallsOutsideBoundary(
            MethodInfo root,
            MethodInfo method,
            MethodInfo boundary,
            HashSet<MethodBase> visited,
            ref bool reachedBoundary)
        {
            if (!visited.Add(method))
            {
                return;
            }

            List<MethodBase> calls = ReadCalledMethods(method);
            foreach (MethodBase call in calls)
            {
                if (call.Module == boundary.Module && call.MetadataToken == boundary.MetadataToken)
                {
                    reachedBoundary = true;
                    continue;
                }

                Assert.IsFalse(IsSynchronizationAcquisition(call),
                    $"Emit route '{root.Name}' acquires synchronization through " +
                    $"'{call.DeclaringType?.FullName}.{call.Name}' outside '{boundary.Name}'.");

                MethodInfo runtimeCall = call as MethodInfo;
                if (runtimeCall != null && runtimeCall.DeclaringType == typeof(LuaCsModRuntime))
                {
                    InspectCallsOutsideBoundary(
                        root, runtimeCall, boundary, visited, ref reachedBoundary);
                }
            }
        }

        private static bool IsSynchronizationAcquisition(MethodBase method)
        {
            Type declaringType = method.DeclaringType;
            if (declaringType == null)
            {
                return false;
            }

            if (declaringType == typeof(Monitor))
            {
                return method.Name == "Enter" || method.Name == "TryEnter";
            }

            if (!string.Equals(declaringType.Namespace, "System.Threading", StringComparison.Ordinal))
            {
                return false;
            }

            return method.Name.StartsWith("Enter", StringComparison.Ordinal) ||
                   method.Name.StartsWith("TryEnter", StringComparison.Ordinal) ||
                   method.Name.StartsWith("Wait", StringComparison.Ordinal) ||
                   method.Name.StartsWith("Acquire", StringComparison.Ordinal);
        }

        private static List<MethodBase> ReadCalledMethods(MethodInfo method)
        {
            MethodBody body = method.GetMethodBody();
            Assert.IsNotNull(body, $"Method '{method.Name}' must expose a compiled body.");
            byte[] il = body.GetILAsByteArray();
            Assert.IsNotNull(il, $"Method '{method.Name}' must expose compiled IL.");

            Dictionary<ushort, OpCode> opCodes = new();
            FieldInfo[] opCodeFields = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static);
            foreach (FieldInfo opCodeField in opCodeFields)
            {
                OpCode opCode = (OpCode)opCodeField.GetValue(null);
                opCodes[unchecked((ushort)opCode.Value)] = opCode;
            }

            List<MethodBase> calls = new();
            int offset = 0;
            while (offset < il.Length)
            {
                ushort value = il[offset++];
                if (value == 0xfe)
                {
                    value = (ushort)(0xfe00 | il[offset++]);
                }

                Assert.IsTrue(opCodes.TryGetValue(value, out OpCode opCode),
                    $"Unknown IL opcode 0x{value:x4} in '{method.Name}'.");
                switch (opCode.OperandType)
                {
                    case OperandType.InlineNone:
                        break;
                    case OperandType.ShortInlineBrTarget:
                    case OperandType.ShortInlineI:
                    case OperandType.ShortInlineVar:
                        offset += 1;
                        break;
                    case OperandType.InlineVar:
                        offset += 2;
                        break;
                    case OperandType.InlineBrTarget:
                    case OperandType.InlineField:
                    case OperandType.InlineI:
                    case OperandType.InlineSig:
                    case OperandType.InlineString:
                    case OperandType.InlineTok:
                    case OperandType.ShortInlineR:
                        offset += 4;
                        break;
                    case OperandType.InlineMethod:
                        int metadataToken = BitConverter.ToInt32(il, offset);
                        offset += 4;
                        if (opCode.Value == OpCodes.Call.Value ||
                            opCode.Value == OpCodes.Callvirt.Value ||
                            opCode.Value == OpCodes.Newobj.Value)
                        {
                            MethodBase calledMethod = method.Module.ResolveMethod(
                                metadataToken,
                                method.DeclaringType?.GetGenericArguments(),
                                method.GetGenericArguments());
                            calls.Add(calledMethod);
                        }

                        break;
                    case OperandType.InlineI8:
                    case OperandType.InlineR:
                        offset += 8;
                        break;
                    case OperandType.InlineSwitch:
                        int caseCount = BitConverter.ToInt32(il, offset);
                        offset += 4 + (caseCount * 4);
                        break;
                    default:
                        Assert.Fail($"Unsupported IL operand '{opCode.OperandType}' in '{method.Name}'.");
                        break;
                }
            }

            return calls;
        }

        [Test]
        public void LuaCs_EmitEvent_HasNoSynchronizationBeforePerSubscriberEnqueue()
        {
            MethodInfo hostEmit = typeof(LuaCsModRuntime).GetMethod(
                "EmitEvent",
                BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                new[] { typeof(string), typeof(string) },
                null);
            MethodInfo modEmit = typeof(LuaCsModRuntime).GetMethod(
                "EmitFromMod", BindingFlags.Instance | BindingFlags.NonPublic);
            MethodInfo routeEvent = typeof(LuaCsModRuntime).GetMethod(
                "RouteEvent", BindingFlags.Instance | BindingFlags.NonPublic);
            MethodInfo enqueue = typeof(LuaCsModRuntime).GetMethod(
                "Enqueue", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(hostEmit);
            Assert.IsNotNull(modEmit);
            Assert.IsNotNull(routeEvent);
            Assert.IsNotNull(enqueue);

            AssertNoSynchronizationBeforeBoundary(hostEmit, routeEvent);
            AssertNoSynchronizationBeforeBoundary(modEmit, routeEvent);
            AssertNoSynchronizationOutsideBoundary(routeEvent, enqueue);
        }

        [Test]
        public void LuaCs_EmitEvent_PerSubscriberQueueLockIsTheAllowedSynchronizationBoundary()
        {
            LuaCsModStack stack = BuildStack();
            stack.Runtime.LoadMod("subscriber", "hooks_on('target', function() end)");
            FieldInfo snapshotField = typeof(LuaCsModRuntime).GetField(
                "_subscriptionSnapshot", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(snapshotField);
            IDictionary subscriptions = (IDictionary)snapshotField.GetValue(stack.Runtime);
            Array subscribers = (Array)subscriptions["target"];
            Assert.AreEqual(1, subscribers.Length);
            object subscriber = subscribers.GetValue(0);
            FieldInfo eventGateField = subscriber.GetType().GetField(
                "EventGate", BindingFlags.Instance | BindingFlags.Public);
            Assert.IsNotNull(eventGateField);
            object eventGate = eventGateField.GetValue(subscriber);

            ManualResetEventSlim started = new(false);
            ManualResetEventSlim completed = new(false);
            Exception emitError = null;
            Thread emitThread = new(() =>
            {
                started.Set();
                try
                {
                    stack.Runtime.EmitEvent("target", "");
                }
                catch (Exception ex)
                {
                    emitError = ex;
                }
                finally
                {
                    completed.Set();
                }
            });
            emitThread.IsBackground = true;

            bool startedInTime;
            bool completedWhileQueueHeld;
            lock (eventGate)
            {
                emitThread.Start();
                startedInTime = started.Wait(1000);
                completedWhileQueueHeld = completed.Wait(250);
            }

            bool stoppedInTime = emitThread.Join(5000);
            started.Dispose();
            completed.Dispose();

            Assert.IsTrue(startedInTime, "The emit worker must reach the subscriber queue contention check.");
            Assert.IsFalse(completedWhileQueueHeld,
                "EmitEvent may wait on the target subscriber's queue lock at the enqueue boundary.");
            Assert.IsTrue(stoppedInTime, "The emit worker must complete after the subscriber queue lock is released.");
            Assert.IsNull(emitError);
        }

        [Test]
        public void LuaCs_SubscriptionDeliveryOrder_RemainsLoadOrderAfterEmptyTick()
        {
            string[] expected = { "zeta", "alpha", "mu" };
            for (int run = 0; run < 5; run++)
            {
                List<string> delivered = new();
                LuaCsModStack stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
                {
                    Capabilities = LuaCapabilities.All,
                    OneOffCapabilities = LuaCapabilities.All,
                    AdditionalGameplayBindings = (registry, caps) =>
                        registry.Register("record_order", new Action<string>(value => delivered.Add(value)))
                });

                for (int i = 0; i < expected.Length; i++)
                {
                    string id = expected[i];
                    stack.Runtime.LoadMod(id,
                        $"hooks_on('ordered', function() record_order('{id}') end)");
                }

                stack.Runtime.EmitEvent("ordered", "");
                stack.Runtime.Tick(0);

                CollectionAssert.AreEqual(expected, delivered, $"Delivery order changed on run {run}.");

                delivered.Clear();
                stack.Runtime.Tick(0);
                stack.Runtime.EmitEvent("ordered", "");
                stack.Runtime.Tick(0);

                CollectionAssert.AreEqual(expected, delivered,
                    $"Delivery order changed after an empty tick on run {run}.");
            }
        }

        [Test]
        public void LuaCs_ActorTimerEvent_ReachesEarlierSubscriberInTheSameTick()
        {
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(store);
            stack.Runtime.LoadMod("subscriber-a", @"
                hooks_on('timer-work', function(_, payload)
                    store_set('received', payload)
                end)", persistToStore: false);
            ActorContext actor = new LocalActorIdentityProvider("timer-owner")
                .GetActorContext(BuiltInAgentRoleIds.Programmer);
            stack.Runtime.LoadMod(actor, "timer-b", @"
                hooks_every(0, function()
                    events_emit('timer-work', 'same-frame')
                end)", persistToStore: false);

            stack.Runtime.Tick(0);

            Assert.AreEqual("same-frame", store.Get("subscriber-a", "received"),
                "The timer phase must run before event dispatch, regardless of mod load order.");
        }

        [Test]
        public void LuaCs_TimersAndEvents_ShareOneGlobalInvocationBudget()
        {
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(store);
            string timerSource = string.Concat(Enumerable.Repeat(
                "hooks_every(1, function() end)\n",
                LuaCsModRuntime.DefaultMaxTimersPerMod));
            int timerModCount = LuaCsModRuntime.DefaultMaxEventsDispatchedPerTickGlobal /
                LuaCsModRuntime.DefaultMaxTimersPerMod;
            Assert.AreEqual(
                LuaCsModRuntime.DefaultMaxEventsDispatchedPerTickGlobal,
                timerModCount * LuaCsModRuntime.DefaultMaxTimersPerMod);
            for (int i = 0; i < timerModCount; i++)
            {
                stack.Runtime.LoadMod($"timer-{i}", timerSource, persistToStore: false);
            }

            stack.Runtime.LoadMod("event-subscriber",
                "hooks_on('work', function() store_set('event', 'delivered') end)",
                persistToStore: false);
            stack.Runtime.EmitEvent("work", "");
            stack.Runtime.Tick(1);

            Assert.AreEqual("", store.Get("event-subscriber", "event"),
                "Exactly 256 due timers must exhaust the shared budget and leave the event queued.");

            stack.Runtime.Tick(0);

            Assert.AreEqual("delivered", store.Get("event-subscriber", "event"));
        }

        [Test]
        public void LuaCs_Store_PersistsAcrossTicksAndReload()
        {
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(store);
            const string src = @"
                hooks_on('save', function(_, payload) store_set('v', payload) end)
                hooks_on('echo', function() store_set('after', store_get('v')) end)";
            stack.Runtime.LoadMod("m", src);

            stack.Runtime.EmitEvent("save", "persisted");
            stack.Runtime.Tick(0);
            Assert.AreEqual("persisted", store.Get("m", "v"));

            // Value survives to a later tick without being re-written.
            stack.Runtime.EmitEvent("echo", "");
            stack.Runtime.Tick(0);
            Assert.AreEqual("persisted", store.Get("m", "after"));

            // Value survives a reload: the store is host-owned and keyed by mod id, so the fresh state
            // still reads what was written before the reload.
            stack.Runtime.ReloadMod("m", src);
            store.Set("m", "after", null); // clear the marker to prove the reloaded mod reads live store
            stack.Runtime.EmitEvent("echo", "");
            stack.Runtime.Tick(0);
            Assert.AreEqual("persisted", store.Get("m", "after"),
                "store_set/get survives a reload (host-owned by mod id).");
        }

        [Test]
        public void LuaCs_EventsEmit_RaisesModEventEmitted()
        {
            LuaCsModStack stack = BuildStack();
            string gotMod = null, gotName = null, gotPayload = null;
            stack.Runtime.ModEventEmitted += (modId, name, payload) =>
            {
                gotMod = modId;
                gotName = name;
                gotPayload = payload;
            };

            // events_emit runs at load and raises ModEventEmitted synchronously.
            stack.Runtime.LoadMod("a", "events_emit('quest_event', 'payload')");

            Assert.AreEqual("a", gotMod);
            Assert.AreEqual("quest_event", gotName);
            Assert.AreEqual("payload", gotPayload);
        }

        [Test]
        public void LuaCs_ModSourceLoaded_ThrowingSubscriber_DoesNotFailLoadOrOtherSubscribers()
        {
            LuaCsModStack stack = BuildStack();
            bool healthySubscriberRan = false;
            stack.Runtime.ModSourceLoaded += (_, _, _) => throw new InvalidOperationException("boom");
            stack.Runtime.ModSourceLoaded += (_, _, _) => healthySubscriberRan = true;

            Assert.DoesNotThrow(() => stack.Runtime.LoadMod("a", "hooks_on('noop', function() end)"),
                "A throwing ModSourceLoaded subscriber must not make a healthy load fail.");

            Assert.IsTrue(stack.Runtime.IsLoaded("a"), "The mod must be loaded despite the throwing subscriber.");
            Assert.IsTrue(healthySubscriberRan, "Other subscribers must still run after one throws.");
        }

        [Test]
        [Timeout(15000)]
        public void LuaCs_Coroutine_AdvancesOneStepPerResume_CompletesWithoutHanging()
        {
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(store);
            stack.Runtime.LoadMod("m", @"
                local co = coroutine.create(function()
                    for i = 3, 1, -1 do
                        store_set('step', tostring(i))
                        coroutine.yield()
                    end
                    store_set('step', 'done')
                end)
                hooks_every(0.05, function()
                    if coroutine.status(co) ~= 'dead' then
                        coroutine.resume(co)
                    end
                end)");

            List<string> seq = new();
            for (int i = 0; i < 5; i++)
            {
                stack.Runtime.Tick(0.05); // one resume per tick
                seq.Add(store.Get("m", "step"));
            }

            // One step advances per resume; then the coroutine completes and stays done — no re-run,
            // no hang. This is the WebGL-critical path: coroutine.yield across ticks under Lua-CSharp.
            CollectionAssert.AreEqual(new[] { "3", "2", "1", "done", "done" }, seq);
            Assert.IsEmpty(stack.Runtime.GetRecentHandlerErrors("m"),
                "The coroutine pump must not raise handler errors.");
        }

        [Test]
        public void LuaCs_InterMod_ValueAndFunctionExport_ConsumerReadsAndCalls_FunctionNotReadable()
        {
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(store);

            stack.Runtime.LoadMod("provider", @"
                local base = 2.0
                mods_export('multiplier', base)
                mods_export('scale', function(x) return (tonumber(x) or 0) * base end)");

            stack.Runtime.LoadMod("consumer", @"
                hooks_on('read', function()
                    local m = mods_get('provider', 'multiplier')
                    local s = mods_call('provider', 'scale', 10)
                    store_set('ok', (m == 2.0 and s == 20.0) and 'yes' or 'no')
                    store_set('m_type', type(m))
                end)
                hooks_on('read_fn', function()
                    -- a function export is NOT readable via mods_get; this call must raise.
                    local fn = mods_get('provider', 'scale')
                    store_set('leaked', 'yes')
                end)");

            stack.Runtime.EmitEvent("read", "");
            stack.Runtime.Tick(0);
            Assert.AreEqual("yes", store.Get("consumer", "ok"),
                "Consumer reads the exported value and calls the exported function via mods_call.");
            Assert.AreEqual("number", store.Get("consumer", "m_type"),
                "Cross-mod value crosses as a plain-data copy.");

            stack.Runtime.EmitEvent("read_fn", "");
            stack.Runtime.Tick(0); // must not throw out of Tick
            Assert.AreEqual("", store.Get("consumer", "leaked"),
                "A function export must NOT be readable via mods_get.");
            IReadOnlyList<LuaModHandlerError> errors = stack.Runtime.GetRecentHandlerErrors("consumer");
            Assert.IsNotEmpty(errors, "mods_get on a function export must fail the handler.");
            StringAssert.Contains("function", errors[0].Error,
                "The error should steer the author toward mods_call.");
        }

        [Test]
        [Timeout(15000)]
        public void LuaCs_ModsCall_SelfCall_CannotDisarmHandlerGuard()
        {
            MemoryStore store = new();
            // Tight per-handler budget so the over-budget loop is cut fast: this proves the outer guard is
            // still armed after the nested mods_call, independent of the Roblox-parity default budget.
            LuaCsModStack stack = BuildStack(store, handlerMaxSteps: 5000, handlerTimeoutMs: 100);
            stack.Runtime.LoadMod("m", @"
                mods_export('noop', function() return 1 end)
                hooks_on('go', function()
                    mods_call(mod_id(), 'noop')
                    local x = 0
                    for i = 1, 1000000 do x = x + 1 end
                    store_set('escaped', 'yes')
                end)");

            stack.Runtime.EmitEvent("go", "");
            stack.Runtime.Tick(0);

            Assert.AreEqual("", store.Get("m", "escaped"),
                "A self mods_call must not disarm the outer guard: the over-budget loop after it must be cut.");
            Assert.IsNotEmpty(stack.Runtime.GetRecentHandlerErrors("m"),
                "The over-budget handler must fail with a recorded error, not run to completion unlimited.");
        }

        [Test]
        [Timeout(15000)]
        public void LuaCs_ModsCall_IndirectCycle_CannotDisarmHandlerGuard()
        {
            MemoryStore store = new();
            // Tight per-handler budget (see SelfCall test): the A->B->A cycle must not disarm A's outer guard.
            LuaCsModStack stack = BuildStack(store, handlerMaxSteps: 5000, handlerTimeoutMs: 100);
            stack.Runtime.LoadMod("a", @"
                mods_export('noop', function() return 1 end)
                hooks_on('go', function()
                    mods_call('b', 'pong')
                    local x = 0
                    for i = 1, 1000000 do x = x + 1 end
                    store_set('escaped', 'yes')
                end)");
            stack.Runtime.LoadMod("b", "mods_export('pong', function() return mods_call('a', 'noop') end)");

            stack.Runtime.EmitEvent("go", "");
            stack.Runtime.Tick(0);

            Assert.AreEqual("", store.Get("a", "escaped"),
                "An A->B->A mods_call cycle must not disarm A's outer guard (self-call bans cannot catch this).");
            Assert.IsNotEmpty(stack.Runtime.GetRecentHandlerErrors("a"),
                "The over-budget handler must fail with a recorded error.");
        }

        [Test]
        public void LuaCs_HandlerDiesInsideWorldTransaction_NextHandlerCommandStillReachesSink()
        {
            MemoryStore store = new();
            FakeCommandSink sink = new();
            LuaCsModStack stack = BuildStack(store, sink);

            stack.Runtime.LoadMod("t", @"
                hooks_on('leak', function()
                    coreai_world_begin()
                    error('dies before commit')
                end)
                hooks_on('later', function() coreai_world_destroy('victim') end)",
                LuaCapabilities.WorldEdit);

            stack.Runtime.EmitEvent("leak", "");
            stack.Runtime.Tick(0);
            Assert.IsNotEmpty(stack.Runtime.GetRecentHandlerErrors("t"), "The leaking handler must fail.");
            Assert.AreEqual(0, sink.Commands.Count,
                "The aborted transaction's buffered commands must never reach the sink.");

            stack.Runtime.EmitEvent("later", "");
            stack.Runtime.Tick(0);
            Assert.AreEqual(1, sink.Commands.Count,
                "A transaction leaked by a dead handler must not silently swallow the next handler's world command.");
        }

        [Test]
        public void LuaCs_LoadChunkDiesInsideWorldTransaction_NextLoadCommandStillReachesSink()
        {
            FakeCommandSink sink = new();
            LuaCsModStack stack = BuildStack(new MemoryStore(), sink);

            Assert.Catch(() => stack.Runtime.LoadMod("dying",
                "coreai_world_begin()\nerror('dies before commit')",
                LuaCapabilities.WorldEdit));
            Assert.IsFalse(stack.Runtime.IsLoaded("dying"));

            stack.Runtime.LoadMod("healthy", "coreai_world_destroy('victim')", LuaCapabilities.WorldEdit);
            Assert.AreEqual(1, sink.Commands.Count,
                "A transaction leaked by a failing load chunk must not swallow the next load's world command.");
        }

        [Test]
        public void LuaCs_OneOff_ExecuteReturnsOutput()
        {
            LuaCsModStack stack = BuildStack();

            LuaTool.LuaResult result = stack.ToolExecutor
                .ExecuteAsync("return 2 + 3", CancellationToken.None)
                .GetAwaiter().GetResult();

            Assert.IsTrue(result.Success, result.Error);
            Assert.AreEqual("5", result.Output);
        }

        [Test]
        [Timeout(15000)]
        public void LuaCs_OneOff_RunawayLoop_CutByInstructionBudget_DoesNotHang()
        {
            LuaCsModStack stack = BuildStack();

            LuaTool.LuaResult result = stack.ToolExecutor
                .ExecuteAsync("while true do local x = 1 end", CancellationToken.None)
                .GetAwaiter().GetResult();

            Assert.IsFalse(result.Success, "A runaway loop must be cut by the budget, not run forever.");
            Assert.IsFalse(string.IsNullOrEmpty(result.Error), "A cut runaway must report an error.");
        }

        [Test]
        public void LuaCs_WorldEditTier_RoutesCommandToSink()
        {
            MemoryStore store = new();
            FakeCommandSink sink = new();
            LuaCsModStack stack = BuildStack(store, sink);

            stack.Runtime.LoadMod("w", @"
                store_set('has_world_destroy', (coreai_world_destroy ~= nil) and 'yes' or 'no')
                hooks_on('do_it', function() coreai_world_destroy('target') end)",
                LuaCapabilities.WorldEdit);

            Assert.AreEqual("yes", store.Get("w", "has_world_destroy"),
                "A WorldEdit-tier mod must see the coreai_world_* APIs.");

            stack.Runtime.EmitEvent("do_it", "");
            stack.Runtime.Tick(0);

            Assert.AreEqual(1, sink.Commands.Count, "The world API must route exactly one command to the sink.");
            Assert.AreEqual(AiGameCommandTypeIds.WorldCommand, sink.Commands[0].CommandTypeId);
        }

        [Test]
        public void LuaCs_ReadTier_DoesNotExposeWriteApis()
        {
            MemoryStore store = new();
            FakeCommandSink sink = new();
            LuaCsModStack stack = BuildStack(store, sink);

            stack.Runtime.LoadMod("r", @"
                store_set('has_world_destroy', (coreai_world_destroy ~= nil) and 'yes' or 'no')
                store_set('has_world_spawn', (coreai_world_spawn ~= nil) and 'yes' or 'no')",
                LuaCapabilities.Read);

            Assert.AreEqual("no", store.Get("r", "has_world_destroy"),
                "A Read-tier mod must not see world-edit APIs (fail-closed).");
            Assert.AreEqual("no", store.Get("r", "has_world_spawn"));

            // Calling an absent write API from a read-tier mod fails the load (attempt to call nil).
            Assert.Catch(() => stack.Runtime.LoadMod("r2", "coreai_world_destroy('x')", LuaCapabilities.Read));
            Assert.IsFalse(stack.Runtime.IsLoaded("r2"));
            Assert.AreEqual(0, sink.Commands.Count, "No command may reach the sink from a read-tier mod.");
        }

        [Test]
        public void LuaCs_WorldBuildBindingsDisabled_StubsBuildApisButKeepsRbxAndQueries()
        {
            MemoryStore store = new();
            FakeCommandSink sink = new();
            // WHY: mirrors CoreAiModsInstaller exactly — WorldEdit capability is granted (so the Rbx
            // Instance.new build surface stays available), but the coreai_world_* BUILD bindings are replaced
            // with actionable withheld stubs via RegisterWorldEditBuildBindings = false, so the Programmer
            // builds the world only the Roblox way. Read-gated world queries must survive.
            LuaCsModStack stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = new FakeGameLogger(),
                CommandSink = sink,
                ModStore = store,
                Capabilities = LuaCapabilities.All,
                OneOffCapabilities = LuaCapabilities.All,
                // WHY: wire the Rbx surface so the test proves it SURVIVES the flag — the whole point is that
                // dropping the coreai_world_* build bindings must not take Instance.new down with them.
                RbxApi = new LuaCsRbxApiBindings(),
                RegisterWorldEditBuildBindings = false
            });

            stack.Runtime.LoadMod("m", @"
                store_set('spawn', (coreai_world_spawn ~= nil) and 'yes' or 'no')
                store_set('change', (coreai_world_change ~= nil) and 'yes' or 'no')
                store_set('set_color', (coreai_world_set_color ~= nil) and 'yes' or 'no')
                store_set('destroy', (coreai_world_destroy ~= nil) and 'yes' or 'no')
                store_set('find', (coreai_world_find ~= nil) and 'yes' or 'no')
                store_set('pos', (coreai_world_pos ~= nil) and 'yes' or 'no')
                store_set('exists', (coreai_world_exists ~= nil) and 'yes' or 'no')
                store_set('rbx', (Instance ~= nil) and 'yes' or 'no')",
                LuaCapabilities.All);

            // WHY: withheld build APIs resolve to actionable stubs (calling one throws a capability
            // error — proven in LuaCsWithheldApiStubEditModeTests), so presence probes see a function.
            Assert.AreEqual("yes", store.Get("m", "spawn"), "coreai_world_spawn must resolve to an actionable stub.");
            Assert.AreEqual("yes", store.Get("m", "change"), "coreai_world_change must resolve to an actionable stub.");
            Assert.AreEqual("yes", store.Get("m", "set_color"),
                "coreai_world_set_color must resolve to an actionable stub.");
            Assert.AreEqual("yes", store.Get("m", "destroy"),
                "coreai_world_destroy must resolve to an actionable stub.");
            Assert.AreEqual("yes", store.Get("m", "find"), "Read-only coreai_world_find must remain.");
            Assert.AreEqual("yes", store.Get("m", "pos"), "Read-only coreai_world_pos must remain.");
            Assert.AreEqual("yes", store.Get("m", "exists"), "Read-only coreai_world_exists must remain.");
            Assert.AreEqual("yes", store.Get("m", "rbx"),
                "Rbx Instance surface must remain — WorldEdit is still granted, only the build bindings are gone.");
            Assert.AreEqual(0, sink.Commands.Count, "Presence probing must not route any world command.");
        }

        [Test]
        public void LuaCs_NestedModsCall_WorldTransaction_DoesNotCorruptCallerBuffer()
        {
            MemoryStore store = new();
            FakeCommandSink sink = new();
            LuaCsModStack stack = BuildStack(store, sink);

            // B opens and commits its OWN world transaction while A's is still open. Before the per-run
            // transaction frames, B ran against the SAME shared buffer/flag as A: B's begin cleared A's
            // buffered 'a1' and its commit reset A's active flag, so A's commit below threw
            // "no active transaction" (or silently lost 'a1'). Each run must now own an isolated frame.
            stack.Runtime.LoadMod("b", @"
                mods_export('work', function()
                    coreai_world_begin()
                    coreai_world_destroy('b1')
                    coreai_world_destroy('b2')
                    return coreai_world_commit()
                end)", LuaCapabilities.WorldEdit);

            stack.Runtime.LoadMod("a", @"
                hooks_on('go', function()
                    coreai_world_begin()
                    coreai_world_destroy('a1')
                    local nb = mods_call('b', 'work')
                    local na = coreai_world_commit()
                    store_set('nb', tostring(nb))
                    store_set('na', tostring(na))
                end)", LuaCapabilities.WorldEdit);

            stack.Runtime.EmitEvent("go", "");
            stack.Runtime.Tick(0);

            Assert.IsEmpty(stack.Runtime.GetRecentHandlerErrors("a"),
                "A's commit must not throw: its transaction survives B's nested begin/commit.");
            Assert.AreEqual("2", store.Get("a", "nb"), "B's nested commit flushes its own 2 buffered commands.");
            Assert.AreEqual("1", store.Get("a", "na"),
                "A's commit flushes ONLY its own single buffered command, proving isolation.");
            Assert.AreEqual(3, sink.Commands.Count,
                "b1 + b2 (nested commit) then a1 (outer commit) all reach the sink exactly once.");
            StringAssert.Contains("a1", sink.Commands[2].JsonPayload,
                "The outer transaction's buffered command commits last and is not lost or merged into B's.");
        }

        [Test]
        [Timeout(30000)]
        public void LuaCs_RunawayHandler_IsCutAndSurvivesOneTrip()
        {
            MemoryStore store = new();
            LuaCsModStack stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = new FakeGameLogger(),
                ModStore = store,
                Capabilities = LuaCapabilities.All,
                OneOffCapabilities = LuaCapabilities.All,
                // Tight per-call budgets so the runaway loop is cut quickly (keeps the test fast).
                HandlerTimeoutMs = 100,
                HandlerMaxSteps = 5000
            });

            // A runaway handler must be CUT by the step/time budget before it completes — it never reaches
            // store_set. One cut must not quarantine a mod (a single failure is below MaxErrorsBeforeQuarantine),
            // and the failure is surfaced for observability. (A loop, not an allocation bomb: the step/time
            // budgets are the reliable guards, and a huge concat is a non-interruptible single opcode that
            // risks OOM.)
            stack.Runtime.LoadMod("m", @"
                hooks_on('bomb', function()
                    while true do local x = 1 end
                    store_set('reached', 'yes')
                end)");

            stack.Runtime.EmitEvent("bomb", "");
            stack.Runtime.Tick(0);

            Assert.IsTrue(stack.Runtime.IsLoaded("m"),
                "A single cut run must not quarantine the mod (one failure < MaxErrorsBeforeQuarantine).");
            Assert.IsFalse(stack.Runtime.ListMods()[0].Quarantined,
                "A single failure must leave the mod un-quarantined.");
            Assert.AreEqual("", store.Get("m", "reached"),
                "The runaway run must be cut before completing (the guard stays real).");

            IReadOnlyList<LuaModHandlerError> errors = stack.Runtime.GetRecentHandlerErrors("m");
            Assert.IsNotEmpty(errors, "The cut run is surfaced as a handler error for observability.");
        }

        [Test]
        [Timeout(30000)]
        public void LuaCs_RunawayHandlerAfterAnEarlierTrip_IsStillCut_AndTheStreakQuarantines()
        {
            // WHY (security, W6-A): every handler of a mod runs on the mod's one LuaState, and the guard's
            // hook used to throw to trip, which left Lua-CSharp's in-hook flag set on that state. After the
            // first trip no later handler of the mod was guarded at all: a runaway handler hung the host
            // (a frozen page on WebGL). The loop below is finite so the regression fails instead of hanging.
            MemoryStore store = new();
            LuaCsModStack stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = new FakeGameLogger(),
                ModStore = store,
                Capabilities = LuaCapabilities.All,
                OneOffCapabilities = LuaCapabilities.All,
                HandlerTimeoutMs = 150,
                HandlerMaxSteps = 20_000,
                MaxErrorsBeforeQuarantine = 2
            });
            stack.Runtime.LoadMod("m", @"
                hooks_on('first', function()
                    pcall(function() while true do end end)
                    store_set('first', 'survived')
                end)
                hooks_on('later', function()
                    local n = 0
                    for i = 1, 50000000 do n = n + 1 end
                    store_set('later', 'finished')
                end)");
            string quarantinedId = null;
            stack.Runtime.ModQuarantined += (id, count) => quarantinedId = id;

            stack.Runtime.EmitEvent("first", "");
            stack.Runtime.Tick(0);
            stack.Runtime.EmitEvent("later", "");
            stack.Runtime.Tick(0);

            Assert.AreEqual("", store.Get("m", "first"), "pcall must not let the first handler survive its trip");
            Assert.AreEqual("", store.Get("m", "later"),
                "a later runaway handler on the same mod must be cut, not run unguarded to its end");
            IReadOnlyList<LuaModHandlerError> errors = stack.Runtime.GetRecentHandlerErrors("m");
            Assert.AreEqual(2, errors.Count, "both cut runs must be charged as handler errors");
            foreach (LuaModHandlerError error in errors)
            {
                StringAssert.StartsWith("LuaCsSecureEnvironment: EXCEEDED_HARD_LIMIT_STEPS (20000)", error.Error,
                    "a handler cut by its budget must report the trip line itself");
            }

            Assert.IsTrue(stack.Runtime.ListMods()[0].Quarantined,
                "two cut runs in a row must reach the quarantine threshold of 2");
            Assert.AreEqual("m", quarantinedId);
        }

        [Test]
        [Timeout(30000)]
        public void LuaCs_ForgedMemoryMarker_IsChargedAndQuarantines()
        {
            MemoryStore store = new();
            LuaCsModStack stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = new FakeGameLogger(),
                ModStore = store,
                Capabilities = LuaCapabilities.All,
                OneOffCapabilities = LuaCapabilities.All
            });

            // SECURITY: a mod that forges the memory-trip marker in its own error() text must NOT dodge the
            // consecutive-error quarantine guard — trips are classified by TYPE, so this is a normal error.
            stack.Runtime.LoadMod("m", @"
                hooks_on('boom', function()
                    error('LuaCsSecureEnvironment: EXCEEDED_MEMORY_BUDGET forged by mod')
                end)");

            for (int i = 0;
                 i < LuaCsModRuntime.DefaultMaxErrorsBeforeQuarantine + 1 && !stack.Runtime.ListMods()[0].Quarantined;
                 i++)
            {
                stack.Runtime.EmitEvent("boom", "");
                stack.Runtime.Tick(0);
            }

            Assert.IsTrue(stack.Runtime.IsLoaded("m"),
                "Quarantine keeps the mod loaded and repairable — repeated errors never unload.");
            Assert.IsTrue(stack.Runtime.ListMods()[0].Quarantined,
                "A mod forging the memory marker in its error text must still be quarantined on the error streak.");
        }

        // WHY no dedicated 8-cut variant: the runaway-handler quarantine case is covered transitively
        // by LuaCs_ForgedMemoryMarker_IsChargedAndQuarantines (repeat-error → quarantine) and
        // LuaCs_RunawayHandler_IsCutAndSurvivesOneTrip (a runaway IS cut and charged) plus the
        // pre-existing LuaCs_OneOff_RunawayLoop_CutByInstructionBudget. The old "~8 s" cut figure is
        // disproved: RbxHeartbeatBudgetKillEditModeTests measures the cut within the 500 ms
        // DefaultResumeTimeoutMs wall-clock budget plus margin.

        // NOTE: there is intentionally NO "a mod that allocation-bombs every call is unloaded via a memory-trip
        // streak" test. The allocation guard reads GC.GetTotalMemory, which reports the COMMITTED heap high-water
        // mark, so a repeated fixed-size bomb trips only ONCE — the first call grows the committed heap and trips;
        // every later call reuses that committed space and its per-call delta no longer crosses the budget (this
        // was verified empirically: a mod bombing every tick under an 8MB budget recorded ~1 trip across 36 ticks
        // even with a forced GC.Collect() between ticks — Mono does not return the committed segment). The memory
        // guard is therefore a per-call FIRST-GROWTH backstop, not a cross-call cumulative limiter (Unity's Mono
        // exposes no per-call/per-thread allocation counter to build one). A mod that keeps allocating within the
        // committed envelope is bounded by the per-call step/time budgets, not by unloading. The single memory
        // trip IS charged to the ordinary error streak and forgiven by the next success — see the test below.

        [Test]
        [Timeout(30000)]
        public void LuaCs_SingleMemoryTrip_ChargedButForgivenByNextSuccess_DoesNotUnload()
        {
            MemoryStore store = new();
            LuaCsModStack stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = new FakeGameLogger(),
                ModStore = store,
                Capabilities = LuaCapabilities.All,
                OneOffCapabilities = LuaCapabilities.All,
                HandlerMaxAllocatedBytes = 8 * 1024 * 1024
            });

            // A memory trip is charged to the ordinary consecutive-error streak (a success resets it), so a mod
            // that trips once — on its own first oversized allocation or on unrelated shared-heap growth — and
            // then runs cleanly is forgiven and never quarantined. This mod bombs on its FIRST invocation only and
            // succeeds on every later call, so it must stay dispatching well past MaxErrorsBeforeQuarantine ticks.
            stack.Runtime.LoadMod("occasional", @"
                local n = 0
                hooks_on('poke', function()
                    n = n + 1
                    if n == 1 then
                        local s = string.rep('x', 1000000)
                        for i = 1, 6 do s = s .. s end
                    end
                end)");

            for (int i = 0; i < LuaCsModRuntime.DefaultMaxErrorsBeforeQuarantine * 2 + 2; i++)
            {
                stack.Runtime.EmitEvent("poke", "");
                stack.Runtime.Tick(0);
            }

            Assert.IsTrue(stack.Runtime.IsLoaded("occasional"),
                "A single memory trip followed by successful calls must be forgiven (streak reset) and keep the mod loaded.");
            Assert.IsFalse(stack.Runtime.ListMods()[0].Quarantined,
                "A forgiven streak must never quarantine the mod.");
        }

        [Test]
        public void LuaCs_AdditionalGameplayBindings_ReachLoadedMods()
        {
            MemoryStore store = new();
            LuaCsModStack stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = new FakeGameLogger(),
                ModStore = store,
                Capabilities = LuaCapabilities.All,
                OneOffCapabilities = LuaCapabilities.All,
                // A host/per-scene binding injected alongside the built-in surface (the seam a demo uses to add
                // e.g. forge_define). It must reach a persistently-loaded mod's handler.
                AdditionalGameplayBindings = (registry, caps) =>
                    registry.Register("extra_double", new Func<double, double>(x => x * 2))
            });

            stack.Runtime.LoadMod("m", @"
                hooks_on('go', function()
                    store_set('r', tostring(extra_double(21)))
                end)");

            stack.Runtime.EmitEvent("go", "");
            stack.Runtime.Tick(0);

            StringAssert.StartsWith("42", store.Get("m", "r"),
                "An injected AdditionalGameplayBindings API must be callable from a loaded mod's handler.");
        }

        private const string PrivilegedProbeApi = "host_privileged_probe";

        /// <summary>
        /// Stack whose host extension records every capability set it is handed and, like a real host
        /// integration, registers a privileged API only when that set carries the Full bit.
        /// </summary>
        private static LuaCsModStack BuildCapabilityProbeStack(
            LuaCapabilities ceiling,
            LuaCapabilities oneOffCeiling,
            List<LuaCapabilities> seen,
            ILuaModStore store = null,
            LuaCsRbxApiBindings rbxApi = null)
        {
            return LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = new FakeGameLogger(),
                ModStore = store,
                Capabilities = ceiling,
                OneOffCapabilities = oneOffCeiling,
                RbxApi = rbxApi,
                AdditionalGameplayBindings = (registry, caps) =>
                {
                    lock (seen)
                    {
                        seen.Add(caps);
                    }

                    if ((caps & LuaCapabilities.Full) != 0)
                    {
                        registry.Register(PrivilegedProbeApi, new Action(() => { }));
                    }
                }
            });
        }

        private static void AssertExtensionSawOnly(List<LuaCapabilities> seen, LuaCapabilities expected,
            string because)
        {
            lock (seen)
            {
                Assert.IsNotEmpty(seen, "The AdditionalGameplayBindings callback never ran.");
                foreach (LuaCapabilities caps in seen)
                {
                    Assert.AreEqual(expected, caps, because);
                }
            }
        }

        private const string PrivilegedProbeModSource =
            "store_set('privileged', (" + PrivilegedProbeApi + " ~= nil) and 'yes' or 'no')";

        [Test]
        public void LuaCs_AdditionalGameplayBindings_FullRequestAboveCeiling_ExtensionSeesNoFull()
        {
            MemoryStore store = new();
            List<LuaCapabilities> seen = new();
            LuaCsModStack stack = BuildCapabilityProbeStack(
                LuaCapabilities.All, LuaCapabilities.All, seen, store);

            stack.Runtime.LoadMod("asks-full", PrivilegedProbeModSource,
                LuaCapabilities.All | LuaCapabilities.Full, persistToStore: false);

            AssertExtensionSawOnly(seen, LuaCapabilities.All,
                "A mod that merely REQUESTS Full under a ceiling without Full must hand the host extension "
                + "the ceiling-trimmed tiers, exactly what the built-in surface registered.");
            Assert.AreEqual("no", store.Get("asks-full", "privileged"),
                "A host API gated on the Full bit must stay out of reach when the host ceiling withholds Full.");
        }

        [Test]
        public void LuaCs_AdditionalGameplayBindings_FullWithinCeiling_ExtensionSeesFullAndOnlyTheCeiling()
        {
            MemoryStore store = new();
            List<LuaCapabilities> seen = new();
            LuaCapabilities ceiling =
                LuaCapabilities.Read | LuaCapabilities.WorldEdit | LuaCapabilities.Full;
            LuaCsModStack stack = BuildCapabilityProbeStack(ceiling, LuaCapabilities.All, seen, store);

            stack.Runtime.LoadMod("granted-full", PrivilegedProbeModSource,
                LuaCapabilities.All | LuaCapabilities.Full, persistToStore: false);

            AssertExtensionSawOnly(seen, ceiling,
                "The extension must see Full when the host grants it, and nothing the ceiling withholds.");
            Assert.AreEqual(ceiling,
                stack.GameplayBindings.EffectiveCapabilities(LuaCapabilities.All | LuaCapabilities.Full),
                "The extension's tiers must equal what the built-in surface registered.");
            Assert.AreEqual("yes", store.Get("granted-full", "privileged"),
                "A host API gated on the Full bit must stay reachable when the host grants Full.");
        }

        [Test]
        public void LuaCs_AdditionalGameplayBindings_ReadOnlyMod_ExtensionSeesNoGameplayOrFull()
        {
            MemoryStore store = new();
            List<LuaCapabilities> seen = new();
            LuaCsModStack stack = BuildCapabilityProbeStack(
                LuaCapabilities.All | LuaCapabilities.Full, LuaCapabilities.All, seen, store);

            stack.Runtime.LoadMod("reader", PrivilegedProbeModSource, LuaCapabilities.Read,
                persistToStore: false);

            AssertExtensionSawOnly(seen, LuaCapabilities.Read,
                "A Read-only mod must hand the extension Read alone, never the wider host ceiling.");
            Assert.AreEqual("no", store.Get("reader", "privileged"));
        }

        [TestCase(LuaCapabilities.All, LuaCapabilities.All | LuaCapabilities.Full,
            LuaCapabilities.All)]
        [TestCase(LuaCapabilities.All | LuaCapabilities.Full, LuaCapabilities.Read | LuaCapabilities.Gameplay,
            LuaCapabilities.Read | LuaCapabilities.Gameplay)]
        [TestCase(LuaCapabilities.Read | LuaCapabilities.WorldEdit | LuaCapabilities.Full,
            LuaCapabilities.Read | LuaCapabilities.Gameplay | LuaCapabilities.Full,
            LuaCapabilities.Read | LuaCapabilities.Full)]
        public void LuaCs_AdditionalGameplayBindings_OneOffExecutor_ExtensionSeesCeilingAndOneOffTier(
            LuaCapabilities ceiling, LuaCapabilities oneOffCeiling, LuaCapabilities expected)
        {
            List<LuaCapabilities> seen = new();
            LuaCsModStack stack = BuildCapabilityProbeStack(ceiling, oneOffCeiling, seen);

            LuaTool.LuaResult result = stack.ToolExecutor
                .ExecuteAsync("return 1", CancellationToken.None)
                .GetAwaiter().GetResult();

            Assert.IsTrue(result.Success, result.Error);
            AssertExtensionSawOnly(seen, expected,
                "The one-off extension must see the host ceiling AND the one-off tier applied, "
                + "exactly like the built-in one-off surface.");
        }

        [Test]
        public void LuaCs_AdditionalGameplayBindings_OneOffMutationEnvelope_ExtensionSeesNoFullAboveCeiling()
        {
            InstanceRegistry registry = new(
                worldAclVersion: InstanceRegistry.CurrentWorldAclVersion);
            LuaCsRbxApiBindings rbxApi = new(registry: registry);
            List<LuaCapabilities> seen = new();
            LuaCsModStack stack = BuildCapabilityProbeStack(
                LuaCapabilities.All, LuaCapabilities.All | LuaCapabilities.Full, seen, rbxApi: rbxApi);
            RbxInstance target = CreateMutationTarget(registry, "CapabilityProbeTarget");
            ActorContext actor = MutationActor("capability-probe", "session-1");
            MutationEnvelope envelope = new(
                actor.ActorId, target.Id, "capability-probe-operation",
                MutationRecord(registry, target).Revision);

            LuaTool.LuaResult result = ExecuteMutation(stack, actor, envelope,
                "local t=workspace:FindFirstChild('CapabilityProbeTarget'); "
                + "t:SetAttribute('Probe','seen'); "
                + "return (" + PrivilegedProbeApi + " ~= nil) and 'yes' or 'no'");

            Assert.IsTrue(result.Success, result.Error);
            AssertExtensionSawOnly(seen, LuaCapabilities.All,
                "The actor-scoped one-off path must hand the extension the ceiling-trimmed tiers too.");
            Assert.AreEqual("no", result.Output,
                "A one-off tier wider than the host ceiling must not expose a Full-gated host API.");
        }

        [Test]
        [Timeout(30000)]
        public void LuaCs_PcallSwallowedMemoryTrip_DoesNotLaunderLaterRealError()
        {
            MemoryStore store = new();
            LuaCsModStack stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = new FakeGameLogger(),
                ModStore = store,
                Capabilities = LuaCapabilities.All,
                OneOffCapabilities = LuaCapabilities.All,
                HandlerMaxAllocatedBytes = 64 * 1024 * 1024
            });

            // SECURITY: the mod swallows a failure INSIDE pcall, then throws a REAL, unrelated error. That real
            // error must be charged to the normal error streak and quarantine the mod — a swallowed inner failure
            // must never launder a subsequent real error out of the quarantine guard.
            stack.Runtime.LoadMod("m", @"
                hooks_on('evade', function()
                    pcall(function() error('swallowed inner failure') end)
                    error('a real unrelated error')
                end)");

            for (int i = 0;
                 i < LuaCsModRuntime.DefaultMaxErrorsBeforeQuarantine + 1 && !stack.Runtime.ListMods()[0].Quarantined;
                 i++)
            {
                stack.Runtime.EmitEvent("evade", "");
                stack.Runtime.Tick(0);
            }

            Assert.IsTrue(stack.Runtime.ListMods()[0].Quarantined,
                "A real error after a pcall-swallowed memory trip must charge the error streak and quarantine the mod.");
        }

        /// <summary>Stack with a low quarantine threshold so streak tests stay fast.</summary>
        private static LuaCsModStack BuildQuarantineStack(MemoryStore store, int maxErrorsBeforeQuarantine = 2)
        {
            return LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = new FakeGameLogger(),
                ModStore = store,
                Capabilities = LuaCapabilities.All,
                OneOffCapabilities = LuaCapabilities.All,
                MaxErrorsBeforeQuarantine = maxErrorsBeforeQuarantine
            });
        }

        private const string FailingModSource = @"
            hooks_on('boom', function() error('boom') end)
            hooks_on('work', function()
                store_set('n', tostring((tonumber(store_get('n')) or 0) + 1))
            end)
            hooks_every(0.05, function()
                store_set('t', tostring((tonumber(store_get('t')) or 0) + 1))
            end)";

        private const string HealthyModSource = @"
            hooks_on('work', function()
                store_set('n', tostring((tonumber(store_get('n')) or 0) + 1))
            end)";

        [Test]
        public void LuaCs_Quarantine_ModStaysListedAndStopsDispatching()
        {
            MemoryStore store = new();
            LuaCsModStack stack = BuildQuarantineStack(store);
            stack.Runtime.LoadMod("m", FailingModSource);

            string quarantinedId = null;
            int quarantinedStreak = 0;
            stack.Runtime.ModQuarantined += (id, count) =>
            {
                quarantinedId = id;
                quarantinedStreak = count;
            };

            for (int i = 0; i < 2; i++)
            {
                stack.Runtime.EmitEvent("boom", "");
                stack.Runtime.Tick(0);
            }

            Assert.IsTrue(stack.Runtime.IsLoaded("m"), "A quarantined mod must STAY loaded and addressable.");
            LuaModInfo info = stack.Runtime.ListMods()[0];
            Assert.IsTrue(info.Quarantined, "ListMods must surface the quarantine so the repairing agent SEES it.");
            Assert.AreEqual("m", quarantinedId, "ModQuarantined must fire with the mod id.");
            Assert.AreEqual(2, quarantinedStreak, "ModQuarantined must carry the error streak.");
            Assert.IsTrue(stack.Runtime.TryGetModSource("m", out string source) && source.Length > 0,
                "get_source must keep working for a quarantined mod.");

            // Suspended: named-event handlers and timers must both stop running.
            stack.Runtime.EmitEvent("work", "");
            stack.Runtime.Tick(0.06);
            stack.Runtime.Tick(0.06);
            Assert.AreEqual("", store.Get("m", "n"), "A quarantined mod's hooks_on handlers must not dispatch.");
            Assert.AreEqual("", store.Get("m", "t"), "A quarantined mod's hooks_every timers must not fire.");
        }

        [Test]
        public void LuaCs_Quarantine_ReloadClearsQuarantineAndResumesDispatch()
        {
            MemoryStore store = new();
            LuaCsModStack stack = BuildQuarantineStack(store);
            stack.Runtime.LoadMod("m", FailingModSource);

            for (int i = 0; i < 2; i++)
            {
                stack.Runtime.EmitEvent("boom", "");
                stack.Runtime.Tick(0);
            }

            Assert.IsTrue(stack.Runtime.ListMods()[0].Quarantined, "Precondition: the mod is quarantined.");

            // The flagship repair path: an async LLM repair lands as a plain ReloadMod — it must work on a
            // quarantined mod, clear the quarantine + streak, and dispatch must resume.
            Assert.DoesNotThrow(() => stack.Runtime.ReloadMod("m", HealthyModSource),
                "ReloadMod on a quarantined mod must succeed normally.");

            LuaModInfo info = stack.Runtime.ListMods()[0];
            Assert.IsFalse(info.Quarantined, "A successful reload must clear the quarantine.");
            Assert.AreEqual(0, info.ErrorCount, "A successful reload must clear the error streak.");

            stack.Runtime.EmitEvent("work", "");
            stack.Runtime.Tick(0);
            Assert.AreEqual("1", store.Get("m", "n"), "Dispatch must resume after the repairing reload.");
        }

        [Test]
        public void LuaCs_Quarantine_ReloadLandsMidTick_FreshInstanceIsNotQuarantined()
        {
            MemoryStore store = new();
            LuaCsModStack stack = BuildQuarantineStack(store);
            stack.Runtime.LoadMod("m", FailingModSource);

            // Reproduces the stale-snapshot race: Tick iterates a snapshot of mod objects; this subscriber
            // reloads the mod MID-TICK the moment the streak hits the threshold, swapping the registry
            // entry. The quarantine check at the end of the tick then sees the OLD object's streak — it
            // must re-resolve the live entry and skip, never suspending the freshly repaired instance.
            bool repaired = false;
            stack.Runtime.ModHandlerErrored += (id, error, count) =>
            {
                if (!repaired && count >= 2)
                {
                    repaired = true;
                    stack.Runtime.ReloadMod("m", HealthyModSource);
                }
            };

            for (int i = 0; i < 2; i++)
            {
                stack.Runtime.EmitEvent("boom", "");
                stack.Runtime.Tick(0);
            }

            Assert.IsTrue(repaired, "Precondition: the mid-tick repair ran.");
            Assert.IsTrue(stack.Runtime.IsLoaded("m"));
            Assert.IsFalse(stack.Runtime.ListMods()[0].Quarantined,
                "The stale snapshot's error streak must not quarantine the freshly reloaded instance.");

            stack.Runtime.EmitEvent("work", "");
            stack.Runtime.Tick(0);
            Assert.AreEqual("1", store.Get("m", "n"), "The repaired instance must dispatch normally.");
        }

        [Test]
        public void LuaCs_Quarantine_CrossModExportSuspended_HealthyCallerNotEscalated()
        {
            MemoryStore store = new();
            LuaCsModStack stack = BuildQuarantineStack(store);

            // provider exports a callable AND has a failing handler that drives it into quarantine.
            stack.Runtime.LoadMod("provider", @"
                mods_export('scale', function(x) return (tonumber(x) or 0) * 2 end)
                hooks_on('boom', function() error('boom') end)");

            // consumer is HEALTHY: it pcall-wraps its cross-mod call so a suspended target is handled,
            // not propagated into its own streak.
            stack.Runtime.LoadMod("consumer", @"
                hooks_on('use', function()
                    local ok, err = pcall(function() return mods_call('provider', 'scale', 10) end)
                    store_set('ok', ok and 'yes' or 'no')
                    store_set('err', tostring(err))
                end)");

            for (int i = 0; i < 2; i++)
            {
                stack.Runtime.EmitEvent("boom", "");
                stack.Runtime.Tick(0);
            }

            LuaModInfo provider = stack.Runtime.ListMods().Single(m => m.Id == "provider");
            Assert.IsTrue(provider.Quarantined, "Precondition: the provider mod is quarantined.");

            // The quarantined export must be suspended: repeated cross-mod calls all fail with the
            // quarantine error, and must never escalate the healthy caller's streak into quarantine.
            for (int i = 0; i < LuaCsModRuntime.DefaultMaxErrorsBeforeQuarantine * 2 + 2; i++)
            {
                stack.Runtime.EmitEvent("use", "");
                stack.Runtime.Tick(0);
            }

            Assert.AreEqual("no", store.Get("consumer", "ok"),
                "A quarantined mod's exports must NOT be callable via mods_call.");
            StringAssert.Contains("quarantined", store.Get("consumer", "err"),
                "The failure must be attributable to the quarantined target, naming the quarantine.");
            StringAssert.Contains("provider", store.Get("consumer", "err"),
                "The quarantine error must name the target mod.");

            LuaModInfo consumer = stack.Runtime.ListMods().Single(m => m.Id == "consumer");
            Assert.IsFalse(consumer.Quarantined,
                "Repeated calls into a quarantined mod must not quarantine the healthy caller.");
            Assert.AreEqual(0, consumer.ErrorCount,
                "The quarantined-target failure must not be mis-charged to the caller's error streak.");
        }

        [Test]
        public void LuaCs_LogicSlots_OverrideClearedOnUnload()
        {
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(store);
            LuaCsLogicSlots slots = stack.GameplayBindings.LogicSlots;
            slots.DeclareSlot("dmg");

            List<(string ModId, LuaModTeardownReason Reason)> teardowns = new();
            stack.Runtime.ModTearingDown += (id, reason) => teardowns.Add((id, reason));

            stack.Runtime.LoadMod("m", "logic_define('dmg', function(x) return x * 2 end)");
            Assert.IsTrue(slots.TryInvokeNumber("dmg", out double value, 21), "The mod's override is installed.");
            Assert.AreEqual(42d, value);

            stack.Runtime.UnloadMod("m");
            Assert.IsFalse(slots.IsOverridden("dmg"),
                "Unload must clear the mod's logic-slot override — the dead mod's formula is never invoked again.");
            Assert.IsFalse(slots.TryInvokeNumber("dmg", out _, 21), "The call falls back to the C# default.");
            CollectionAssert.Contains(teardowns, ("m", LuaModTeardownReason.Unload),
                "ModTearingDown must fire for the unload so future subsystems can hook the same point.");
        }

        [Test]
        public void LuaCs_LogicSlots_RbxSchedulerLoadKeepsOwnerStateAfterStartupCoroutineEnds()
        {
            LuaCsRbxApiBindings rbxApi = new();
            LuaCsModStack stack = BuildMutationStack(rbxApi);
            LuaCsLogicSlots slots = stack.GameplayBindings.LogicSlots;
            slots.DeclareSlot("scheduler-formula");

            stack.Runtime.LoadMod(
                "scheduler-owner",
                "logic_define('scheduler-formula', function(value) return value * 2 end)");

            Assert.IsTrue(slots.TryInvokeNumber(
                "scheduler-formula", out double value, 21d), slots.LastError);
            Assert.AreEqual(42d, value);
        }

        [Test]
        public void LuaCs_LogicSlots_ReloadDropsOldFormula_AndKeepsTheReplacementsOwn()
        {
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(store);
            LuaCsLogicSlots slots = stack.GameplayBindings.LogicSlots;
            slots.DeclareSlot("dmg");
            slots.DeclareSlot("loot");

            stack.Runtime.LoadMod("m", @"
                logic_define('dmg', function(x) return x * 2 end)
                logic_define('loot', function() return 10 end)");

            // v2 re-defines dmg with a NEW formula and drops loot entirely. After the reload the old
            // instance's formulas must be gone: dmg answers with the new math, loot reverts to vanilla.
            stack.Runtime.ReloadMod("m", "logic_define('dmg', function(x) return x * 3 end)");

            Assert.IsTrue(slots.TryInvokeNumber("dmg", out double value, 10),
                "The replacement's own logic_define (made during its load chunk) must survive the teardown.");
            Assert.AreEqual(30d, value, "The NEW formula answers — the old mod version's formula is dead.");
            Assert.IsFalse(slots.IsOverridden("loot"),
                "A slot the new version no longer defines must revert to vanilla, not keep the stale formula.");
        }

        [Test]
        public void LuaCs_LogicSlots_QuarantineRevertsOverridesToVanilla()
        {
            MemoryStore store = new();
            LuaCsModStack stack = BuildQuarantineStack(store);
            LuaCsLogicSlots slots = stack.GameplayBindings.LogicSlots;
            slots.DeclareSlot("dmg");

            stack.Runtime.LoadMod("m",
                "logic_define('dmg', function(x) return x * 2 end)\n" + FailingModSource);
            Assert.IsTrue(slots.IsOverridden("dmg"), "Precondition: the override is installed.");

            for (int i = 0; i < 2; i++)
            {
                stack.Runtime.EmitEvent("boom", "");
                stack.Runtime.Tick(0);
            }

            Assert.IsTrue(stack.Runtime.ListMods()[0].Quarantined, "Precondition: the mod is quarantined.");
            Assert.IsFalse(slots.IsOverridden("dmg"),
                "Quarantine must clear the broken mod's overrides — its formula must stop being invoked.");
        }

        [Test]
        public void LuaCs_LogicSlots_OverrideFailure_AttributedToOwningModInDiagnostics()
        {
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(store);
            LuaCsLogicSlots slots = stack.GameplayBindings.LogicSlots;
            slots.DeclareSlot("dmg");

            stack.Runtime.LoadMod("m", "logic_define('dmg', function() error('formula broke') end)");

            Assert.IsFalse(slots.TryInvokeNumber("dmg", out _, 1),
                "The failing override fails open: the call reports 'not overridden'.");
            Assert.IsFalse(slots.IsOverridden("dmg"), "The failing override is reset to vanilla.");

            // The old behavior was a SILENT revert; the failure must now land in the mod's own error
            // channel with the slot named, so diagnostics/get_mod_logs show which mod's formula broke.
            IReadOnlyList<LuaModHandlerError> errors = stack.Runtime.GetRecentHandlerErrors("m");
            Assert.IsNotEmpty(errors, "The override failure must be recorded against the owning mod.");
            StringAssert.Contains("dmg", errors[0].Error, "The recorded error must name the slot.");
            Assert.AreEqual(1, stack.Runtime.ListMods()[0].ErrorCount,
                "The override failure charges the owning mod's error streak.");
        }

        [Test]
        public void LuaCs_LogicSlots_InvocationScope_BracketsEachFormulaCall_AndClosesAfterTheFailOpenReset()
        {
            // WHY: the runtime counts a formula as running mod code between the two calls, so the bracket
            // must open only when a formula actually runs and close on every exit, a failure included,
            // after the failure has been charged to the mod that is still loaded.
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(store);
            LuaCsLogicSlots slots = stack.GameplayBindings.LogicSlots;
            slots.DeclareSlot("dmg");
            slots.DeclareSlot("broken");
            slots.DeclareSlot("vanilla");
            stack.Runtime.LoadMod("m", @"
                logic_define('dmg', function(x) return x * 2 end)
                logic_define('broken', function() error('formula broke') end)");
            List<string> events = new();
            slots.SetInvocationScope(() => events.Add("start"), () => events.Add("finish"));
            slots.OverrideFailed += (modId, slot, error) => events.Add("failed:" + slot);

            Assert.IsTrue(slots.TryInvokeNumber("dmg", out double value, 21));
            Assert.AreEqual(42d, value);
            CollectionAssert.AreEqual(new[] { "start", "finish" }, events);

            events.Clear();
            Assert.IsFalse(slots.TryInvokeNumber("vanilla", out _, 1));
            CollectionAssert.IsEmpty(events, "no formula ran, so no bracket opened");

            Assert.IsFalse(slots.TryInvokeNumber("broken", out _, 1));
            CollectionAssert.AreEqual(new[] { "start", "failed:broken", "finish" }, events,
                "the bracket closes after the fail-open reset has reported the failure");

            events.Clear();
            slots.SetInvocationScope(null, null);
            Assert.IsTrue(slots.TryInvokeNumber("dmg", out _, 1));
            CollectionAssert.IsEmpty(events, "a detached scope is never called");
        }

        /// <summary>Collects the runtime's error lines so host-fault reporting can be counted.</summary>
        private sealed class RecordingLog : CoreAI.Logging.ILog
        {
            public readonly List<string> Errors = new();

            public void Debug(string message, string tag = null)
            {
            }

            public void Info(string message, string tag = null)
            {
            }

            public void Warn(string message, string tag = null)
            {
            }

            public void Error(string message, string tag = null)
            {
                Errors.Add(message);
            }
        }

        /// <summary>
        /// Builds a Roblox-API stack wired to the installer's ModTearingDown cleanup, so a quarantined
        /// mod's scheduler threads and connections stop exactly as they do in production.
        /// </summary>
        private static LuaCsModStack BuildSchedulerStack(LuaCsRbxApiBindings bindings,
            MemoryStore store, int maxErrorsBeforeQuarantine, CoreAI.Logging.ILog log = null)
        {
            LuaCsModStack stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = new FakeGameLogger(),
                ModStore = store,
                Capabilities = LuaCapabilities.All,
                OneOffCapabilities = LuaCapabilities.All,
                RbxApi = bindings,
                MaxErrorsBeforeQuarantine = maxErrorsBeforeQuarantine,
                Log = log
            });
            stack.Runtime.ModTearingDown += (modId, reason) =>
            {
                if (reason == LuaModTeardownReason.Reload)
                {
                    bindings.KillOutgoingScheduledGenerations(modId);
                }
                else
                {
                    bindings.KillAllScheduledOwnedBy(modId);
                }

                bindings.Connections.DisconnectOwnedBy(modId, reason == LuaModTeardownReason.Reload);
            };
            return stack;
        }

        /// <summary>One production frame: the scheduler's Advance, then the runtime's Tick.</summary>
        private static void PumpSchedulerFrame(LuaCsModStack stack, LuaCsRbxApiBindings bindings)
        {
            Assert.DoesNotThrow(() => bindings.Scheduler.Advance(1d / 60d),
                "a mod's fault must never escape the frame");
            stack.Runtime.Tick(1d / 60d);
        }

        private static LuaModInfo ModInfo(LuaCsModStack stack, string modId)
        {
            return stack.Runtime.ListMods().Single(mod => mod.Id == modId);
        }

        private static string InvariantText(int value)
        {
            return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private const string CascadingModSource = @"
            local RunService = game:GetService('RunService')
            workspace.ChildAdded:Connect(function(child)
                if child.Name == 'Loop' then
                    local folder = Instance.new('Folder')
                    folder.Name = 'Loop'
                    folder.Parent = workspace
                end
            end)
            RunService.Stepped:Connect(function()
                local seed = Instance.new('Folder')
                seed.Name = 'Loop'
                seed.Parent = workspace
            end)";

        private const string HealthySchedulerModSource = @"
            local RunService = game:GetService('RunService')
            local beats = 0
            RunService.Heartbeat:Connect(function()
                beats = beats + 1
                store_set('heartbeats', tostring(beats))
            end)
            task.spawn(function()
                local waits = 0
                while true do
                    task.wait()
                    waits = waits + 1
                    store_set('waits', tostring(waits))
                end
            end)";

        [Test]
        public void LuaCs_M2_02_CascadingModIsQuarantinedAfterKFaultingFrames_WhileAnotherModKeepsRunning()
        {
            const int threshold = 3;
            const int framesAfterQuarantine = 3;
            LuaCsRbxApiBindings bindings = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildSchedulerStack(bindings, store, threshold);
            List<(string ModId, int Streak)> quarantines = new();
            stack.Runtime.ModQuarantined += (modId, streak) => quarantines.Add((modId, streak));
            stack.Runtime.LoadMod("cascading", CascadingModSource);
            stack.Runtime.LoadMod("healthy", HealthySchedulerModSource);

            // WHY this mod: its Stepped handler and the ChildAdded handlers of the chain resume cleanly
            // in the same frame, before the chain is cut, so a streak that any clean resume resets would
            // never reach the threshold.
            for (int frame = 1; frame <= threshold; frame++)
            {
                PumpSchedulerFrame(stack, bindings);
                Assert.AreEqual(frame == threshold, ModInfo(stack, "cascading").Quarantined,
                    "the cascading mod is quarantined exactly at faulting frame " + threshold
                    + ", checked after frame " + frame);
            }

            CollectionAssert.AreEqual(new[] { ("cascading", threshold) }, quarantines);
            IReadOnlyList<LuaModHandlerError> errors = stack.Runtime.GetRecentHandlerErrors("cascading");
            Assert.AreEqual(threshold, errors.Count,
                "one SIGNAL_CASCADE per frame: " + string.Join(" || ", errors.Select(error => error.Error)));
            for (int index = 0; index < errors.Count; index++)
            {
                StringAssert.Contains("SIGNAL_CASCADE", errors[index].Error);
                Assert.AreEqual(index + 1, errors[index].ConsecutiveCount,
                    "each faulting frame lengthens the streak by one");
            }

            for (int frame = 0; frame < framesAfterQuarantine; frame++)
            {
                PumpSchedulerFrame(stack, bindings);
            }

            Assert.AreEqual(threshold, stack.Runtime.GetRecentHandlerErrors("cascading").Count,
                "the quarantined mod's connections are gone, so its cascade stops");
            string totalFrames = InvariantText(threshold + framesAfterQuarantine);
            Assert.AreEqual(totalFrames, store.Get("healthy", "heartbeats"),
                "the healthy mod's Heartbeat ran every frame, before and after the quarantine");
            Assert.AreEqual(totalFrames, store.Get("healthy", "waits"),
                "the healthy mod's task.wait loop resumed every frame, before and after the quarantine");
            Assert.IsFalse(ModInfo(stack, "healthy").Quarantined);
            Assert.IsEmpty(stack.Runtime.GetRecentHandlerErrors("healthy"));
        }

        [Test]
        public void LuaCs_M2_08_SchedulerHandlerErroringOnAlternateFires_IsNeverQuarantined()
        {
            const int frames = 20;
            LuaCsRbxApiBindings bindings = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildSchedulerStack(bindings, store, 2);
            stack.Runtime.LoadMod("flaky", @"
                local beats = 0
                game:GetService('RunService').Heartbeat:Connect(function()
                    beats = beats + 1
                    store_set('beats', tostring(beats))
                    if beats % 2 == 1 then
                        error('odd beat')
                    end
                end)");

            for (int frame = 1; frame <= frames; frame++)
            {
                PumpSchedulerFrame(stack, bindings);
                Assert.IsFalse(ModInfo(stack, "flaky").Quarantined,
                    "a clean frame between two faulting frames resets the streak; quarantined after frame "
                    + frame);
            }

            Assert.AreEqual(InvariantText(frames), store.Get("flaky", "beats"),
                "the handler kept firing every frame");
            IReadOnlyList<LuaModHandlerError> errors = stack.Runtime.GetRecentHandlerErrors("flaky");
            Assert.AreEqual(frames / 2, errors.Count);
            Assert.IsTrue(errors.All(error => error.ConsecutiveCount == 1),
                "every fault starts a fresh streak: "
                + string.Join(", ", errors.Select(error => error.ConsecutiveCount)));
            Assert.AreEqual(0, ModInfo(stack, "flaky").ErrorCount,
                "the last frame ran cleanly, so the streak is zero");
        }

        [Test]
        public void LuaCs_M2_08_SchedulerHandlerErroringKFiresInARow_IsQuarantinedAtTheNextTick_AndReloadClearsIt()
        {
            const int threshold = 3;
            LuaCsRbxApiBindings bindings = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildSchedulerStack(bindings, store, threshold);
            stack.Runtime.LoadMod("poked", @"
                workspace.ChildAdded:Connect(function(child)
                    if child.Name == 'Poke' then
                        error('poke failed')
                    end
                end)");

            // WHY idle frames between the pokes: a frame in which the mod ran nothing is no success, so
            // K faults in a row quarantine however far apart they land.
            for (int poke = 1; poke <= threshold; poke++)
            {
                RbxInstance folder = bindings.Registry.Create("Folder");
                folder.Name = "Poke";
                folder.Parent = bindings.Registry.WorldRoot;
                Assert.DoesNotThrow(() => bindings.Scheduler.Advance(1d / 60d));
                Assert.IsFalse(ModInfo(stack, "poked").Quarantined,
                    "quarantine is decided at Tick, never inside the scheduler frame");
                stack.Runtime.Tick(1d / 60d);
                Assert.AreEqual(poke == threshold, ModInfo(stack, "poked").Quarantined,
                    "quarantined exactly at the Tick after fault " + threshold + ", checked after " + poke);
                PumpSchedulerFrame(stack, bindings);
            }

            Assert.AreEqual(threshold, stack.Runtime.GetRecentHandlerErrors("poked").Count);

            stack.Runtime.ReloadMod("poked", @"
                workspace.ChildAdded:Connect(function(child)
                    store_set('seen', child.Name)
                end)");
            LuaModInfo reloaded = ModInfo(stack, "poked");
            Assert.IsFalse(reloaded.Quarantined, "a reload clears the scheduler-path quarantine");
            Assert.AreEqual(0, reloaded.ErrorCount, "a reload clears the scheduler-path streak");

            RbxInstance afterReload = bindings.Registry.Create("Folder");
            afterReload.Name = "Poke";
            afterReload.Parent = bindings.Registry.WorldRoot;
            PumpSchedulerFrame(stack, bindings);
            Assert.AreEqual("Poke", store.Get("poked", "seen"), "the reloaded mod dispatches again");
            Assert.AreEqual(threshold, stack.Runtime.GetRecentHandlerErrors("poked").Count);
        }

        [Test]
        public void LuaCs_M2_08_ManySchedulerFaultsInOneFrame_CountAsOneFaultingFrame()
        {
            LuaCsRbxApiBindings bindings = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildSchedulerStack(bindings, store, 2);
            stack.Runtime.LoadMod("burst", @"
                local RunService = game:GetService('RunService')
                for index = 1, 3 do
                    RunService.Heartbeat:Connect(function()
                        error('burst ' .. index)
                    end)
                end");

            PumpSchedulerFrame(stack, bindings);

            IReadOnlyList<LuaModHandlerError> errors = stack.Runtime.GetRecentHandlerErrors("burst");
            Assert.AreEqual(3, errors.Count, "every fault of the burst is still reported");
            Assert.IsTrue(errors.All(error => error.ConsecutiveCount == 1),
                "three faults in one frame are one faulting frame");
            Assert.AreEqual(1, ModInfo(stack, "burst").ErrorCount);
            Assert.IsFalse(ModInfo(stack, "burst").Quarantined,
                "one burst frame must not quarantine a mod whose threshold is two");

            PumpSchedulerFrame(stack, bindings);

            Assert.IsTrue(ModInfo(stack, "burst").Quarantined,
                "a second faulting frame in a row reaches the threshold");
            Assert.AreEqual(2, ModInfo(stack, "burst").ErrorCount);
        }

        [Test]
        public void LuaCs_M2_08_CleanSchedulerFrameDoesNotForgiveALegacyHookErrorOfTheSameFrame()
        {
            LuaCsRbxApiBindings bindings = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildSchedulerStack(bindings, store, 2);
            stack.Runtime.LoadMod("mixed", @"
                hooks_on('boom', function() error('legacy boom') end)
                game:GetService('RunService').Heartbeat:Connect(function()
                    store_set('beat', 'yes')
                end)");

            // WHY: the Heartbeat handler resumes cleanly in Advance, before Tick dispatches the failing
            // hook; closing the frame must not let that earlier clean resume erase the later failure.
            for (int frame = 1; frame <= 2; frame++)
            {
                stack.Runtime.EmitEvent("boom", "");
                PumpSchedulerFrame(stack, bindings);
                Assert.AreEqual(frame, ModInfo(stack, "mixed").ErrorCount,
                    "the hook failure of frame " + frame + " stays charged");
            }

            Assert.AreEqual("yes", store.Get("mixed", "beat"));
            Assert.IsTrue(ModInfo(stack, "mixed").Quarantined);
        }

        [Test]
        public void LuaCs_SchedulerHostFault_IsLoggedOncePerDistinctFault_AndNeverRethrownOutOfTheFrame()
        {
            RecordingLog log = new();
            LuaCsRbxApiBindings bindings = new();
            LuaCsModStack stack = BuildSchedulerStack(bindings, new MemoryStore(), 8, log);
            Assert.IsNotNull(stack.Runtime);
            int heartbeats = 0;
            bindings.Scheduler.PhaseReached += (phase, deltaSeconds) =>
            {
                if (phase != CoreAI.Mods.Rbx.Instances.Scheduling.SchedulerPhase.Heartbeat)
                {
                    return;
                }

                heartbeats++;
                throw new InvalidOperationException(
                    heartbeats <= 3 ? "host-fault-probe first" : "host-fault-probe second");
            };

            for (int frame = 0; frame < 5; frame++)
            {
                Assert.DoesNotThrow(() => bindings.Scheduler.Advance(1d / 60d),
                    "an ownerless fault the runtime reports is not rethrown after the frame");
            }

            Assert.AreEqual(5, heartbeats);
            Assert.AreEqual(1, log.Errors.Count(line => line.Contains("host-fault-probe first")),
                "a fault recurring every frame is logged once: " + string.Join(" || ", log.Errors));
            Assert.AreEqual(1, log.Errors.Count(line => line.Contains("host-fault-probe second")),
                "a distinct fault is logged once more");
        }

        [Test]
        public void LuaCs_SchedulerHostFault_DistinctFaultMemoIsBounded()
        {
            RecordingLog log = new();
            LuaCsRbxApiBindings bindings = new();
            LuaCsModStack stack = BuildSchedulerStack(bindings, new MemoryStore(), 8, log);
            Assert.IsNotNull(stack.Runtime);
            int heartbeats = 0;
            bindings.Scheduler.PhaseReached += (phase, deltaSeconds) =>
            {
                if (phase == CoreAI.Mods.Rbx.Instances.Scheduling.SchedulerPhase.Heartbeat)
                {
                    heartbeats++;
                    throw new InvalidOperationException("host-fault-probe distinct " + heartbeats);
                }
            };

            int frames = LuaCsModRuntime.MaxDistinctHostFaultsLogged + 5;
            for (int frame = 0; frame < frames; frame++)
            {
                Assert.DoesNotThrow(() => bindings.Scheduler.Advance(1d / 60d));
            }

            List<string> probeLines = log.Errors.Where(line => line.Contains("host-fault-probe")).ToList();
            Assert.AreEqual(LuaCsModRuntime.MaxDistinctHostFaultsLogged + 1, probeLines.Count,
                "each distinct fault up to the cap, then one overflow line, then nothing");
            StringAssert.Contains("further distinct faults are not logged", probeLines[probeLines.Count - 1]);
        }

        [Test]
        public void LuaCs_SchedulerHostFault_WithoutALogger_IsStillRethrownAfterTheFrame()
        {
            LuaCsRbxApiBindings bindings = new();
            LuaCsModStack stack = BuildSchedulerStack(bindings, new MemoryStore(), 8);
            Assert.IsNotNull(stack.Runtime);
            bindings.Scheduler.PhaseReached += (phase, deltaSeconds) =>
            {
                if (phase == CoreAI.Mods.Rbx.Instances.Scheduling.SchedulerPhase.Heartbeat)
                {
                    throw new InvalidOperationException("host-fault-probe unlogged");
                }
            };

            InvalidOperationException rethrown = Assert.Throws<InvalidOperationException>(
                () => bindings.Scheduler.Advance(1d / 60d),
                "with nowhere to report it, the runtime must leave the fault to the scheduler's rethrow");
            StringAssert.Contains("host-fault-probe unlogged", rethrown.Message);
        }

        /// <summary>The stack a logic-slot rollback test runs on: legacy only, or with the Rbx scheduler.</summary>
        private static LuaCsModStack BuildLogicSlotStack(bool withRbxApi)
        {
            return withRbxApi
                ? BuildMutationStack(new LuaCsRbxApiBindings())
                : BuildStack(new MemoryStore());
        }

        [TestCase(false)]
        [TestCase(true)]
        public void LuaCs_LogicSlots_FailedFirstLoad_LeavesNoFormula_AndPutsBackTheFormulaItReplaced(
            bool withRbxApi)
        {
            // WHY (A2-04): logic_define installs its formula at once, and a load that failed after it
            // never took it back, so a mod that never loaded kept answering the game's formula calls.
            LuaCsModStack stack = BuildLogicSlotStack(withRbxApi);
            LuaCsLogicSlots slots = stack.GameplayBindings.LogicSlots;
            slots.DeclareSlot("dmg");
            slots.DeclareSlot("loot");
            stack.Runtime.LoadMod("owner", "logic_define('loot', function() return 7 end)");

            Assert.Catch(() => stack.Runtime.LoadMod("never-loaded", @"
                logic_define('dmg', function(x) return x * 100 end)
                logic_define('loot', function() return 999 end)
                error('the first load fails')"));

            Assert.IsFalse(stack.Runtime.IsLoaded("never-loaded"), "precondition: the load failed");
            Assert.IsFalse(slots.IsOverridden("dmg"),
                "a mod that never loaded must not leave its formula installed");
            Assert.IsTrue(slots.TryInvokeNumber("loot", out double loot), slots.LastError);
            Assert.AreEqual(7d, loot, "the loaded mod's formula that the failed load replaced is back");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void LuaCs_LogicSlots_FailedReload_KeepsTheLiveModsFormulas_AndASuccessfulReloadStillReplacesThem(
            bool withRbxApi)
        {
            // WHY (A2-04): ReloadMod promises that a failed reload leaves the loaded mod untouched, yet
            // the candidate chunk's logic_define and logic_reset calls had already changed the live
            // formulas before its error.
            LuaCsModStack stack = BuildLogicSlotStack(withRbxApi);
            LuaCsLogicSlots slots = stack.GameplayBindings.LogicSlots;
            slots.DeclareSlot("dmg");
            slots.DeclareSlot("loot");
            stack.Runtime.LoadMod("kept", @"
                logic_define('loot', function() return 10 end)
                logic_define('dmg', function(x) return x * 2 end)");

            Assert.Catch(() => stack.Runtime.ReloadMod("kept", @"
                logic_reset('dmg')
                logic_define('loot', function() return 999 end)
                error('the reload fails')"));

            Assert.IsTrue(stack.Runtime.IsLoaded("kept"), "precondition: a failed reload keeps the mod");
            Assert.IsTrue(slots.TryInvokeNumber("loot", out double loot), slots.LastError);
            Assert.AreEqual(10d, loot, "the failed reload must not replace the live mod's formula");
            Assert.IsTrue(slots.TryInvokeNumber("dmg", out double damage, 5d),
                "the failed reload must not reset the live mod's formula either: " + slots.LastError);
            Assert.AreEqual(10d, damage);

            stack.Runtime.ReloadMod("kept", "logic_define('loot', function() return 11 end)");

            Assert.IsTrue(slots.TryInvokeNumber("loot", out double reloaded), slots.LastError);
            Assert.AreEqual(11d, reloaded, "the negative twin: a reload that succeeds replaces the formula");
            Assert.IsFalse(slots.IsOverridden("dmg"),
                "and drops the formula the new version no longer defines");
        }

        [Test]
        public void LuaCs_M2_08_AHealthyTimerDoesNotForgiveAFrameInWhichASchedulerHandlerFaulted()
        {
            // WHY (A2-06): a successful hook or timer call reset the streak right after the frame's
            // scheduler fault had lengthened it, so a Heartbeat handler that failed in every frame was
            // never quarantined as long as its mod also ran any healthy timer.
            LuaCsRbxApiBindings bindings = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildSchedulerStack(bindings, store, 2);
            stack.Runtime.LoadMod("mixed-timer", @"
                hooks_every(0, function() store_set('timer', 'ran') end)
                game:GetService('RunService').Heartbeat:Connect(function() error('every frame') end)");

            PumpSchedulerFrame(stack, bindings);

            Assert.AreEqual("ran", store.Get("mixed-timer", "timer"),
                "precondition: the healthy timer ran in the faulting frame");
            Assert.AreEqual(1, ModInfo(stack, "mixed-timer").ErrorCount,
                "the timer that succeeded after the Heartbeat fault must not erase it");
            Assert.IsFalse(ModInfo(stack, "mixed-timer").Quarantined);

            PumpSchedulerFrame(stack, bindings);

            Assert.IsTrue(ModInfo(stack, "mixed-timer").Quarantined,
                "two faulting frames in a row reach the threshold of two");
            Assert.AreEqual(2, ModInfo(stack, "mixed-timer").ErrorCount);
        }

        [Test]
        public void LuaCs_M2_08_AModWithoutSchedulerFaultsKeepsPerCallForgiveness()
        {
            // WHY: the negative twin of the test above. A mod that faults only in hooks and timers keeps
            // the per-call rule: the handler that succeeds after the failing timer in the same frame
            // resets the streak, so a failure followed by a success never adds up to a quarantine.
            LuaCsRbxApiBindings bindings = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildSchedulerStack(bindings, store, 2);
            stack.Runtime.LoadMod("legacy-only", @"
                hooks_every(0, function() error('timer boom') end)
                hooks_on('work', function() store_set('work', 'done') end)");

            const int frames = 4;
            for (int frame = 1; frame <= frames; frame++)
            {
                stack.Runtime.EmitEvent("work", "");
                PumpSchedulerFrame(stack, bindings);
                Assert.IsFalse(ModInfo(stack, "legacy-only").Quarantined,
                    "a success after each failure resets the streak; quarantined after frame " + frame);
            }

            Assert.AreEqual("done", store.Get("legacy-only", "work"));
            IReadOnlyList<LuaModHandlerError> errors = stack.Runtime.GetRecentHandlerErrors("legacy-only");
            Assert.AreEqual(frames, errors.Count);
            Assert.IsTrue(errors.All(error => error.ConsecutiveCount == 1),
                "every timer failure starts a fresh streak: "
                + string.Join(", ", errors.Select(error => error.ConsecutiveCount)));
            Assert.AreEqual(0, ModInfo(stack, "legacy-only").ErrorCount);
        }

#if COREAI_LUA
        [TestCase("store_set, {}, 'v'", "bad argument #1 to 'store_set' (string expected, got table)")]
        [TestCase("store_set, 'k', {}", "bad argument #2 to 'store_set' (string expected, got table)")]
        [TestCase("hooks_every, 'soon', function() end",
            "bad argument #1 to 'hooks_every' (number expected, got string)")]
        [TestCase("hooks_on, {}, function() end", "bad argument #1 to 'hooks_on' (string expected, got table)")]
        [TestCase("hooks_on", "hooks_on: event name and function are required.")]
        [TestCase("logic_define, 'undeclared', function() end",
            "logic_define: slot 'undeclared' is not declared by the game. Use logic_list().")]
        [TestCase("mods_call, 'missing', 'f'", "mods_call: mod 'missing' is not loaded.")]
        public void LuaCs_ModApiErrors_ReadAsLuaErrorLines_WithoutClrTypeNamesOrADoubledPrefix(
            string call, string expected)
        {
            // WHY (A2-08): a wrong argument type reached pcall as Lua-CSharp's conversion text
            // ("store_set: Cannot convert LuaValueType.Table to System.String."), and a host function
            // whose own message already named it was prefixed twice ("hooks_on: hooks_on: ...").
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(store);
            stack.Runtime.LoadMod("api-errors", "local ok, err = pcall(" + call + ")\n"
                                                + "store_set('ok', tostring(ok))\n"
                                                + "store_set('err', tostring(err))");

            Assert.AreEqual("false", store.Get("api-errors", "ok"));
            string error = store.Get("api-errors", "err");
            LuaCsSecureSandboxEditModeTests.AssertIsOnlyTheErrorLine(error);
            Assert.AreEqual(expected, error);
        }

        [Test]
        public void LuaCs_HostFunctionReadingAnArgumentAsTheWrongType_ReportsOnlyTheLuaTypes()
        {
            // WHY (A2-08): a host function that reads an argument with LuaValue.Read handed the script
            // Lua-CSharp's "Cannot convert LuaValueType.Table to System.String." through its pcall.
            MemoryStore store = new();
            LuaCsModStack stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = new FakeGameLogger(),
                ModStore = store,
                Capabilities = LuaCapabilities.All,
                OneOffCapabilities = LuaCapabilities.All,
                AdditionalGameplayBindings = (registry, capabilities) =>
                    registry.RegisterCallback("raw_read", (ctx, ct) =>
                    {
                        string text = ctx.GetArgument(0).Read<string>();
                        return new ValueTask<int>(ctx.Return(text.Length));
                    })
            });
            stack.Runtime.LoadMod("raw-read", "local ok, err = pcall(raw_read, {})\n"
                                              + "store_set('err', tostring(err))\n"
                                              + "store_set('length', tostring(raw_read('four')))");

            string error = store.Get("raw-read", "err");
            LuaCsSecureSandboxEditModeTests.AssertIsOnlyTheErrorLine(error);
            Assert.AreEqual("bad argument to 'raw_read' (string expected, got table)", error);
            Assert.AreEqual("4", store.Get("raw-read", "length"), "the negative twin: a string argument reads");
        }
#endif

        [Test]
        [Timeout(60000)]
        public void LuaCs_ModsCall_FromASignalHandler_RunsTheExportWithinTheHandlersResumeBudget()
        {
            // WHY (A2-10): the export ran under a fresh handler budget (50,000,000 steps, 10 s), so a
            // Heartbeat handler held to its per-resume budget got the whole handler budget through
            // one call of its own export, and the frame stalled for seconds.
            LuaCsRbxApiBindings bindings = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildSchedulerStack(bindings, store, 8);
            stack.Runtime.LoadMod("spinner", @"
                local spins = 0
                mods_export('spin', function()
                    while true do spins = spins + 1 end
                end)
                local fired = false
                game:GetService('RunService').Heartbeat:Connect(function()
                    if fired then return end
                    fired = true
                    local ok, err = pcall(mods_call, mod_id(), 'spin')
                    store_set('ok', tostring(ok))
                    store_set('err', tostring(err))
                    store_set('spins', tostring(spins))
                end)");

            PumpSchedulerFrame(stack, bindings);

            int resumeSteps = bindings.CoroutineResumeBudget.BudgetPerResume;
            Assert.AreEqual("false", store.Get("spinner", "ok"),
                "the export must be cut, and the handler must still be inside its own budget afterwards");
            StringAssert.StartsWith(
                "LuaCsSecureEnvironment: EXCEEDED_HARD_LIMIT_STEPS (" + InvariantText(resumeSteps) + ")",
                store.Get("spinner", "err"));
            int spins = int.Parse(store.Get("spinner", "spins"), System.Globalization.CultureInfo.InvariantCulture);
            Assert.Greater(spins, 0, "precondition: the export ran");
            Assert.LessOrEqual(spins, resumeSteps,
                "the export ran no further than the handler's own per-resume step budget");
        }

        [Test]
        [Timeout(60000)]
        public void LuaCs_ModsCall_ExportStopsAtOnceWhenItsCallingThreadIsKilled()
        {
            // WHY (A2-10): the export ran with CancellationToken.None instead of the token its caller
            // was called with, so stopping the calling thread left the export running to the end of
            // its own budget.
            LuaCsRbxApiBindings bindings = new();
            MemoryStore store = new();
            LuaCsModStack stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = new FakeGameLogger(),
                ModStore = store,
                Capabilities = LuaCapabilities.All,
                OneOffCapabilities = LuaCapabilities.All,
                RbxApi = bindings,
                AdditionalGameplayBindings = (registry, capabilities) =>
                    registry.Register("stop_my_threads",
                        new Func<int>(() => bindings.Scheduler.KillOwnedBy("stopped")))
            });
            stack.Runtime.LoadMod("stopped", @"
                local spins = 0
                mods_export('spin', function()
                    stop_my_threads()
                    for i = 1, 2000000 do spins = spins + 1 end
                end)
                hooks_on('read', function() store_set('spins', tostring(spins)) end)
                task.defer(function() mods_call(mod_id(), 'spin') end)");

            Assert.DoesNotThrow(() => bindings.Scheduler.Advance(1d / 60d),
                "a thread killed inside its own export call must not abort the frame");

            stack.Runtime.EmitEvent("read", "");
            stack.Runtime.Tick(1d / 60d);

            Assert.AreEqual("0", store.Get("stopped", "spins"),
                "the export must end at its first check after its calling thread was killed");
        }

        private static IDictionary QuotaAttribution(LuaCsModRuntime runtime)
        {
            FieldInfo field = typeof(LuaCsModRuntime).GetField(
                "_quotaActorByOwnerModId", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, "the runtime's quota attribution map was renamed");
            return (IDictionary)field.GetValue(runtime);
        }

        [Test]
        public void LuaCs_QuotaAttribution_IsDroppedByAFailedFirstLoadAndByUnload_AndKeptByAFailedReload()
        {
            // WHY (A2-11): every load recorded the actor its mod id is charged to, and nothing ever
            // removed the record, so the map grew by one entry for every mod id ever attempted.
            LuaCsRbxApiBindings rbxApi = new();
            LuaCsModStack stack = BuildMutationStack(rbxApi);
            ActorContext actor = QuotaActor("attribution-actor");
            IDictionary attribution = QuotaAttribution(stack.Runtime);

            Assert.Catch(() => stack.Runtime.LoadMod(
                actor, "never-loaded", "error('the first load fails')", persistToStore: false));
            Assert.IsFalse(attribution.Contains("never-loaded"),
                "a mod whose first load failed keeps no attribution");

            stack.Runtime.LoadMod(actor, "kept", "local loaded = true", persistToStore: false);
            Assert.AreEqual(actor.ActorId, attribution["kept"]);
            Assert.Catch(() => stack.Runtime.ReloadMod("kept", "error('the reload fails')"));
            Assert.AreEqual(actor.ActorId, attribution["kept"],
                "the negative twin: a failed reload keeps the loaded mod's attribution");

            Assert.IsTrue(stack.Runtime.UnloadMod("kept"));
            Assert.IsFalse(attribution.Contains("kept"), "an unloaded mod keeps no attribution");

            for (int index = 0; index < 50; index++)
            {
                Assert.Catch(() => stack.Runtime.LoadMod(
                    actor, "churn-" + index, "error('fails')", persistToStore: false));
            }

            Assert.AreEqual(0, attribution.Count, "the map stays bounded by the loaded mods");
        }

        [Test]
        public void LuaCs_RegisteredInstanceQuota_RefusalIsACodedBudgetError_NamingWhatWasCreated_WithAFix()
        {
            // WHY (A3-05): the refusal was a plain InvalidOperationException that began with
            // "Instance.new:" even for Clone and TweenService:Create, carried no code a repair loop
            // could classify and no hint how to get back under the quota.
            const int quota = 3;
            LuaCsRbxApiBindings rbxApi = new();
            MemoryStore store = new();
            LuaCsModStack stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = new FakeGameLogger(),
                ModStore = store,
                Capabilities = LuaCapabilities.All,
                OneOffCapabilities = LuaCapabilities.All,
                RbxApi = rbxApi,
                MaxRegisteredInstancesPerActor = quota
            });
            ActorContext actor = QuotaActor("coded-quota-actor");

            stack.Runtime.LoadMod(actor, "coded-quota", @"
                local function grab(key, fn)
                    local ok, err = pcall(fn)
                    store_set(key .. '_ok', tostring(ok))
                    store_set(key .. '_err', tostring(err))
                end
                local model = Instance.new('Model')
                local part = Instance.new('Part')
                part.Parent = model
                local spare = Instance.new('Part')
                grab('new', function() return Instance.new('Folder') end)
                grab('clone', function() return part:Clone() end)
                grab('tween', function()
                    return game:GetService('TweenService'):Create(part, TweenInfo.new(1), {Transparency = 1})
                end)", persistToStore: false);

            string limit = "registered instances quota reached (limit " + InvariantText(quota) + ")";
            foreach ((string key, string created) in new[]
                     {
                         ("new", "Instance.new(\"Folder\")"),
                         ("clone", "creating Part"),
                         ("tween", "creating Tween")
                     })
            {
                Assert.AreEqual("false", store.Get("coded-quota", key + "_ok"), key + " must be refused");
                string error = store.Get("coded-quota", key + "_err");
                StringAssert.Contains("BUDGET_EXCEEDED: " + created + ": actor '" + actor.ActorId + "'", error,
                    key + " names what was being created and the actor it is charged to");
                StringAssert.Contains(limit, error, key + " keeps the limit");
                StringAssert.Contains(" | fix: destroy instances", error, key + " says how to get back under it");
            }

            StringAssert.DoesNotContain("Instance.new", store.Get("coded-quota", "clone_err"),
                "a refused Clone must not be reported as an Instance.new");
            StringAssert.DoesNotContain("Instance.new", store.Get("coded-quota", "tween_err"),
                "a refused TweenService:Create must not be reported as an Instance.new");
        }
    }
}
