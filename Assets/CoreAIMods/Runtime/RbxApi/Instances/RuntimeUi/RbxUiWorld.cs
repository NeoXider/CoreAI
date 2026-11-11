using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Instances.Scheduling;

namespace CoreAI.Mods.Rbx.Instances.RuntimeUi
{
    /// <summary>
    /// The runtime UI state of one Rbx world: which mod owns which screen, the per-mod budgets, the
    /// claims that keep a screen alive across a hot reload, and the design-token overrides each mod
    /// applies to its own screens. Engine-free: it drives an <see cref="IRbxUiBackend"/> and raises
    /// <see cref="RbxError"/>s with <c>UI_*</c> codes (<see cref="RbxUiErrors"/>); the Lua binding only
    /// marshals values.
    /// </summary>
    /// <remarks>
    /// Lifetime rule: a screen lives while a loaded generation of its mod has created it or looked it
    /// up (<see cref="IRbxUiLease"/>). Unload and quarantine end every generation, a reload ends the
    /// outgoing one after the new chunk ran, so a screen the new chunk re-created survives with its
    /// element state and one it did not is destroyed by the next <see cref="Sweep"/>, which runs once
    /// per frame when a scheduler is attached.
    /// </remarks>
    public sealed class RbxUiWorld : IDisposable
    {
        /// <summary>The element kinds <c>CreateElement</c> accepts.</summary>
        public static readonly string[] CreatableKinds =
        {
            "VisualElement", "Label", "Button", "Toggle", "Slider", "TextField", "ProgressBar", "Image",
            "ScrollView", "ListView"
        };

        private const int MaxScreenIdLength = 64;

        private readonly IRbxUiBackend _backend;
        private readonly string _hostIdPrefix;
        private readonly Dictionary<string, ModState> _mods = new(StringComparer.Ordinal);
        private readonly Dictionary<LeaseKey, RbxUiConnectionLease> _leases = new();
        private readonly List<LeaseKey> _deadLeaseScratch = new();
        private readonly List<RbxUiScreenHandle> _sweepScratch = new();
        private readonly List<string> _emptyModScratch = new();
        private readonly RbxScriptSignal _lifetimeSignal = new("UxmlService.ModLifetime");
        private readonly Action<bool> _onTextFocusChanged;
        private readonly Action<SchedulerPhase, double> _onPhase;
        private readonly Action<object[]> _leaseHandler = _ => { };
        private ModScheduler _scheduler;
        private bool _disposed;

        /// <summary>Creates the state of one world.</summary>
        /// <param name="backend">The runtime UI host; null makes every screen call fail with <c>UI_UNAVAILABLE</c>.</param>
        /// <param name="budgets">Per-mod limits; null uses the defaults.</param>
        /// <param name="hostIdPrefix">Prefix of host screen ids, keeping two worlds of one host apart.</param>
        public RbxUiWorld(IRbxUiBackend backend, RbxUiBudgets budgets = null, string hostIdPrefix = null)
        {
            _backend = backend;
            Budgets = budgets ?? new RbxUiBudgets();
            _hostIdPrefix = hostIdPrefix ?? string.Empty;
            TextInputFocusChanged = new RbxScriptSignal("UxmlService.TextInputFocusChanged");
            _onTextFocusChanged = OnTextFocusChanged;
            _onPhase = OnPhase;
            if (_backend != null)
            {
                _backend.TextInputFocusChanged += _onTextFocusChanged;
            }
        }

        /// <summary>The runtime UI host, or null when this game has none.</summary>
        public IRbxUiBackend Backend => _backend;

        /// <summary>The per-mod limits.</summary>
        public RbxUiBudgets Budgets { get; }

        /// <summary>True when a runtime UI host stands behind this world and the world is not disposed.</summary>
        public bool IsAvailable => _backend != null && !_disposed;

        /// <summary>True after <see cref="Dispose"/>.</summary>
        public bool IsDisposed => _disposed;

        /// <summary>Fired with the new value when a runtime UI text field gains or loses keyboard focus.</summary>
        public RbxScriptSignal TextInputFocusChanged { get; }

        /// <summary>True while a runtime UI text field holds keyboard focus.</summary>
        public bool IsTextInputFocused => _backend != null && !_disposed && _backend.IsTextInputFocused;

        /// <summary>True while the pointer is over a pickable element of a runtime screen.</summary>
        public bool IsPointerOverUi => _backend != null && !_disposed && _backend.IsPointerOverUi;

        /// <summary>Live screens of every mod.</summary>
        public int LiveScreenCount
        {
            get
            {
                int count = 0;
                foreach (ModState mod in _mods.Values)
                {
                    count += mod.Order.Count;
                }

                return count;
            }
        }

        /// <summary>
        /// Binds the world to the scheduler of its Rbx world: the signals it hands out deliver through
        /// it, and once per frame (<see cref="SchedulerPhase.PreRender"/>) it resets the write budgets
        /// and sweeps screens nobody claims any more.
        /// </summary>
        public void AttachScheduler(ModScheduler scheduler)
        {
            if (scheduler == null)
            {
                throw new ArgumentNullException(nameof(scheduler));
            }

            if (ReferenceEquals(_scheduler, scheduler))
            {
                return;
            }

            if (_scheduler != null)
            {
                _scheduler.PhaseReached -= _onPhase;
            }

            _scheduler = scheduler;
            _lifetimeSignal.BindScheduler(scheduler);
            TextInputFocusChanged.BindScheduler(scheduler);
            _scheduler.PhaseReached += _onPhase;
        }

        /// <summary>
        /// The caller for a mod generation. The first call per generation opens its lease, a connection
        /// that <paramref name="track"/> records in the mod's connection ledger under that generation.
        /// </summary>
        /// <param name="modId">Owning mod id; null or blank yields the ownerless caller.</param>
        /// <param name="generation">The connection generation of the calling mod context.</param>
        /// <param name="track">Records the lease connection against the mod and generation.</param>
        public RbxUiCaller GetCaller(string modId, int generation, Action<RbxScriptConnection> track)
        {
            if (string.IsNullOrWhiteSpace(modId))
            {
                return default;
            }

            LeaseKey key = new(modId, generation);
            if (_leases.TryGetValue(key, out RbxUiConnectionLease lease) && lease.IsAlive)
            {
                return new RbxUiCaller(modId, lease);
            }

            if (_scheduler == null)
            {
                throw new InvalidOperationException(
                    "RbxUiWorld.GetCaller needs a scheduler; call AttachScheduler first.");
            }

            RbxScriptConnection connection = _lifetimeSignal.Connect(_leaseHandler);
            track?.Invoke(connection);
            lease = new RbxUiConnectionLease(connection);
            _leases[key] = lease;
            return new RbxUiCaller(modId, lease);
        }

        /// <summary>
        /// Creates a screen the caller owns, or, when the caller already owns one under
        /// <paramref name="id"/> (a hot reload re-running the same code), replaces both of its texts so
        /// the state of elements whose names match survives. Either way the caller's generation claims it.
        /// </summary>
        public RbxUiScreenHandle CreateScreen(RbxUiCaller caller, string id, string uxml, string uss,
            int? sortOrder = null, bool? visible = null)
        {
            const string member = "UxmlService:CreateScreen";
            RequireScreens(caller, member);
            string key = ValidateScreenId(id, member);
            CheckText(uxml, member, "the UXML");
            CheckText(uss, member, "the USS");
            ModState mod = GetOrCreateMod(caller.ModId);
            int built = _backend.CountElements(uxml);
            if (mod.ById.TryGetValue(key, out RbxUiScreenHandle existing) && !existing.IsDestroyed)
            {
                RequireElementBudget(mod, built - existing.Port.ElementCount, member);
                RbxUiResult replaced = existing.Port.Replace(uxml, uss);
                ThrowIfFailed(replaced, member);
                existing.AfterRebuild();
                if (sortOrder.HasValue)
                {
                    existing.Port.SetSortOrder(sortOrder.Value);
                }

                if (visible.HasValue)
                {
                    existing.Port.SetVisible(visible.Value);
                }

                existing.Claim(caller.Lease);
                ApplyTokens(mod, existing);
                return existing;
            }

            if (mod.ScreenCount >= Budgets.MaxScreensPerMod)
            {
                throw RbxUiErrors.Create(RbxUiErrors.BudgetScreens,
                    member + " refused '" + key + "': mod '" + caller.ModId + "' already owns "
                    + mod.ScreenCount + " screens (budget " + Budgets.MaxScreensPerMod + ")",
                    "reuse a screen (SetUxml replaces its markup) or Destroy one you no longer show");
            }

            RequireElementBudget(mod, built, member);
            RbxUiResult created = _backend.CreateScreen(_hostIdPrefix + caller.ModId + "/" + key, caller.ModId,
                uxml, uss, sortOrder ?? 0, visible ?? true);
            ThrowIfFailed(created, member);
            if (created.Screen == null)
            {
                throw RbxUiErrors.Create(RbxUiErrors.Unavailable,
                    member + " got no screen back from the runtime UI host",
                    "report this host bug; the runtime UI host must return the screen it created");
            }

            RbxUiScreenHandle handle = new(this, caller.ModId, key, created.Screen);
            mod.ById[key] = handle;
            mod.Order.Add(handle);
            handle.Claim(caller.Lease);
            ApplyTokens(mod, handle);
            return handle;
        }

        /// <summary>The caller's live screen under <paramref name="id"/>, or null; a hit is claimed by the caller's generation.</summary>
        public RbxUiScreenHandle GetScreen(RbxUiCaller caller, string id)
        {
            const string member = "UxmlService:GetScreen";
            RequireScreens(caller, member);
            string key = ValidateScreenId(id, member);
            if (!_mods.TryGetValue(caller.ModId, out ModState mod)
                || !mod.ById.TryGetValue(key, out RbxUiScreenHandle handle) || handle.IsDestroyed)
            {
                return null;
            }

            handle.Claim(caller.Lease);
            return handle;
        }

        /// <summary>Appends the caller's live screens in creation order; each is claimed by the caller's generation.</summary>
        public void GetScreens(RbxUiCaller caller, List<RbxUiScreenHandle> output)
        {
            RequireScreens(caller, "UxmlService:GetScreens");
            if (!_mods.TryGetValue(caller.ModId, out ModState mod))
            {
                return;
            }

            for (int i = 0; i < mod.Order.Count; i++)
            {
                RbxUiScreenHandle handle = mod.Order[i];
                if (!handle.IsDestroyed)
                {
                    handle.Claim(caller.Lease);
                    output.Add(handle);
                }
            }
        }

        /// <summary>
        /// Overrides a design token (<c>--cai-color-accent</c>, ...) on the caller's screens only, present
        /// and future. The global theme belongs to the host and is never changed from here.
        /// </summary>
        public void SetToken(RbxUiCaller caller, string name, string value)
        {
            const string member = "UxmlService:SetToken";
            RequireScreens(caller, member);
            string token = ValidateToken(name, member);
            CheckText(value, member, "the token value");
            if (string.IsNullOrWhiteSpace(value))
            {
                throw RbxUiErrors.Create(RbxUiErrors.InvalidArgument,
                    member + " expects a USS value for '" + token + "'",
                    "pass a value such as \"rgb(80, 200, 255)\" or \"8px\"");
            }

            ModState mod = GetOrCreateMod(caller.ModId);
            mod.Tokens[token] = value;
            for (int i = 0; i < mod.Order.Count; i++)
            {
                RootOf(mod.Order[i])?.SetStyle(token, value);
            }
        }

        /// <summary>The caller's override of a design token, else the host theme's value, else null.</summary>
        public string GetToken(RbxUiCaller caller, string name)
        {
            const string member = "UxmlService:GetToken";
            RequireScreens(caller, member);
            string token = ValidateToken(name, member);
            if (_mods.TryGetValue(caller.ModId, out ModState mod) && mod.Tokens.TryGetValue(token, out string value))
            {
                return value;
            }

            return _backend.GetThemeToken(token);
        }

        /// <summary>Destroys every screen no loaded generation claims any more; returns how many.</summary>
        public int Sweep()
        {
            if (_disposed)
            {
                return 0;
            }

            _sweepScratch.Clear();
            foreach (ModState mod in _mods.Values)
            {
                for (int i = 0; i < mod.Order.Count; i++)
                {
                    if (!mod.Order[i].HasLiveClaim())
                    {
                        _sweepScratch.Add(mod.Order[i]);
                    }
                }
            }

            int destroyed = 0;
            for (int i = 0; i < _sweepScratch.Count; i++)
            {
                _sweepScratch[i].Destroy();
                destroyed++;
            }

            _sweepScratch.Clear();
            PruneLeasesAndMods();
            return destroyed;
        }

        /// <summary>Ends a frame: resets every mod's write budget, then sweeps.</summary>
        public void EndFrame()
        {
            foreach (ModState mod in _mods.Values)
            {
                mod.Writes = 0;
            }

            Sweep();
        }

        /// <summary>Destroys every screen a mod owns, whatever claims them; returns how many.</summary>
        public int DestroyScreensOwnedBy(string modId)
        {
            if (modId == null || !_mods.TryGetValue(modId, out ModState mod))
            {
                return 0;
            }

            _sweepScratch.Clear();
            _sweepScratch.AddRange(mod.Order);
            for (int i = 0; i < _sweepScratch.Count; i++)
            {
                _sweepScratch[i].Destroy();
            }

            int count = _sweepScratch.Count;
            _sweepScratch.Clear();
            return count;
        }

        /// <summary>Live screens a mod owns: its UxmlService screens and the ScreenGuis shown for it.</summary>
        public int CountScreens(string modId)
        {
            return modId != null && _mods.TryGetValue(modId, out ModState mod) ? mod.ScreenCount : 0;
        }

        /// <summary>
        /// Counts a screen another entrance shows for a mod (a Roblox <c>ScreenGui</c>) against the mod's screen
        /// budget, the same budget its UxmlService screens use; raises <c>UI_BUDGET_SCREENS</c> when it is full.
        /// </summary>
        public void ReserveExternalScreen(string modId, string member)
        {
            if (string.IsNullOrWhiteSpace(modId))
            {
                return;
            }

            ModState mod = GetOrCreateMod(modId);
            if (mod.ScreenCount >= Budgets.MaxScreensPerMod)
            {
                throw RbxUiErrors.Create(RbxUiErrors.BudgetScreens,
                    member + " refused: mod '" + modId + "' already shows " + mod.ScreenCount
                    + " screens (budget " + Budgets.MaxScreensPerMod + ")",
                    "show fewer ScreenGuis at once (set Enabled = false keeps one without counting it twice), "
                    + "or Destroy those you no longer need");
            }

            mod.ExternalScreens++;
        }

        /// <summary>Returns a screen reserved with <see cref="ReserveExternalScreen"/>.</summary>
        public void ReleaseExternalScreen(string modId)
        {
            if (modId != null && _mods.TryGetValue(modId, out ModState mod) && mod.ExternalScreens > 0)
            {
                mod.ExternalScreens--;
            }
        }

        /// <summary>
        /// Counts elements another entrance shows for a mod against its element budget; raises
        /// <c>UI_BUDGET_ELEMENTS</c> when they would not fit.
        /// </summary>
        public void ReserveExternalElements(string modId, int count, string member)
        {
            if (string.IsNullOrWhiteSpace(modId) || count <= 0)
            {
                return;
            }

            ModState mod = GetOrCreateMod(modId);
            RequireElementBudget(mod, count, member);
            mod.ExternalElements += count;
        }

        /// <summary>Returns elements reserved with <see cref="ReserveExternalElements"/>.</summary>
        public void ReleaseExternalElements(string modId, int count)
        {
            if (modId != null && count > 0 && _mods.TryGetValue(modId, out ModState mod))
            {
                mod.ExternalElements = Math.Max(0, mod.ExternalElements - count);
            }
        }

        /// <summary>True when the mod's element budget has room for <paramref name="count"/> more.</summary>
        public bool HasElementRoom(string modId, int count)
        {
            if (string.IsNullOrWhiteSpace(modId) || count <= 0)
            {
                return true;
            }

            int current = _mods.TryGetValue(modId, out ModState mod) ? mod.ElementCount() : 0;
            return current + count <= Budgets.MaxElementsPerMod;
        }

        /// <summary>
        /// Charges one element write of another entrance to the mod's per-frame write budget; raises
        /// <c>UI_BUDGET_WRITES</c> when it is spent.
        /// </summary>
        public void ChargeExternalWrite(string modId, string className, string member)
        {
            if (string.IsNullOrWhiteSpace(modId) || !_mods.TryGetValue(modId, out ModState mod))
            {
                return;
            }

            if (mod.Writes >= Budgets.MaxWritesPerFrame)
            {
                throw RbxUiErrors.Create(RbxUiErrors.BudgetWrites,
                    className + "." + member + " refused: mod '" + modId + "' already made " + mod.Writes
                    + " element writes this frame (budget " + Budgets.MaxWritesPerFrame + ")",
                    "write only what changed, or spread the updates over several frames (RunService.Heartbeat)");
            }

            mod.Writes++;
        }

        /// <summary>Authored elements over all screens a mod owns.</summary>
        public int CountElements(string modId)
        {
            return modId != null && _mods.TryGetValue(modId, out ModState mod) ? mod.ElementCount() : 0;
        }

        /// <summary>
        /// The Roblox GUI entrance of this world (ScreenGuis in the local PlayerGui), or null when none is attached;
        /// it shares this world's host and budgets and is disposed with it.
        /// </summary>
        public RbxGuiPresenter GuiPresenter { get; private set; }

        /// <summary>Attaches the Roblox GUI entrance; a second call replaces (and disposes) the first.</summary>
        public void AttachGuiPresenter(RbxGuiPresenter presenter)
        {
            if (ReferenceEquals(GuiPresenter, presenter))
            {
                return;
            }

            GuiPresenter?.Dispose();
            GuiPresenter = presenter;
        }

        /// <summary>Destroys every screen and stops listening to the host and the scheduler.</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            GuiPresenter?.Dispose();
            _sweepScratch.Clear();
            foreach (ModState mod in _mods.Values)
            {
                _sweepScratch.AddRange(mod.Order);
            }

            for (int i = 0; i < _sweepScratch.Count; i++)
            {
                _sweepScratch[i].Destroy();
            }

            _sweepScratch.Clear();
            _disposed = true;
            if (_backend != null)
            {
                _backend.TextInputFocusChanged -= _onTextFocusChanged;
            }

            if (_scheduler != null)
            {
                _scheduler.PhaseReached -= _onPhase;
            }

            foreach (RbxUiConnectionLease lease in _leases.Values)
            {
                lease.Connection.Disconnect();
            }

            _leases.Clear();
            _mods.Clear();
        }

        internal ModScheduler Scheduler => _scheduler;

        internal void ChargeWrite(string modId, string member)
        {
            if (!_mods.TryGetValue(modId, out ModState mod))
            {
                return;
            }

            if (mod.Writes >= Budgets.MaxWritesPerFrame)
            {
                throw RbxUiErrors.Create(RbxUiErrors.BudgetWrites,
                    member + " refused: mod '" + modId + "' already made " + mod.Writes
                    + " element writes this frame (budget " + Budgets.MaxWritesPerFrame + ")",
                    "write only what changed, or spread the updates over several frames (RunService.Heartbeat)");
            }

            mod.Writes++;
        }

        internal void RequireElementBudget(string modId, int additional, string member)
        {
            if (_mods.TryGetValue(modId, out ModState mod))
            {
                RequireElementBudget(mod, additional, member);
            }
        }

        internal void CheckText(string text, string member, string what)
        {
            if (text != null && text.Length > Budgets.MaxTextLength)
            {
                throw RbxUiErrors.Create(RbxUiErrors.TextTooLarge,
                    member + " refused " + what + ": " + text.Length + " characters, at most "
                    + Budgets.MaxTextLength + " per call",
                    "split the interface into several screens or templates, or shorten the text");
            }
        }

        internal void ValidateSelector(string selector, string member)
        {
            if (string.IsNullOrWhiteSpace(selector))
            {
                throw RbxUiErrors.Create(RbxUiErrors.InvalidSelector,
                    member + " expects a selector",
                    "pass a selector such as \"#hotbar > .slot\", \".slot\" or \"Button\"");
            }

            if (!_backend.IsValidSelector(selector, out string error))
            {
                throw RbxUiErrors.Create(RbxUiErrors.InvalidSelector,
                    member + " cannot use '" + selector + "': " + error,
                    "use type, .class, #name, descendant and > child combinators and :hover :active "
                    + ":focus :disabled :checked; attribute selectors, + and ~ are not supported");
            }
        }

        internal static string ValidateKind(string kind, string member)
        {
            string name = kind?.Trim() ?? string.Empty;
            int colon = name.IndexOf(':');
            if (colon >= 0)
            {
                name = name.Substring(colon + 1);
            }

            for (int i = 0; i < CreatableKinds.Length; i++)
            {
                if (string.Equals(CreatableKinds[i], name, StringComparison.Ordinal))
                {
                    return name;
                }
            }

            throw RbxUiErrors.Create(RbxUiErrors.InvalidArgument,
                member + " cannot create an element of kind '" + kind + "'",
                "pass one of " + string.Join(", ", CreatableKinds));
        }

        internal void OnScreenDestroyed(RbxUiScreenHandle handle)
        {
            if (!_mods.TryGetValue(handle.OwnerModId, out ModState mod))
            {
                return;
            }

            if (mod.ById.TryGetValue(handle.Id, out RbxUiScreenHandle current) && ReferenceEquals(current, handle))
            {
                mod.ById.Remove(handle.Id);
            }

            mod.Order.Remove(handle);
        }

        private void RequireScreens(RbxUiCaller caller, string member)
        {
            RequireAvailable(member);
            if (!caller.HasMod)
            {
                throw RbxUiErrors.Create(RbxUiErrors.ModRequired,
                    member + " needs a mod: a screen belongs to the mod that creates it, and a one-off "
                    + "chunk owns nothing that could remove it again",
                    "create and change screens from a loaded mod (manage_mods), not from execute_lua");
            }
        }

        private void RequireAvailable(string member)
        {
            if (_backend == null || _disposed)
            {
                throw RbxUiErrors.NotAvailable(member);
            }

            if (!_backend.IsEnabled)
            {
                throw RbxUiErrors.SwitchedOff(member);
            }
        }

        private void RequireElementBudget(ModState mod, int additional, string member)
        {
            if (additional <= 0)
            {
                return;
            }

            int current = mod.ElementCount();
            if (current + additional > Budgets.MaxElementsPerMod)
            {
                throw RbxUiErrors.Create(RbxUiErrors.BudgetElements,
                    member + " refused: mod '" + mod.ModId + "' would hold " + (current + additional)
                    + " elements over its screens (budget " + Budgets.MaxElementsPerMod + ")",
                    "use a ListView (SetItems) for long lists, or destroy screens you no longer show");
            }
        }

        private static void ThrowIfFailed(RbxUiResult result, string member)
        {
            if (result.Success)
            {
                return;
            }

            string code = string.IsNullOrEmpty(result.ErrorCode) ? RbxUiErrors.InvalidArgument : result.ErrorCode;
            string fix = code == RbxUiErrors.Disabled
                ? "show the information in 3D space instead; the host decides whether mods may draw UI"
                : code == RbxUiErrors.ScreenExists
                    ? "pick another screen id; another world of this game still holds this one"
                    : "read the message and the screen's Diagnostics, then correct the call";
            throw RbxUiErrors.Create(code, member + ": " + result.Error, fix);
        }

        private static string ValidateScreenId(string id, string member)
        {
            string key = id?.Trim() ?? string.Empty;
            if (key.Length == 0 || key.Length > MaxScreenIdLength || key.IndexOf('/') >= 0)
            {
                throw RbxUiErrors.Create(RbxUiErrors.InvalidArgument,
                    member + " expects a screen id of 1 to " + MaxScreenIdLength + " characters without '/', got '"
                    + id + "'",
                    "pass a short name such as \"hud\"; ids are private to your mod");
            }

            return key;
        }

        private static string ValidateToken(string name, string member)
        {
            string token = name?.Trim() ?? string.Empty;
            if (token.Length < 3 || !token.StartsWith("--", StringComparison.Ordinal))
            {
                throw RbxUiErrors.Create(RbxUiErrors.InvalidArgument,
                    member + " expects a design token name starting with --, got '" + name + "'",
                    "pass a custom property name such as \"--cai-color-accent\"");
            }

            return token;
        }

        private static IRbxUiElement RootOf(RbxUiScreenHandle handle)
        {
            return handle.IsDestroyed ? null : handle.Port.Root;
        }

        private static void ApplyTokens(ModState mod, RbxUiScreenHandle handle)
        {
            IRbxUiElement root = RootOf(handle);
            if (root == null)
            {
                return;
            }

            foreach (KeyValuePair<string, string> token in mod.Tokens)
            {
                root.SetStyle(token.Key, token.Value);
            }
        }

        private ModState GetOrCreateMod(string modId)
        {
            if (!_mods.TryGetValue(modId, out ModState mod))
            {
                mod = new ModState(modId);
                _mods[modId] = mod;
            }

            return mod;
        }

        private void PruneLeasesAndMods()
        {
            _deadLeaseScratch.Clear();
            foreach (KeyValuePair<LeaseKey, RbxUiConnectionLease> pair in _leases)
            {
                if (!pair.Value.IsAlive)
                {
                    _deadLeaseScratch.Add(pair.Key);
                }
            }

            for (int i = 0; i < _deadLeaseScratch.Count; i++)
            {
                _leases.Remove(_deadLeaseScratch[i]);
            }

            _deadLeaseScratch.Clear();
            _emptyModScratch.Clear();
            foreach (ModState mod in _mods.Values)
            {
                if (mod.Order.Count == 0 && mod.ExternalScreens == 0 && mod.ExternalElements == 0
                    && !HasLiveLease(mod.ModId))
                {
                    _emptyModScratch.Add(mod.ModId);
                }
            }

            for (int i = 0; i < _emptyModScratch.Count; i++)
            {
                _mods.Remove(_emptyModScratch[i]);
            }

            _emptyModScratch.Clear();
        }

        private bool HasLiveLease(string modId)
        {
            foreach (KeyValuePair<LeaseKey, RbxUiConnectionLease> pair in _leases)
            {
                if (string.Equals(pair.Key.ModId, modId, StringComparison.Ordinal) && pair.Value.IsAlive)
                {
                    return true;
                }
            }

            return false;
        }

        private void OnTextFocusChanged(bool focused)
        {
            TextInputFocusChanged.Fire(focused);
        }

        private void OnPhase(SchedulerPhase phase, double deltaSeconds)
        {
            if (phase == SchedulerPhase.PreRender)
            {
                EndFrame();
            }
        }

        private readonly struct LeaseKey : IEquatable<LeaseKey>
        {
            public LeaseKey(string modId, int generation)
            {
                ModId = modId;
                Generation = generation;
            }

            public string ModId { get; }

            public int Generation { get; }

            public bool Equals(LeaseKey other)
            {
                return Generation == other.Generation && string.Equals(ModId, other.ModId, StringComparison.Ordinal);
            }

            public override bool Equals(object obj)
            {
                return obj is LeaseKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return (StringComparer.Ordinal.GetHashCode(ModId ?? string.Empty) * 397) ^ Generation;
                }
            }
        }

        private sealed class ModState
        {
            public ModState(string modId)
            {
                ModId = modId;
            }

            public string ModId { get; }

            public Dictionary<string, RbxUiScreenHandle> ById { get; } = new(StringComparer.Ordinal);

            public List<RbxUiScreenHandle> Order { get; } = new();

            public Dictionary<string, string> Tokens { get; } = new(StringComparer.Ordinal);

            public int Writes { get; set; }

            /// <summary>Screens another entrance (the Roblox GUI classes) shows for the mod.</summary>
            public int ExternalScreens { get; set; }

            /// <summary>Elements another entrance shows for the mod.</summary>
            public int ExternalElements { get; set; }

            public int ScreenCount => Order.Count + ExternalScreens;

            public int ElementCount()
            {
                int count = ExternalElements;
                for (int i = 0; i < Order.Count; i++)
                {
                    if (!Order[i].IsDestroyed)
                    {
                        count += Order[i].Port.ElementCount;
                    }
                }

                return count;
            }
        }
    }
}
