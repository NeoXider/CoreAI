using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Binding.RuntimeUi;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.RuntimeUi;
using CoreAI.UiFocus;

namespace CoreAI.Ai.LuaCs
{
    /// <summary>
    /// The composition's runtime UI for Lua: one per scope, holding the runtime UI host (as an
    /// <see cref="IRbxUiBackend"/>, or null when the game registered none), the per-mod budgets, and the
    /// focus reporter that makes the game see input over a screen as processed. Every Rbx world of the
    /// scope is attached with <see cref="AttachTo"/>, which gives its DataModel a working
    /// <c>UxmlService</c>; a world that was never attached answers <c>UI_UNAVAILABLE</c>.
    /// </summary>
    public sealed class RuntimeUiLuaSurface : IDisposable
    {
        private readonly List<RbxUiWorld> _worlds = new();
        private readonly RuntimeUiFocusReporter _focusReporter;
        private int _attachCount;
        private bool _disposed;

        /// <summary>Creates the surface.</summary>
        /// <param name="backend">The runtime UI host; null when the game has none (headless, dedicated server).</param>
        /// <param name="budgets">Per-mod limits; null uses the defaults.</param>
        /// <param name="uiFocus">The shared CoreAI UI focus; null skips focus reporting.</param>
        public RuntimeUiLuaSurface(IRbxUiBackend backend, RbxUiBudgets budgets = null, ICoreAiUiFocus uiFocus = null)
        {
            Backend = backend;
            Budgets = budgets ?? new RbxUiBudgets();
            _focusReporter = new RuntimeUiFocusReporter(backend, uiFocus);
        }

        /// <summary>The runtime UI host, or null.</summary>
        public IRbxUiBackend Backend { get; }

        /// <summary>The per-mod limits every attached world enforces.</summary>
        public RbxUiBudgets Budgets { get; }

        /// <summary>True while host focus and pointer state reach the shared UI focus.</summary>
        public bool IsReportingFocus => _focusReporter.IsActive;

        /// <summary>
        /// Gives an Rbx world its <c>UxmlService</c>: registers the service on the world's DataModel when
        /// the catalog did not, attaches a fresh <see cref="RbxUiWorld"/> bound to the world's scheduler,
        /// and ties that state to the world's lifetime, so retiring the world destroys its screens.
        /// Idempotent per world.
        /// </summary>
        public RbxUiWorld AttachTo(LuaCsRbxApiBindings rbxApi)
        {
            if (rbxApi == null)
            {
                throw new ArgumentNullException(nameof(rbxApi));
            }

            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(RuntimeUiLuaSurface));
            }

            RbxUxmlService service = EnsureService(rbxApi.Game);
            if (service.World != null && !service.World.IsDisposed)
            {
                return service.World;
            }

            _worlds.RemoveAll(retired => retired.IsDisposed);
            _attachCount++;
            // WHY a prefix after the first world: a world load stages the next world while the current
            // one still runs, and both talk to the same host, whose screen ids must stay unique.
            string prefix = _attachCount == 1 ? string.Empty : "w" + _attachCount + ":";
            RbxUiWorld world = new(Backend, Budgets, prefix);
            world.AttachScheduler(rbxApi.Scheduler);
            if (Backend != null)
            {
                // WHY the same world: the Roblox GUI entrance draws through the same host and counts
                // against the same per-mod budgets as UxmlService screens (plan decision D14).
                RbxGuiPresenter presenter = new(rbxApi.Registry, world, rbxApi.ResolveScreenPlayer,
                    rbxApi.ReadMouseLocation, rbxApi.CreateGuiClickInput, rbxApi.LogSink, prefix);
                world.AttachGuiPresenter(presenter);
                rbxApi.UserInputService?.AttachFocusedTextBoxSource(() => presenter.FocusedTextBox);
                presenter.HeldKeyboardKeys = () => HeldKeyboardKeys(rbxApi.UserInputService);
            }

            service.Attach(world);
            _worlds.Add(world);
            rbxApi.RegisterRuntimeLifetime(world);
            return world;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            for (int i = 0; i < _worlds.Count; i++)
            {
                _worlds[i].Dispose();
            }

            _worlds.Clear();
            _focusReporter.Dispose();
        }

        /// <summary>The keyboard keys (not gamepad buttons) the world's input source holds now.</summary>
        internal static int[] HeldKeyboardKeys(RbxUserInputService input)
        {
            IInputSource source = input?.InputSource;
            if (source == null)
            {
                return Array.Empty<int>();
            }

            List<int> held = new();
            source.CollectPressedKeyCodes(held);
            // WHY below 1000: gamepad buttons share Enum.KeyCode from 1000 up and never type into a field.
            held.RemoveAll(key => key >= 1000);
            return held.ToArray();
        }

        private static RbxUxmlService EnsureService(RbxDataModel game)
        {
            if (game == null)
            {
                throw new ArgumentException("The Rbx world has no DataModel.", nameof(game));
            }

            foreach (string name in game.Services.ServiceNames)
            {
                if (string.Equals(name, RbxUxmlService.ServiceClassName, StringComparison.Ordinal))
                {
                    if (game.Services.FindService(name) is RbxUxmlService registered)
                    {
                        return registered;
                    }

                    break;
                }
            }

            RbxUxmlService service = new();
            game.Services.Register(RbxUxmlService.ServiceClassName, service);
            return service;
        }
    }
}
