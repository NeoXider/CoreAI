using System;
using System.Collections.Generic;
using System.Reflection;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Composition;
using CoreAI.Infrastructure.Logging;
using CoreAI.Messaging;
using CoreAI.Mods.Rbx.Instances.Networking;
using NUnit.Framework;
using UnityEngine;
using VContainer;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// The empty-field contract of the network transport seam on <see cref="CoreAiModsLifetimeScope"/>:
    /// with no bridge provider assigned, the scope registers no <see cref="INetworkBridge"/> and the
    /// installer's world falls back to <see cref="NullNetworkBridge"/> - the behaviour every scene had
    /// before the field existed.
    /// </summary>
    /// <remarks>
    /// WHY this lives here and not next to the provider's own fixture: the only other test of the field
    /// compiles under the MIRROR define, and Assets/Mirror is gitignored, so a clone without Mirror keeps a
    /// green suite while losing every line that touches the seam. This assembly always compiles,
    /// references nothing from Mirror, and drives the scope's real Configure the way that fixture does -
    /// a copy of the registration would pass under a scope that had stopped consulting the field.
    /// </remarks>
    [TestFixture]
    public sealed class CoreAiModsLifetimeScopeNoNetworkBridgeEditModeTests
    {
        private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

        private GameObject _scopeGo;
        private CoreAiModsLifetimeScope _scope;

        [SetUp]
        public void CreateScope()
        {
            _scopeGo = new GameObject("CoreAiModsLifetimeScope_NoBridge");
            _scope = _scopeGo.AddComponent<CoreAiModsLifetimeScope>();
        }

        [TearDown]
        public void DestroyScope()
        {
            UnityEngine.Object.DestroyImmediate(_scopeGo);
        }

        [Test]
        public void Configure_WithNoBridgeProvider_RegistersNoNetworkBridge()
        {
            Assert.IsNull(GetField(_scope, "networkBridgeProvider"),
                "a freshly added scope must start with the transport field empty");
            ContainerBuilder builder = new();

            Configure(_scope, builder);

            Assert.IsFalse(builder.Exists(typeof(INetworkBridge), includeInterfaceTypes: true),
                "an empty provider field must leave the container without any transport registration");
            using IObjectResolver container = builder.Build();
            Assert.IsNull(container.ResolveOrDefault<INetworkBridge>(),
                "the installer's ResolveOrDefault<INetworkBridge>() must see nothing - that is what selects NullNetworkBridge");
        }

        [Test]
        public void Configure_WithNoBridgeProvider_WorldRunsOnTheNullBridge()
        {
            ContainerBuilder builder = new();
            Configure(_scope, builder);
            RegisterMinimalHostServices(builder);

            using IObjectResolver container = builder.Build();
            LuaCsModStack stack = container.Resolve<LuaCsModStack>();

            Assert.IsInstanceOf<NullNetworkBridge>(stack.GameplayBindings.RbxApi.NetworkBridge,
                "with nothing registered the installer must build the Rbx world on the in-process null bridge");
        }

        /// <summary>
        /// The services the installer's world factory resolves unconditionally and a scope normally
        /// inherits from its CoreAI parent; the null log keeps the headless-world diagnostic out of the
        /// Unity console, and the in-memory store keeps the persistent mod store untouched.
        /// </summary>
        private static void RegisterMinimalHostServices(IContainerBuilder builder)
        {
            builder.RegisterInstance<IGameLogger>(new SilentGameLogger());
            builder.RegisterInstance<Logging.ILog>(Logging.NullLog.Instance);
            builder.Register<NoopCommandSink>(Lifetime.Singleton).As<IAiGameCommandSink>();
            builder.RegisterInstance<ILuaModStore>(new MemoryStore());
            builder.RegisterInstance<ILuaModSourceStore>(new EmptySourceStore());
        }

        private static void Configure(CoreAiModsLifetimeScope scope, ContainerBuilder builder)
        {
            MethodInfo configure = typeof(CoreAiModsLifetimeScope).GetMethod(
                "Configure", Private, null, new[] { typeof(IContainerBuilder) }, null);
            Assert.IsNotNull(configure, "CoreAiModsLifetimeScope.Configure(IContainerBuilder)");
            configure.Invoke(scope, new object[] { builder });
        }

        private static object GetField(object target, string name)
        {
            FieldInfo field = target.GetType().GetField(name, Private);
            Assert.IsNotNull(field, target.GetType().Name + "." + name);
            return field.GetValue(target);
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

        private sealed class NoopCommandSink : IAiGameCommandSink
        {
            public void Publish(ApplyAiGameCommand command)
            {
            }
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

                foreach ((string storedModId, string key) in keys)
                {
                    _values.Remove((storedModId, key));
                }
            }
        }

        private sealed class EmptySourceStore : ILuaModSourceStore
        {
            public void Save(string id, string source, LuaModManifest manifest)
            {
            }

            public bool TryLoad(string id, out string source, out LuaModManifest manifest)
            {
                source = null;
                manifest = null;
                return false;
            }

            public IReadOnlyList<LuaModManifest> List()
            {
                return Array.Empty<LuaModManifest>();
            }

            public void SetActive(string id, bool active)
            {
            }

            public void Delete(string id)
            {
            }
        }
    }
}
