using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.Composition;
using CoreAI.Infrastructure.Logging;
using CoreAI.Logging;
using CoreAI.Messaging;
using NUnit.Framework;
using UnityEngine;
using VContainer;

namespace CoreAI.Tests.EditMode
{
    /// <summary>Production-composed acceptance coverage for subscription-routed Lua events.</summary>
    public sealed class LuaCsEventRoutingProductionPathEditModeTests
    {
        [Test]
        public void RegisterCoreAiMods_OneSubscriberLeavesNineteenNonSubscriberQueuesUntouched()
        {
            SilentCoreLog coreLog = new SilentCoreLog();
            using GlobalLogScope logScope = new GlobalLogScope(coreLog);
            MemoryStore store = new MemoryStore();
            ContainerBuilder builder = new ContainerBuilder();
            builder.RegisterInstance<IGameLogger>(new SilentGameLogger());
            builder.RegisterInstance<ILog>(coreLog);
            builder.RegisterInstance<IAiGameCommandSink>(new NoopCommandSink());
            builder.RegisterCoreAiMods(
                applicationIsPlayingProvider: () => false,
                skillTextProvider: _ => null);
            builder.RegisterInstance<ILuaModStore>(store);
            builder.RegisterInstance<ILuaModSourceStore>(NullLuaModSourceStore.Instance);

            using IObjectResolver container = builder.Build();
            ILuaModRuntime runtime = container.Resolve<ILuaModRuntime>();
            LuaCsModRuntime concreteRuntime = container.Resolve<LuaCsModRuntime>();
            for (int index = 0; index < 20; index++)
            {
                string modId = index == 0 ? "subscriber" : $"non-subscriber-{index:00}";
                string source = index == 0
                    ? "hooks_on('target', function(_, payload) store_set('ran', payload) end)"
                    : $"hooks_on('other-{index:00}', function() store_set('ran', 'wrong') end)";
                ActorContext owner = new LocalActorIdentityProvider($"event-actor-{index:00}")
                    .GetActorContext(BuiltInAgentRoleIds.Programmer);
                runtime.LoadMod(owner, modId, source, persistToStore: false);
            }

            Dictionary<string, int> before = ReadPendingQueueCounts(concreteRuntime);
            ActorContext hostActor = CoreServicesInstaller.DefaultLocalHostIdentityProvider
                .GetActorContext(BuiltInAgentRoleIds.Programmer);
            runtime.EmitEvent(hostActor, "target", "delivered");
            Dictionary<string, int> after = ReadPendingQueueCounts(concreteRuntime);

            Assert.AreEqual(20, after.Count);
            Assert.AreEqual(before["subscriber"] + 1, after["subscriber"],
                "The subscribed production mod must receive exactly one queued event.");
            for (int index = 1; index < 20; index++)
            {
                string modId = $"non-subscriber-{index:00}";
                Assert.AreEqual(before[modId], after[modId],
                    $"Non-subscriber '{modId}' was touched by the production event route.");
            }

            runtime.Tick(hostActor, 0d);
            Assert.AreEqual("delivered", store.Get("subscriber", "ran"));
            for (int index = 1; index < 20; index++)
            {
                string modId = $"non-subscriber-{index:00}";
                Assert.AreEqual("", store.Get(modId, "ran"),
                    $"Non-subscriber '{modId}' ran a handler for an event it did not subscribe to.");
            }
        }

        private static Dictionary<string, int> ReadPendingQueueCounts(LuaCsModRuntime runtime)
        {
            FieldInfo modsField = typeof(LuaCsModRuntime).GetField(
                "_mods", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(modsField);
            IDictionary mods = modsField.GetValue(runtime) as IDictionary;
            Assert.IsNotNull(mods);

            Dictionary<string, int> counts = new Dictionary<string, int>();
            foreach (DictionaryEntry entry in mods)
            {
                object mod = entry.Value;
                FieldInfo pendingField = mod.GetType().GetField(
                    "Pending", BindingFlags.Instance | BindingFlags.Public);
                Assert.IsNotNull(pendingField);
                ICollection pending = pendingField.GetValue(mod) as ICollection;
                Assert.IsNotNull(pending);
                counts.Add((string)entry.Key, pending.Count);
            }

            return counts;
        }

        private sealed class MemoryStore : ILuaModStore
        {
            private readonly Dictionary<(string ModId, string Key), string> _values =
                new Dictionary<(string ModId, string Key), string>();

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
                List<(string ModId, string Key)> removed = new List<(string ModId, string Key)>();
                foreach ((string ModId, string Key) key in _values.Keys)
                {
                    if (string.Equals(key.ModId, modId, System.StringComparison.Ordinal))
                    {
                        removed.Add(key);
                    }
                }

                foreach ((string ModId, string Key) key in removed)
                {
                    _values.Remove(key);
                }
            }
        }

        private sealed class NoopCommandSink : IAiGameCommandSink
        {
            public void Publish(ApplyAiGameCommand command)
            {
            }
        }

        private sealed class SilentGameLogger : IGameLogger
        {
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

        private sealed class GlobalLogScope : IDisposable
        {
            private readonly ILog _saved;

            public GlobalLogScope(ILog replacement)
            {
                _saved = Log.Instance;
                Log.Instance = replacement;
            }

            public void Dispose()
            {
                Log.Instance = _saved;
            }
        }

        private sealed class SilentCoreLog : ILog
        {
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
            }
        }
    }
}
