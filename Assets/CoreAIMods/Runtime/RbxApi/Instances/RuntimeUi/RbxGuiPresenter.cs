using System;
using System.Collections.Generic;
using System.Globalization;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances.Networking;
using CoreAI.Mods.Rbx.Instances.Scheduling;

namespace CoreAI.Mods.Rbx.Instances.RuntimeUi
{
    /// <summary>
    /// The Roblox GUI entrance of runtime UI (plan decision D14): it shows every <c>ScreenGui</c> in the local
    /// player's <c>PlayerGui</c> as one screen of the same runtime UI host the <c>UxmlService</c> screens use,
    /// each GUI object as one element built by the same element factory, and keeps them in step with the
    /// instances. Engine-free: it drives an <see cref="IRbxUiBackend"/> and counts against the per-mod budgets
    /// of <see cref="RbxUiWorld"/> (a ScreenGui is a screen, a GUI object an element, a property write of a shown
    /// object an element write), so both entrances share ownership, budgets, focus reporting and lifetime.
    /// </summary>
    /// <remarks>
    /// Nothing runs on an idle frame: writes mark their node dirty, and once per frame
    /// (<see cref="SchedulerPhase.PreRender"/>) the presenter re-reads the tree if it changed, lays out the screens
    /// whose layout changed and pushes only the styles whose value differs from the last one pushed, through the
    /// port's typed style path. Every element is absolutely positioned in pixels from the engine-free layout, so
    /// <c>AbsolutePosition</c> and <c>AbsoluteSize</c> are exactly what is drawn.
    /// </remarks>
    public sealed class RbxGuiPresenter : IDisposable, IRbxGuiLayoutSource
    {
        /// <summary>
        /// The USS every GUI screen is built with: it neutralizes the control theme so a Roblox box draws only
        /// what its properties say, routes a TextBox's text style into the field's inner input, and turns a
        /// ScrollingFrame's scroll view into Roblox's: a canvas that holds the children, and flat bars of the frame's
        /// thickness and color drawn over the canvas on the side it names, without arrow buttons.
        /// </summary>
        public const string ScreenUss =
            ".rbx-gui { position: absolute; margin: 0; padding: 0; border-width: 0; border-radius: 0; "
            + "background-color: rgba(0, 0, 0, 0); min-width: 0; min-height: 0; max-width: none; max-height: none; "
            + "flex-shrink: 0; }\n"
            + ".rbx-textbox .unity-base-text-field__input { margin: 0; padding: 0; border-width: 0; "
            + "background-color: rgba(0, 0, 0, 0); flex-grow: 1; color: var(--rbx-text-color, rgb(27, 42, 53)); "
            + "font-size: var(--rbx-font-size, 8px); -unity-text-align: var(--rbx-text-align, middle-center); }\n"
            + ".rbx-textbox .unity-label { display: none; }\n"
            + ".rbx-canvas { position: relative; margin: 0; padding: 0; border-width: 0; flex-grow: 0; "
            + "flex-shrink: 0; overflow: hidden; background-color: rgba(0, 0, 0, 0); }\n"
            + ".rbx-scroll > .unity-scroll-view__content-and-vertical-scroll-container > .unity-scroller--vertical { "
            + "position: absolute; top: 0; bottom: 0; width: var(--rbx-scroll-bar-size, 12px); min-width: 0; "
            + "margin: 0; }\n"
            + ".rbx-scroll-right > .unity-scroll-view__content-and-vertical-scroll-container > .unity-scroller--vertical"
            + " { right: 0; }\n"
            + ".rbx-scroll-left > .unity-scroll-view__content-and-vertical-scroll-container > .unity-scroller--vertical"
            + " { left: 0; }\n"
            + ".rbx-scroll > .unity-scroller--horizontal { position: absolute; left: 0; right: 0; bottom: 0; "
            + "height: var(--rbx-scroll-bar-size, 12px); min-height: 0; margin: 0; }\n"
            + ".rbx-scroll > .unity-scroll-view__content-and-vertical-scroll-container > .unity-scroller--vertical "
            + ".unity-scroller__low-button, "
            + ".rbx-scroll > .unity-scroll-view__content-and-vertical-scroll-container > .unity-scroller--vertical "
            + ".unity-scroller__high-button, "
            + ".rbx-scroll > .unity-scroller--horizontal .unity-scroller__low-button, "
            + ".rbx-scroll > .unity-scroller--horizontal .unity-scroller__high-button { display: none; }\n"
            + ".rbx-scroll > .unity-scroll-view__content-and-vertical-scroll-container > .unity-scroller--vertical "
            + ".unity-base-slider__tracker, "
            + ".rbx-scroll > .unity-scroller--horizontal .unity-base-slider__tracker { "
            + "background-color: rgba(0, 0, 0, 0); border-width: 0; }\n"
            + ".rbx-scroll > .unity-scroll-view__content-and-vertical-scroll-container > .unity-scroller--vertical "
            + ".unity-base-slider__dragger, "
            + ".rbx-scroll > .unity-scroller--horizontal .unity-base-slider__dragger { "
            + "background-color: var(--rbx-scroll-bar-color, rgb(255, 255, 255)); border-width: 0; }\n";

        private const string ElementClass = "rbx-gui";
        private const string TextBoxClass = "rbx-textbox";
        private const string ScrollClass = "rbx-scroll";
        private const string CanvasClass = "rbx-canvas";
        private const string Member = "ScreenGui";

        private readonly InstanceRegistry _registry;
        private readonly RbxUiWorld _world;
        private readonly Func<RbxPlayer> _localPlayer;
        private readonly Func<RbxVector2> _mouseLocation;
        private readonly Func<object> _clickInput;
        private readonly Action<string> _log;
        private readonly string _hostIdPrefix;
        private readonly Dictionary<RbxScreenGui, RbxGuiScreen> _screens = new();
        private readonly List<RbxGuiScreen> _screenOrder = new();
        private readonly Dictionary<RbxInstance, RbxGuiNode> _nodes = new();
        private readonly Dictionary<RbxInstance, RbxGuiNode> _componentOwners = new();
        private readonly HashSet<RbxInstance> _watched = new();
        private readonly HashSet<RbxInstance> _reported = new();
        private readonly List<RbxScreenGui> _wanted = new();
        private readonly List<RbxGuiScreen> _screenScratch = new();
        private readonly List<RbxGuiNode> _removeScratch = new();
        private readonly Action<InstanceRecord> _onUnregistered;
        private readonly Action<RbxInstance> _onParentChanged;
        private readonly Action<SchedulerPhase, double> _onPhase;
        private readonly Action<RbxTextBox, bool> _onFocusRequest;
        private ModScheduler _scheduler;
        private RbxPlayer _lastPlayer;
        private RbxInstance _lastPlayerGui;
        private bool _structureDirty = true;
        private bool _disposed;
        private int _generation;

        /// <summary>Creates the presenter of one world.</summary>
        /// <param name="registry">The world's instances.</param>
        /// <param name="world">The runtime UI state of the world: the host port and the per-mod budgets.</param>
        /// <param name="localPlayer">The player at this screen, read every frame; null shows nothing.</param>
        /// <param name="mouseLocation">The pointer position MouseEnter and MouseLeave report; null reports (0, 0).</param>
        /// <param name="clickInput">The InputObject a click hands <c>Activated</c>; null hands nil.</param>
        /// <param name="log">One line per refused screen or element; may be null.</param>
        /// <param name="hostIdPrefix">Prefix of host screen ids, keeping two worlds of one host apart.</param>
        public RbxGuiPresenter(InstanceRegistry registry, RbxUiWorld world, Func<RbxPlayer> localPlayer,
            Func<RbxVector2> mouseLocation = null, Func<object> clickInput = null, Action<string> log = null,
            string hostIdPrefix = null)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _world = world ?? throw new ArgumentNullException(nameof(world));
            _localPlayer = localPlayer;
            _mouseLocation = mouseLocation;
            _clickInput = clickInput;
            _log = log;
            _hostIdPrefix = hostIdPrefix ?? string.Empty;
            _onUnregistered = OnUnregistered;
            _onParentChanged = OnParentChanged;
            _onPhase = OnPhase;
            _onFocusRequest = OnFocusRequest;
            _registry.Unregistered += _onUnregistered;
            _registry.ParentChanged += _onParentChanged;
            _registry.GuiLayout = this;
            if (world.Scheduler != null)
            {
                _scheduler = world.Scheduler;
                _scheduler.PhaseReached += _onPhase;
            }
        }

        /// <summary>Screens shown now.</summary>
        public int ScreenCount => _screenOrder.Count;

        /// <summary>Elements shown now over every screen (the screen roots excluded).</summary>
        public int ElementCount
        {
            get
            {
                int count = 0;
                for (int i = 0; i < _screenOrder.Count; i++)
                {
                    count += _screenOrder[i].ElementCount;
                }

                return count;
            }
        }

        /// <summary>True after <see cref="Dispose"/>.</summary>
        public bool IsDisposed => _disposed;

        /// <summary>The shown TextBox that holds keyboard focus, or null.</summary>
        public RbxTextBox FocusedTextBox { get; private set; }

        /// <summary>The host screen showing <paramref name="screenGui"/>, or null.</summary>
        public IRbxUiScreen GetScreenPort(RbxScreenGui screenGui)
        {
            return screenGui != null && _screens.TryGetValue(screenGui, out RbxGuiScreen screen) ? screen.Port : null;
        }

        /// <summary>The host element drawing <paramref name="gui"/>, or null.</summary>
        public IRbxUiElement GetElementPort(RbxGuiObject gui)
        {
            return gui != null && _nodes.TryGetValue(gui, out RbxGuiNode node) ? node.Element : null;
        }

        /// <summary>
        /// Refuses, before it happens, a move of <paramref name="target"/> under <paramref name="destination"/> that
        /// would put more screens or elements on screen than the owning mod's budget holds; the Lua Parent write
        /// calls it so the error names the line that made the move.
        /// </summary>
        public void RequireRoomForMove(RbxInstance target, RbxInstance destination)
        {
            if (_disposed || target == null || destination == null || !(target is RbxGuiBase2d))
            {
                return;
            }

            if (!WouldBeShown(destination, target is RbxScreenGui))
            {
                return;
            }

            string owner = OwnerOf(target is RbxScreenGui ? target : FindScreenGui(destination));
            if (string.IsNullOrWhiteSpace(owner))
            {
                return;
            }

            if (target is RbxScreenGui && !_screens.ContainsKey((RbxScreenGui)target)
                                       && _world.CountScreens(owner) >= _world.Budgets.MaxScreensPerMod)
            {
                throw RbxUiErrors.Create(RbxUiErrors.BudgetScreens,
                    "ScreenGui.Parent refused: mod '" + owner + "' already shows " + _world.CountScreens(owner)
                    + " screens (budget " + _world.Budgets.MaxScreensPerMod + ")",
                    "show fewer ScreenGuis at once, or Destroy those you no longer need");
            }

            int elements = CountGuiObjects(target);
            if (!_nodes.ContainsKey(target) && !_world.HasElementRoom(owner, elements))
            {
                throw RbxUiErrors.Create(RbxUiErrors.BudgetElements,
                    target.ClassName + ".Parent refused: mod '" + owner + "' would show "
                    + (_world.CountElements(owner) + elements) + " GUI elements (budget "
                    + _world.Budgets.MaxElementsPerMod + ")",
                    "reuse GUI objects instead of creating new ones, or Destroy those you no longer show");
            }
        }

        /// <summary>
        /// Brings every screen up to date now: re-reads the tree when it changed, lays out the screens whose layout
        /// changed and pushes what differs. Runs once per frame on its own; tests and hosts may call it directly.
        /// </summary>
        public void Flush()
        {
            if (_disposed || !_world.IsAvailable)
            {
                return;
            }

            CheckLocalPlayer();
            if (_structureDirty)
            {
                Reconcile();
            }

            for (int i = 0; i < _screenOrder.Count; i++)
            {
                _screenOrder[i].Update(true);
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _screenScratch.Clear();
            _screenScratch.AddRange(_screenOrder);
            for (int i = 0; i < _screenScratch.Count; i++)
            {
                DestroyScreen(_screenScratch[i]);
            }

            _screenScratch.Clear();
            _disposed = true;
            _registry.Unregistered -= _onUnregistered;
            _registry.ParentChanged -= _onParentChanged;
            if (ReferenceEquals(_registry.GuiLayout, this))
            {
                _registry.GuiLayout = null;
            }
            if (_scheduler != null)
            {
                _scheduler.PhaseReached -= _onPhase;
            }
        }

        internal RbxUiWorld World => _world;

        void IRbxGuiLayoutSource.EnsureLayout(RbxInstance instance)
        {
            if (_disposed || !_world.IsAvailable)
            {
                return;
            }

            CheckLocalPlayer();
            if (!_structureDirty)
            {
                return;
            }

            Reconcile();
            IRbxGuiObserver observer = instance is RbxGuiBase2d gui ? gui.Observer
                : instance is RbxUiBase component ? component.Observer : null;
            observer?.EnsureLayout();
        }

        internal void EnsureLayout(RbxGuiScreen screen)
        {
            if (_disposed || !_world.IsAvailable)
            {
                return;
            }

            CheckLocalPlayer();
            if (_structureDirty)
            {
                Reconcile();
            }

            if (!screen.IsClosed)
            {
                screen.Update(false);
            }
        }

        internal void MarkDirty(RbxInstance source, RbxGuiChange change)
        {
            if (source is RbxUiBase component)
            {
                if (_componentOwners.TryGetValue(component, out RbxGuiNode owner))
                {
                    owner.Dirty |= change;
                    MarkScreen(owner, change);
                }

                return;
            }

            if (_nodes.TryGetValue(source, out RbxGuiNode node))
            {
                node.Dirty |= change;
                MarkScreen(node, change);
            }
        }

        private static void MarkScreen(RbxGuiNode node, RbxGuiChange change)
        {
            RbxGuiNode root = node;
            while (root.Parent != null)
            {
                root = root.Parent;
            }

            if (root.Gui.Observer is RbxGuiScreen screen)
            {
                screen.HasDirty = true;
                if ((change & (RbxGuiChange.Layout | RbxGuiChange.Visibility)) != 0)
                {
                    screen.LayoutDirty = true;
                }
            }
        }

        private void OnPhase(SchedulerPhase phase, double deltaSeconds)
        {
            if (phase == SchedulerPhase.PreRender)
            {
                Flush();
            }
        }

        private void OnParentChanged(RbxInstance instance)
        {
            if (_disposed || _structureDirty)
            {
                return;
            }

            if (instance is RbxGuiBase2d || instance is RbxUiBase || instance is RbxPlayerGui
                || instance is RbxPlayer || _watched.Contains(instance)
                || instance.FindFirstAncestorOfClass("PlayerGui") != null)
            {
                _structureDirty = true;
            }
        }

        private void OnUnregistered(InstanceRecord record)
        {
            RbxInstance instance = record?.Instance;
            if (instance != null && (_nodes.ContainsKey(instance) || _componentOwners.ContainsKey(instance)))
            {
                _structureDirty = true;
            }
        }

        private void CheckLocalPlayer()
        {
            RbxPlayer player = _localPlayer?.Invoke();
            if (player != null && player.IsDestroyed)
            {
                player = null;
            }

            RbxInstance playerGui = player?.FindFirstChildOfClass("PlayerGui");
            if (!ReferenceEquals(player, _lastPlayer) || !ReferenceEquals(playerGui, _lastPlayerGui))
            {
                _lastPlayer = player;
                _lastPlayerGui = playerGui;
                _structureDirty = true;
            }
        }

        // ---- structure ---------------------------------------------------------------------------

        private void Reconcile()
        {
            _structureDirty = false;
            _wanted.Clear();
            _watched.Clear();
            if (_lastPlayerGui != null && !_lastPlayerGui.IsDestroyed)
            {
                CollectScreens(_lastPlayerGui, 0);
            }

            _screenScratch.Clear();
            for (int i = 0; i < _screenOrder.Count; i++)
            {
                RbxGuiScreen screen = _screenOrder[i];
                if (screen.ScreenGui.IsDestroyed || !_wanted.Contains(screen.ScreenGui) || screen.Port.IsDestroyed)
                {
                    _screenScratch.Add(screen);
                }
            }

            for (int i = 0; i < _screenScratch.Count; i++)
            {
                DestroyScreen(_screenScratch[i]);
            }

            _screenScratch.Clear();
            for (int i = 0; i < _wanted.Count; i++)
            {
                if (!_screens.ContainsKey(_wanted[i]))
                {
                    CreateScreen(_wanted[i]);
                }
            }

            _wanted.Clear();
            for (int i = 0; i < _screenOrder.Count; i++)
            {
                ReconcileTree(_screenOrder[i]);
            }
        }

        private void CollectScreens(RbxInstance container, int depth)
        {
            if (depth > InstanceTreeSerializer.MaximumSnapshotDepth)
            {
                return;
            }

            IReadOnlyList<RbxInstance> children = container.GetChildren();
            for (int i = 0; i < children.Count; i++)
            {
                RbxInstance child = children[i];
                if (child is RbxScreenGui screenGui)
                {
                    _wanted.Add(screenGui);
                }
                else if (!(child is RbxGuiBase2d) && !(child is RbxUiBase))
                {
                    _watched.Add(child);
                    CollectScreens(child, depth + 1);
                }
            }
        }

        private void CreateScreen(RbxScreenGui screenGui)
        {
            IRbxUiBackend backend = _world.Backend;
            if (backend == null || !backend.IsEnabled)
            {
                ReportOnce(screenGui, "[CoreAI.RbxGui] ScreenGui \"" + screenGui.Name
                                      + "\" is not shown: runtime UI is switched off in this game");
                return;
            }

            string owner = OwnerOf(screenGui);
            try
            {
                _world.ReserveExternalScreen(owner, Member);
            }
            catch (RbxError error)
            {
                ReportOnce(screenGui, "[CoreAI.RbxGui] ScreenGui \"" + screenGui.Name + "\" is not shown: "
                                      + error.Message);
                return;
            }

            string hostId = _hostIdPrefix + "rbxgui/" + screenGui.Id.Value.ToString(CultureInfo.InvariantCulture);
            RbxUiResult result = backend.CreateScreen(hostId, owner, null, ScreenUss, screenGui.DisplayOrder,
                screenGui.Enabled);
            if (!result.Success || result.Screen == null)
            {
                _world.ReleaseExternalScreen(owner);
                ReportOnce(screenGui, "[CoreAI.RbxGui] ScreenGui \"" + screenGui.Name + "\" is not shown: "
                                      + (result.ErrorCode ?? "UI_UNAVAILABLE") + " " + result.Error);
                return;
            }

            RbxGuiNode root = new(screenGui, null);
            RbxGuiScreen screen = new(this, screenGui, owner, result.Screen, root);
            _screens.Add(screenGui, screen);
            _screenOrder.Add(screen);
            _nodes[screenGui] = root;
            screenGui.Observer = screen;
            _reported.Remove(screenGui);
        }

        private void DestroyScreen(RbxGuiScreen screen)
        {
            if (screen.IsClosed)
            {
                return;
            }

            ReleaseSubtree(screen, screen.Root, false);
            screen.Close();
            _screens.Remove(screen.ScreenGui);
            _screenOrder.Remove(screen);
            _nodes.Remove(screen.ScreenGui);
            if (ReferenceEquals(screen.ScreenGui.Observer, screen))
            {
                screen.ScreenGui.Observer = null;
            }

            _world.ReleaseExternalScreen(screen.OwnerModId);
            if (!screen.Port.IsDestroyed)
            {
                screen.Port.Destroy();
            }
        }

        private void ReconcileTree(RbxGuiScreen screen)
        {
            _generation++;
            screen.Root.Generation = _generation;
            Visit(screen, screen.Root, 0);
            _removeScratch.Clear();
            CollectStale(screen.Root, _removeScratch);
            for (int i = 0; i < _removeScratch.Count; i++)
            {
                RbxGuiNode stale = _removeScratch[i];
                ReleaseSubtree(screen, stale, true);
                stale.Parent?.Children.Remove(stale);
            }

            _removeScratch.Clear();
            screen.LayoutDirty = true;
            screen.HasDirty = true;
            screen.Root.Dirty |= RbxGuiChange.Screen;
        }

        private void Visit(RbxGuiScreen screen, RbxGuiNode node, int depth)
        {
            DetachComponents(node);
            if (depth > InstanceTreeSerializer.MaximumSnapshotDepth)
            {
                return;
            }

            IReadOnlyList<RbxInstance> children = node.Gui.GetChildren();
            List<RbxGuiNode> ordered = new(children.Count);
            for (int i = 0; i < children.Count; i++)
            {
                RbxInstance child = children[i];
                if (child is RbxUiBase component)
                {
                    AttachComponent(screen, node, component);
                    continue;
                }

                if (!(child is RbxGuiObject gui))
                {
                    continue;
                }

                if (_nodes.TryGetValue(gui, out RbxGuiNode existing) && ReferenceEquals(existing.Parent, node))
                {
                    existing.Generation = _generation;
                    ordered.Add(existing);
                    if (existing.Element == null && !TryCreateElement(screen, existing))
                    {
                        continue;
                    }

                    Visit(screen, existing, depth + 1);
                    continue;
                }

                if (existing != null)
                {
                    RbxGuiScreen previous = ScreenOf(existing);
                    ReleaseSubtree(previous ?? screen, existing, true);
                    existing.Parent?.Children.Remove(existing);
                }

                RbxGuiNode created = new(gui, node) { Generation = _generation };
                _nodes[gui] = created;
                ordered.Add(created);
                if (TryCreateElement(screen, created))
                {
                    Visit(screen, created, depth + 1);
                }
            }

            for (int i = 0; i < node.Children.Count; i++)
            {
                if (!ordered.Contains(node.Children[i]))
                {
                    ordered.Add(node.Children[i]);
                }
            }

            node.Children.Clear();
            node.Children.AddRange(ordered);
        }

        private static void CollectStale(RbxGuiNode node, List<RbxGuiNode> stale)
        {
            for (int i = 0; i < node.Children.Count; i++)
            {
                RbxGuiNode child = node.Children[i];
                if (child.Generation != node.Generation || child.Gui.IsDestroyed
                                                        || !ReferenceEquals(child.Gui.Parent, node.Gui))
                {
                    stale.Add(child);
                }
                else
                {
                    CollectStale(child, stale);
                }
            }
        }

        private bool TryCreateElement(RbxGuiScreen screen, RbxGuiNode node)
        {
            if (node.Element != null)
            {
                return true;
            }

            RbxGuiObject gui = node.Object;
            IRbxUiElement parentElement = node.Parent.ChildContainer ?? screen.Port.Root;
            if (node.Parent.Parent != null && node.Parent.Element == null)
            {
                return false;
            }

            if (!_world.HasElementRoom(screen.OwnerModId, 1))
            {
                ReportOnce(gui, "[CoreAI.RbxGui] " + gui.ClassName + " \"" + gui.Name + "\" is not shown: mod '"
                                + screen.OwnerModId + "' reached its budget of " + _world.Budgets.MaxElementsPerMod
                                + " GUI elements");
                return false;
            }

            IRbxUiElement element = screen.Port.CreateElement(KindOf(gui), parentElement, null);
            IRbxUiElement canvas = null;
            if (element != null && gui is RbxScrollingFrame)
            {
                canvas = element.CreateChild("VisualElement", null);
                if (canvas == null)
                {
                    element.Remove();
                    element = null;
                }
            }

            if (element == null)
            {
                ReportOnce(gui, "[CoreAI.RbxGui] " + gui.ClassName + " \"" + gui.Name
                                + "\" is not shown: the runtime UI host refused the element (screen element limit)");
                return false;
            }

            _world.ReserveExternalElements(screen.OwnerModId, 1, Member);
            node.Element = element;
            node.Canvas = canvas;
            node.Pushed = new RbxUiStyleValue[(int)RbxUiStyleProperty.Count];
            node.ResetScrollPushes();
            node.Dirty = (RbxGuiChange)int.MaxValue;
            element.AddClass(ElementClass);
            if (gui is RbxTextBox)
            {
                element.AddClass(TextBoxClass);
            }
            else if (canvas != null)
            {
                element.AddClass(ScrollClass);
                canvas.AddClass(CanvasClass);
                canvas.SetAttribute("picking-mode", "Ignore");
            }

            Subscribe(node);
            gui.Observer = screen;
            screen.ElementCount++;
            return true;
        }

        private void ReleaseSubtree(RbxGuiScreen screen, RbxGuiNode node, bool removeElement)
        {
            for (int i = 0; i < node.Children.Count; i++)
            {
                ReleaseSubtree(screen, node.Children[i], false);
            }

            node.Children.Clear();
            DetachComponents(node);
            if (node.Parent == null)
            {
                return;
            }

            if (node.Element != null)
            {
                Unsubscribe(node);
                if (removeElement && node.Element.IsAlive)
                {
                    node.Element.Remove();
                }

                node.Element = null;
                node.Canvas = null;
                screen.ElementCount--;
                _world.ReleaseExternalElements(screen.OwnerModId, 1);
            }

            if (ReferenceEquals(node.Gui.Observer, screen))
            {
                node.Gui.Observer = null;
            }

            if (node.Gui is RbxTextBox box)
            {
                box.FocusRequest = null;
            }

            if (_nodes.TryGetValue(node.Gui, out RbxGuiNode current) && ReferenceEquals(current, node))
            {
                _nodes.Remove(node.Gui);
            }
        }

        private void AttachComponent(RbxGuiScreen screen, RbxGuiNode node, RbxUiBase component)
        {
            bool attached = false;
            switch (component)
            {
                case RbxUiCorner corner when node.Corner == null:
                    node.Corner = corner;
                    attached = true;
                    break;
                case RbxUiPadding padding when node.Padding == null:
                    node.Padding = padding;
                    attached = true;
                    break;
                case RbxUiStroke stroke when node.Stroke == null:
                    node.Stroke = stroke;
                    attached = true;
                    break;
                case RbxUiScale scale when node.Scale == null:
                    node.Scale = scale;
                    attached = true;
                    break;
                case RbxUiGridStyleLayout layout when node.Layout == null:
                    node.Layout = layout;
                    attached = true;
                    break;
                case RbxUiAspectRatioConstraint aspect when node.Aspect == null:
                    node.Aspect = aspect;
                    attached = true;
                    break;
                case RbxUiSizeConstraint size when node.SizeLimit == null:
                    node.SizeLimit = size;
                    attached = true;
                    break;
            }

            if (attached)
            {
                _componentOwners[component] = node;
                component.Observer = screen;
                node.Dirty |= RbxGuiChange.Paint | RbxGuiChange.Layout | RbxGuiChange.Text;
            }
        }

        private void DetachComponents(RbxGuiNode node)
        {
            DetachComponent(node, node.Corner);
            DetachComponent(node, node.Padding);
            DetachComponent(node, node.Stroke);
            DetachComponent(node, node.Scale);
            DetachComponent(node, node.Layout);
            DetachComponent(node, node.Aspect);
            DetachComponent(node, node.SizeLimit);
            node.Corner = null;
            node.Padding = null;
            node.Stroke = null;
            node.Scale = null;
            node.Layout = null;
            node.Aspect = null;
            node.SizeLimit = null;
        }

        private void DetachComponent(RbxGuiNode node, RbxUiBase component)
        {
            if (component == null)
            {
                return;
            }

            if (_componentOwners.TryGetValue(component, out RbxGuiNode owner) && ReferenceEquals(owner, node))
            {
                _componentOwners.Remove(component);
                component.Observer = null;
            }

            node.Dirty |= RbxGuiChange.Paint | RbxGuiChange.Layout | RbxGuiChange.Text;
        }

        private RbxGuiScreen ScreenOf(RbxGuiNode node)
        {
            RbxGuiNode root = node;
            while (root.Parent != null)
            {
                root = root.Parent;
            }

            return root.Gui.Observer as RbxGuiScreen;
        }

        // ---- events from the screen --------------------------------------------------------------

        private void Subscribe(RbxGuiNode node)
        {
            IRbxUiElement element = node.Element;
            RbxGuiObject gui = node.Object;
            node.OnEntered = () => OnPointerEntered(node);
            node.OnExited = () => OnPointerExited(node);
            element.PointerEntered += node.OnEntered;
            element.PointerExited += node.OnExited;
            if (gui is RbxGuiButton)
            {
                node.OnClicked = () => OnClicked(node);
                element.Clicked += node.OnClicked;
            }

            if (gui is RbxTextBox box)
            {
                node.OnFocusGained = () => OnFieldFocusGained(node);
                node.OnFocusLost = () => OnFieldFocusLost(node);
                node.OnSubmitted = () => OnFieldSubmitted(node);
                node.OnValueChanged = () => OnFieldTyped(node);
                element.FocusGained += node.OnFocusGained;
                element.FocusLost += node.OnFocusLost;
                element.Submitted += node.OnSubmitted;
                element.ValueChanged += node.OnValueChanged;
                box.FocusRequest = _onFocusRequest;
            }

            if (gui is RbxScrollingFrame)
            {
                node.OnScrolled = () => OnScrolled(node);
                element.ScrollChanged += node.OnScrolled;
            }
        }

        private static void Unsubscribe(RbxGuiNode node)
        {
            IRbxUiElement element = node.Element;
            if (node.OnEntered != null)
            {
                element.PointerEntered -= node.OnEntered;
                element.PointerExited -= node.OnExited;
            }

            if (node.OnClicked != null)
            {
                element.Clicked -= node.OnClicked;
            }

            if (node.OnFocusGained != null)
            {
                element.FocusGained -= node.OnFocusGained;
                element.FocusLost -= node.OnFocusLost;
                element.Submitted -= node.OnSubmitted;
                element.ValueChanged -= node.OnValueChanged;
            }

            if (node.OnScrolled != null)
            {
                element.ScrollChanged -= node.OnScrolled;
            }

            node.OnScrolled = null;
            node.OnEntered = null;
            node.OnExited = null;
            node.OnClicked = null;
            node.OnFocusGained = null;
            node.OnFocusLost = null;
            node.OnSubmitted = null;
            node.OnValueChanged = null;
        }

        private void OnPointerEntered(RbxGuiNode node)
        {
            RbxGuiObject gui = node.Object;
            if (gui == null || gui.IsDestroyed)
            {
                return;
            }

            node.Hovered = true;
            if (gui is RbxGuiButton button && button.AutoButtonColor)
            {
                MarkDirty(gui, RbxGuiChange.Paint);
            }

            RbxVector2 mouse = _mouseLocation?.Invoke() ?? RbxVector2.Zero;
            gui.FireMouseEnter((int)Math.Round(mouse.X), (int)Math.Round(mouse.Y));
        }

        private void OnPointerExited(RbxGuiNode node)
        {
            RbxGuiObject gui = node.Object;
            if (gui == null || gui.IsDestroyed)
            {
                return;
            }

            node.Hovered = false;
            if (gui is RbxGuiButton button && button.AutoButtonColor)
            {
                MarkDirty(gui, RbxGuiChange.Paint);
            }

            RbxVector2 mouse = _mouseLocation?.Invoke() ?? RbxVector2.Zero;
            gui.FireMouseLeave((int)Math.Round(mouse.X), (int)Math.Round(mouse.Y));
        }

        private void OnClicked(RbxGuiNode node)
        {
            if (node.Gui is RbxGuiButton button && !button.IsDestroyed && button.Interactable)
            {
                button.FireClick(_clickInput?.Invoke());
            }
        }

        private void OnFieldFocusGained(RbxGuiNode node)
        {
            if (!(node.Gui is RbxTextBox box) || box.IsDestroyed)
            {
                return;
            }

            box.OnFocusGained();
            if (box.ClearTextOnFocus && node.Element != null && !string.IsNullOrEmpty(node.Element.Text))
            {
                node.Element.SetText(string.Empty);
                node.PushedText = string.Empty;
            }

            FocusedTextBox = box;
        }

        private void OnFieldFocusLost(RbxGuiNode node)
        {
            if (!(node.Gui is RbxTextBox box) || box.IsDestroyed)
            {
                return;
            }

            ReadTypedText(node, box);
            box.OnFocusLost(null);
            if (ReferenceEquals(FocusedTextBox, box))
            {
                FocusedTextBox = null;
            }
        }

        private void OnFieldSubmitted(RbxGuiNode node)
        {
            if (!(node.Gui is RbxTextBox box) || box.IsDestroyed || box.MultiLine)
            {
                return;
            }

            ReadTypedText(node, box);
            box.PendingEnterPressed = true;
            node.Element?.Blur();
        }

        private static void OnFieldTyped(RbxGuiNode node)
        {
            if (node.Gui is RbxTextBox box && !box.IsDestroyed)
            {
                ReadTypedText(node, box);
            }
        }

        private static void ReadTypedText(RbxGuiNode node, RbxTextBox box)
        {
            string typed = node.Element?.Text;
            if (typed != null && box.TextState.ApplyTypedText(typed))
            {
                node.PushedText = box.TextState.Text;
            }
        }

        private void OnFocusRequest(RbxTextBox box, bool focus)
        {
            if (!_nodes.TryGetValue(box, out RbxGuiNode node) || node.Element == null)
            {
                return;
            }

            if (focus)
            {
                node.Element.Focus();
            }
            else
            {
                node.Element.Blur();
            }
        }

        /// <summary>
        /// The player scrolled a ScrollingFrame (wheel, drag, scroll bar), or the host clamped its offset: an axis the
        /// player may scroll (<c>ScrollingEnabled</c>, <c>ScrollingDirection</c>) keeps the new offset inside the
        /// canvas, an axis they may not snaps back, and <c>CanvasPosition</c> takes the result as a layout result:
        /// its signal fires and no budget is charged.
        /// </summary>
        private static void OnScrolled(RbxGuiNode node)
        {
            IRbxUiElement element = node.Element;
            if (!(node.Gui is RbxScrollingFrame frame) || frame.IsDestroyed || element == null || !element.IsAlive)
            {
                return;
            }

            float x = element.ScrollOffsetX;
            float y = element.ScrollOffsetY;
            bool allowX = frame.ScrollingEnabled && frame.ScrollingDirection != RbxScrollingDirection.Y;
            bool allowY = frame.ScrollingEnabled && frame.ScrollingDirection != RbxScrollingDirection.X;
            float keptX = allowX ? ClampScroll(x, node.CanvasW - node.WindowW) : node.ScrollX;
            float keptY = allowY ? ClampScroll(y, node.CanvasH - node.WindowH) : node.ScrollY;
            if (!keptX.Equals(x) || !keptY.Equals(y))
            {
                element.SetScrollOffset(keptX, keptY);
            }

            node.PushedScrollSet = true;
            node.PushedScrollX = keptX;
            node.PushedScrollY = keptY;
            frame.ApplyScrolledPosition(new RbxVector2(keptX, keptY));
        }

        private static float ClampScroll(float value, float max)
        {
            if (float.IsNaN(value) || value < 0f)
            {
                return 0f;
            }

            return max > 0f && value > max ? max : max <= 0f ? 0f : value;
        }

        // ---- helpers -----------------------------------------------------------------------------

        private bool WouldBeShown(RbxInstance destination, bool isScreen)
        {
            if (_lastPlayerGui == null)
            {
                CheckLocalPlayer();
            }

            if (_lastPlayerGui == null || _lastPlayerGui.IsDestroyed)
            {
                return false;
            }

            if (isScreen)
            {
                for (RbxInstance current = destination; current != null; current = current.Parent)
                {
                    if (ReferenceEquals(current, _lastPlayerGui))
                    {
                        return true;
                    }

                    if (current is RbxGuiBase2d)
                    {
                        return false;
                    }
                }

                return false;
            }

            RbxScreenGui screen = FindScreenGui(destination);
            return screen != null && destination is RbxGuiBase2d && _screens.ContainsKey(screen);
        }

        private static RbxScreenGui FindScreenGui(RbxInstance from)
        {
            for (RbxInstance current = from; current != null; current = current.Parent)
            {
                if (current is RbxScreenGui screen)
                {
                    return screen;
                }

                if (!(current is RbxGuiBase2d))
                {
                    return null;
                }
            }

            return null;
        }

        private static int CountGuiObjects(RbxInstance root)
        {
            int count = root is RbxGuiObject ? 1 : 0;
            Stack<RbxInstance> pending = new();
            pending.Push(root);
            while (pending.Count > 0)
            {
                IReadOnlyList<RbxInstance> children = pending.Pop().GetChildren();
                for (int i = 0; i < children.Count; i++)
                {
                    if (children[i] is RbxGuiObject)
                    {
                        count++;
                        pending.Push(children[i]);
                    }
                }
            }

            return count;
        }

        private string OwnerOf(RbxInstance instance)
        {
            if (instance == null)
            {
                return null;
            }

            return _registry.TryGetRecord(instance.Id, out InstanceRecord record) ? record.OwnerModId : null;
        }

        private void ReportOnce(RbxInstance instance, string message)
        {
            if (_reported.Add(instance))
            {
                _log?.Invoke(message);
            }
        }

        private static string KindOf(RbxGuiObject gui)
        {
            switch (gui)
            {
                case RbxTextLabel _:
                    return "Label";
                case RbxTextButton _:
                    return "Button";
                case RbxTextBox _:
                    return "TextField";
                case RbxImageLabel _:
                case RbxImageButton _:
                    return "Image";
                case RbxScrollingFrame _:
                    return "ScrollView";
                default:
                    return "VisualElement";
            }
        }
    }
}
