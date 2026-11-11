using System;
using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.Chat;
using CoreAI.Messaging;
using UnityEngine;

namespace CoreAI.ChatOverlay
{
    /// <summary>
    /// <see cref="IChatModelSelector"/> over <see cref="ICoreAiRoutingUiController"/>, the routing controller the Hub
    /// Settings page and the chat panel's API selector use, so <c>/model</c> changes the same per-role assignment.
    /// </summary>
    public sealed class CoreAiRoutingChatModelSelector : IChatModelSelector
    {
        private readonly Func<ICoreAiRoutingUiController> _controller;

        /// <summary>
        /// Creates the selector; <paramref name="controller"/> is read on every call so a controller attached
        /// later is picked up. Null reads <see cref="CoreAiRoutingUi.Controller"/>.
        /// </summary>
        public CoreAiRoutingChatModelSelector(Func<ICoreAiRoutingUiController> controller = null)
        {
            _controller = controller ?? (() => CoreAiRoutingUi.Controller);
        }

        /// <inheritdoc />
        public bool IsAvailable => _controller() != null;

        /// <inheritdoc />
        public IReadOnlyList<ChatModelProfile> GetProfiles()
        {
            ICoreAiRoutingUiController controller = _controller();
            IReadOnlyList<LlmRuntimeProfile> profiles = controller?.GetProfiles();
            if (profiles == null || profiles.Count == 0)
            {
                return Array.Empty<ChatModelProfile>();
            }

            IReadOnlyList<LlmEndpointSnapshot> endpoints = controller.GetEndpoints();
            List<ChatModelProfile> result = new(profiles.Count);
            foreach (LlmRuntimeProfile profile in profiles)
            {
                if (profile == null || string.IsNullOrWhiteSpace(profile.ProfileId))
                {
                    continue;
                }

                result.Add(new ChatModelProfile(profile.ProfileId, profile.DisplayName,
                    EndpointState(profile.EndpointId, endpoints)));
            }

            return result;
        }

        /// <inheritdoc />
        public string GetProfileForRole(string roleId)
        {
            return _controller()?.GetProfileForRole(roleId) ?? "";
        }

        /// <inheritdoc />
        public bool TryAssign(string roleId, string profileId, out string message)
        {
            ICoreAiRoutingUiController controller = _controller();
            if (controller == null)
            {
                message = "no model routing is configured";
                return false;
            }

            CoreAiRoutingUiResult result = controller.AssignProfileToRole(roleId, profileId ?? "");
            message = result.Message;
            return result.Ok;
        }

        private static string EndpointState(string endpointId, IReadOnlyList<LlmEndpointSnapshot> endpoints)
        {
            if (endpoints != null)
            {
                foreach (LlmEndpointSnapshot snapshot in endpoints)
                {
                    if (string.Equals(snapshot?.Descriptor?.EndpointId, endpointId, StringComparison.Ordinal))
                    {
                        return snapshot.State.ToString();
                    }
                }
            }

            return "Unavailable";
        }
    }

    /// <summary>
    /// <see cref="IAgentToolEventSource"/> over the <c>CoreAi</c> tool-call events, the ones the chat panel and the
    /// chat service observe. Dispose to unsubscribe.
    /// </summary>
    public sealed class CoreAiToolEventSource : IAgentToolEventSource, IDisposable
    {
        private bool _subscribed;

        /// <summary>Subscribes to the <c>CoreAi</c> tool-call events.</summary>
        public CoreAiToolEventSource()
        {
            CoreAi.OnToolCallStarted += ForwardStarted;
            CoreAi.OnToolCallCompleted += ForwardCompleted;
            CoreAi.OnToolCallFailed += ForwardFailed;
            _subscribed = true;
        }

        /// <inheritdoc />
        public event Action<LlmToolCallStarted> ToolCallStarted;

        /// <inheritdoc />
        public event Action<LlmToolCallCompleted> ToolCallCompleted;

        /// <inheritdoc />
        public event Action<LlmToolCallFailed> ToolCallFailed;

        /// <inheritdoc />
        public void Dispose()
        {
            if (!_subscribed)
            {
                return;
            }

            _subscribed = false;
            CoreAi.OnToolCallStarted -= ForwardStarted;
            CoreAi.OnToolCallCompleted -= ForwardCompleted;
            CoreAi.OnToolCallFailed -= ForwardFailed;
        }

        private void ForwardStarted(LlmToolCallStarted evt)
        {
            ToolCallStarted?.Invoke(evt);
        }

        private void ForwardCompleted(LlmToolCallCompleted evt)
        {
            ToolCallCompleted?.Invoke(evt);
        }

        private void ForwardFailed(LlmToolCallFailed evt)
        {
            ToolCallFailed?.Invoke(evt);
        }
    }

    /// <summary>Unscaled realtime clock: lines keep fading while the game is paused.</summary>
    public sealed class UnityChatOverlayClock : IChatOverlayClock
    {
        /// <inheritdoc />
        public double NowSeconds => Time.realtimeSinceStartupAsDouble;
    }
}
