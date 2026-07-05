#if COREAI_HAS_MOONSHARP && !COREAI_NO_LUA
using CoreAI.Ai;
using CoreAI.Infrastructure.Logging;
using CoreAI.Messaging;
using MessagePipe;
using UnityEngine;
using VContainer.Unity;

namespace CoreAI.Infrastructure.Lua
{
    /// <summary>
    /// Drives <see cref="LuaModRuntime.Tick"/> from the Unity player loop so mod timers and
    /// queued events run on the main thread once per frame.
    /// </summary>
    public sealed class LuaModRuntimeTicker : ITickable
    {
        private readonly LuaModRuntime _runtime;
        private readonly IGameLogger _logger;
        private readonly IPublisher<LuaModEventEmitted> _eventPublisher;

        public LuaModRuntimeTicker(
            LuaModRuntime runtime,
            IGameLogger logger = null,
            IPublisher<LuaModEventEmitted> eventPublisher = null)
        {
            _runtime = runtime;
            _logger = logger;
            _eventPublisher = eventPublisher;
            if (_runtime != null)
            {
                _runtime.ModReportEmitted += OnModReportEmitted;
                _runtime.ModEventEmitted += OnModEventEmitted;
            }
        }

        public void Tick()
        {
            _runtime?.Tick(Time.deltaTime);
        }

        private void OnModReportEmitted(string modId, string message)
        {
            _logger?.LogInfo(GameLogFeature.MessagePipe, $"[Lua mod report:{modId}] {message}");
        }

        private void OnModEventEmitted(string modId, string eventName, string payload)
        {
            _eventPublisher?.Publish(new LuaModEventEmitted(modId, eventName, payload));
        }
    }
}
#endif