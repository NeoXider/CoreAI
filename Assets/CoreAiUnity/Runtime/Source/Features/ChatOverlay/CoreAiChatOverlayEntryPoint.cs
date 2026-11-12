using System;
using CoreAI.Infrastructure.Logging;
using UnityEngine;
using UnityEngine.UIElements;
using VContainer.Unity;

namespace CoreAI.ChatOverlay
{
    /// <summary>
    /// Scope entry point of the in-world chat: registers the built-in commands, puts the default view on its own
    /// document (or into <see cref="CoreAiChatOverlaySettings.HostElement"/>), polls the open and close keys and
    /// ticks the controller every frame. A replaced view that is not <see cref="CoreAiChatOverlayView"/> gets no
    /// document; it owns its presentation. Disposing removes the commands and the document. While another surface
    /// claims the chat (<see cref="IChatOverlaySurfaceSwitch"/>, a chat made in Lua) the overlay steps aside: its view
    /// is detached, no key is read and its built-in commands are removed, so the claimant's own commands of those
    /// names register; the controller keeps ticking.
    /// </summary>
    public sealed class CoreAiChatOverlayEntryPoint : IStartable, ITickable, IDisposable
    {
        private readonly ChatOverlayController _controller;
        private readonly IChatOverlayView _view;
        private readonly IChatOverlayKeyboard _keyboard;
        private readonly CoreAiChatOverlaySettings _settings;
        private readonly IChatAgentSelector _agents;
        private readonly IChatModelSelector _models;
        private readonly IChatOverlayOpenGate _gate;
        private readonly IGameLogger _logger;
        private readonly IChatOverlaySurfaceSwitch _surfaceSwitch;
        private CoreAiChatOverlaySurface _surface;
        private VisualElement _attachedRoot;
        private IDisposable _builtIns;
        private bool _started;
        private bool _disposed;
        private bool _steppedAside;

        /// <summary>
        /// Creates the entry point; <paramref name="models"/>, <paramref name="gate"/>, <paramref name="logger"/> and
        /// <paramref name="surfaceSwitch"/> may be null.
        /// </summary>
        public CoreAiChatOverlayEntryPoint(
            ChatOverlayController controller,
            IChatOverlayView view,
            IChatOverlayKeyboard keyboard,
            CoreAiChatOverlaySettings settings,
            IChatAgentSelector agents,
            IChatModelSelector models = null,
            IChatOverlayOpenGate gate = null,
            IGameLogger logger = null,
            IChatOverlaySurfaceSwitch surfaceSwitch = null)
        {
            _controller = controller ?? throw new ArgumentNullException(nameof(controller));
            _view = view ?? throw new ArgumentNullException(nameof(view));
            _keyboard = keyboard ?? throw new ArgumentNullException(nameof(keyboard));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _agents = agents;
            _models = models;
            _gate = gate;
            _logger = logger;
            _surfaceSwitch = surfaceSwitch;
        }

        /// <summary>True while another surface claimed the chat and the overlay shows nothing.</summary>
        public bool IsSteppedAside => _steppedAside;

        /// <summary>The overlay's own document; null when attached into a host element or not started.</summary>
        public CoreAiChatOverlaySurface Surface => _surface;

        /// <inheritdoc />
        public void Start()
        {
            if (_started || _disposed)
            {
                return;
            }

            _started = true;
            if (_surfaceSwitch != null)
            {
                _surfaceSwitch.Changed += OnSurfaceChanged;
            }

            if (_surfaceSwitch != null && !_surfaceSwitch.IsDefaultSurfaceShown)
            {
                _steppedAside = true;
                return;
            }

            ShowOverlay();
        }

        private void ShowOverlay()
        {
            if (_settings.RegisterBuiltInCommands && _builtIns == null)
            {
                _builtIns = ChatOverlayBuiltInCommands.RegisterAll(_controller.Commands, _agents, _models, _controller);
            }

            if (_view is CoreAiChatOverlayView view)
            {
                VisualElement host = _settings.HostElement;
                if (host == null)
                {
                    _surface ??= CoreAiChatOverlaySurface.Create(_settings);
                    host = _surface.Root;
                }

                if (host != null)
                {
                    view.Attach(host);
                    _attachedRoot = host;
                }
            }
        }

        private void HideOverlay()
        {
            _controller.CloseInput();
            _builtIns?.Dispose();
            _builtIns = null;
            if (_view is CoreAiChatOverlayView view)
            {
                view.Detach();
            }

            _attachedRoot = null;
        }

        /// <summary>Follows the surface switch at once, so a claimant registers its commands in the same frame.</summary>
        internal void OnSurfaceChanged()
        {
            if (!_started || _disposed || _surfaceSwitch == null)
            {
                return;
            }

            bool aside = !_surfaceSwitch.IsDefaultSurfaceShown;
            if (aside == _steppedAside)
            {
                return;
            }

            _steppedAside = aside;
            try
            {
                if (aside)
                {
                    HideOverlay();
                }
                else
                {
                    ShowOverlay();
                }
            }
            catch (Exception ex)
            {
                (_logger ?? GameLoggerUnscopedFallback.Instance).LogWarning(GameLogFeature.Core,
                    $"[CoreAiChatOverlay] Stepping {(aside ? "aside" : "back")} failed: {ex.Message}");
            }
        }

        /// <inheritdoc />
        public void Tick()
        {
            if (!_started || _disposed)
            {
                return;
            }

            if (!_steppedAside)
            {
                ReattachIfRebuilt();
                PollKeys(Time.frameCount);
            }

            _controller.Tick();
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_surfaceSwitch != null)
            {
                _surfaceSwitch.Changed -= OnSurfaceChanged;
            }

            _builtIns?.Dispose();
            _builtIns = null;
            if (_view is CoreAiChatOverlayView view)
            {
                view.Detach();
            }

            _surface?.Dispose();
            _surface = null;
            _attachedRoot = null;
        }

        /// <summary>
        /// One frame of key handling: Escape closes an open input; while closed, an open key opens it unless the
        /// gate refuses. In the frame the input closed nothing reopens it, so the Enter that sent a message does
        /// not open the input again. Internal so a test drives it with a fake keyboard.
        /// </summary>
        internal void PollKeys(int frame)
        {
            try
            {
                if (_controller.IsInputVisible)
                {
                    if (_settings.CloseKey != KeyCode.None && _keyboard.WasPressedThisFrame(_settings.CloseKey))
                    {
                        _controller.CloseInput();
                    }

                    return;
                }

                if (_controller.IsInputOpen)
                {
                    return;
                }

                ChatOverlayOpenRequest request = ChatOverlayHotkeys.Resolve(_keyboard, _settings, out KeyCode key);
                if (request == ChatOverlayOpenRequest.None || (_gate != null && !_gate.IsOpenAllowed()))
                {
                    return;
                }

                if (request == ChatOverlayOpenRequest.Command)
                {
                    _controller.OpenCommandInput();
                }
                else
                {
                    _controller.OpenInput();
                }

                if (_view is CoreAiChatOverlayView view)
                {
                    view.SuppressOpeningKey(key, frame);
                }
            }
            catch (Exception ex)
            {
                (_logger ?? GameLoggerUnscopedFallback.Instance).LogWarning(GameLogFeature.Core,
                    $"[CoreAiChatOverlay] Key polling failed this frame: {ex.Message}");
            }
        }

        private void ReattachIfRebuilt()
        {
            if (_surface == null || !(_view is CoreAiChatOverlayView view))
            {
                return;
            }

            VisualElement root = _surface.Root;
            if (root != null && (!ReferenceEquals(root, _attachedRoot) || view.Root.parent != root))
            {
                view.Attach(root);
                _attachedRoot = root;
            }
        }
    }
}
