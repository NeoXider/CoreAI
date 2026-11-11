using System;
using System.Collections.Generic;

namespace CoreAI.Mods.Rbx.Instances.RuntimeUi
{
    /// <summary>
    /// One mod-owned screen of the <c>UxmlService</c> surface: its mod-local id, the backend screen, the
    /// claims that keep it alive, and the element handles scripts hold. Every operation checks that
    /// the screen is still alive and charges the owning mod's budgets; the Lua binding checks
    /// ownership through <see cref="RequireAccess"/> first.
    /// </summary>
    public sealed class RbxUiScreenHandle
    {
        private readonly List<IRbxUiLease> _claims = new(2);
        private readonly Dictionary<IRbxUiElement, RbxUiElementHandle> _elements = new();
        private readonly List<IRbxUiElement> _portScratch = new();
        private readonly List<IRbxUiElement> _deadScratch = new();
        private readonly Action _onPortDestroyed;
        private bool _destroyed;

        internal RbxUiScreenHandle(RbxUiWorld world, string ownerModId, string id, IRbxUiScreen port)
        {
            World = world;
            OwnerModId = ownerModId;
            Id = id;
            Port = port;
            Destroying = new RbxScriptSignal("UiScreen.Destroying");
            if (world.Scheduler != null)
            {
                Destroying.BindScheduler(world.Scheduler);
            }

            _onPortDestroyed = OnPortDestroyed;
            port.Destroyed += _onPortDestroyed;
        }

        /// <summary>The world the screen belongs to.</summary>
        public RbxUiWorld World { get; }

        /// <summary>The mod that owns the screen.</summary>
        public string OwnerModId { get; }

        /// <summary>The mod-local screen id.</summary>
        public string Id { get; }

        /// <summary>The backend screen.</summary>
        public IRbxUiScreen Port { get; }

        /// <summary>Fired once when the screen is destroyed, whoever destroyed it.</summary>
        public RbxScriptSignal Destroying { get; }

        /// <summary>True once the screen was destroyed.</summary>
        public bool IsDestroyed => _destroyed || Port.IsDestroyed;

        /// <summary>Element handles currently cached for scripts (detached ones are pruned on rebuild).</summary>
        public int CachedElementCount => _elements.Count;

        /// <summary>Opaque per-binding cache slot (the Lua proxy of this screen).</summary>
        public object BindingProxy { get; set; }

        /// <summary>The owner of <see cref="BindingProxy"/> (the Lua mod context that created it).</summary>
        public object BindingProxyOwner { get; set; }

        /// <summary>
        /// Refuses a caller that does not own the screen (<c>UI_NOT_OWNER</c>) and a destroyed screen
        /// (<c>UI_SCREEN_DESTROYED</c>).
        /// </summary>
        public void RequireAccess(string callerModId, string member)
        {
            RequireAccessAllowDestroyed(callerModId, member);
            RequireAlive(member);
        }

        /// <summary>Refuses a caller that does not own the screen (<c>UI_NOT_OWNER</c>); a destroyed screen passes.</summary>
        public void RequireAccessAllowDestroyed(string callerModId, string member)
        {
            if (!string.Equals(callerModId, OwnerModId, StringComparison.Ordinal))
            {
                throw RbxUiErrors.Create(RbxUiErrors.NotOwner,
                    member + " refused: screen '" + Id + "' belongs to mod '" + OwnerModId + "'",
                    "a mod sees and changes only its own screens; share design tokens or data instead");
            }
        }

        /// <summary>Raises <c>UI_SCREEN_DESTROYED</c> once the screen was destroyed.</summary>
        public void RequireAlive(string member)
        {
            if (IsDestroyed)
            {
                throw RbxUiErrors.Create(RbxUiErrors.ScreenDestroyed,
                    member + " on destroyed screen '" + Id + "'",
                    "create the screen again with UxmlService:CreateScreen");
            }
        }

        /// <summary>Replaces the UXML; elements whose names match keep their state and handles. Returns the reports.</summary>
        public IReadOnlyList<RbxUiDiagnostic> SetUxml(string uxml)
        {
            const string member = "UiScreen:SetUxml";
            RequireAlive(member);
            World.CheckText(uxml, member, "the UXML");
            World.RequireElementBudget(OwnerModId, World.Backend.CountElements(uxml) - Port.ElementCount, member);
            RbxUiResult result = Port.ReplaceUxml(uxml);
            ThrowIfFailed(result, member);
            AfterRebuild();
            return result.Diagnostics;
        }

        /// <summary>Replaces the USS on the same tree. Returns the reports.</summary>
        public IReadOnlyList<RbxUiDiagnostic> SetUss(string uss)
        {
            const string member = "UiScreen:SetUss";
            RequireAlive(member);
            World.CheckText(uss, member, "the USS");
            RbxUiResult result = Port.ReplaceUss(uss);
            ThrowIfFailed(result, member);
            return result.Diagnostics;
        }

        /// <summary>The reports of the last replace plus API reports since.</summary>
        public IReadOnlyList<RbxUiDiagnostic> Diagnostics =>
            IsDestroyed ? Array.Empty<RbxUiDiagnostic>() : Port.Diagnostics;

        /// <summary>True while shown.</summary>
        public bool Visible => !IsDestroyed && Port.IsVisible;

        /// <summary>The draw order.</summary>
        public int SortOrder => IsDestroyed ? 0 : Port.SortOrder;

        /// <summary>Shows or hides the screen.</summary>
        public void SetVisible(bool visible)
        {
            RequireAlive("UiScreen.Visible");
            Port.SetVisible(visible);
        }

        /// <summary>Changes the draw order.</summary>
        public void SetSortOrder(int sortOrder)
        {
            RequireAlive("UiScreen.SortOrder");
            Port.SetSortOrder(sortOrder);
        }

        /// <summary>The screen root element.</summary>
        public RbxUiElementHandle Root
        {
            get
            {
                RequireAlive("UiScreen.Root");
                return Wrap(Port.Root);
            }
        }

        /// <summary>The first element matching a selector, or null.</summary>
        public RbxUiElementHandle Query(string selector)
        {
            const string member = "UiScreen:Query";
            RequireAlive(member);
            World.ValidateSelector(selector, member);
            return Wrap(Port.Query(selector));
        }

        /// <summary>Appends every element matching a selector, in document order.</summary>
        public void QueryAll(string selector, List<RbxUiElementHandle> output)
        {
            const string member = "UiScreen:QueryAll";
            RequireAlive(member);
            World.ValidateSelector(selector, member);
            _portScratch.Clear();
            Port.QueryAll(selector, _portScratch);
            for (int i = 0; i < _portScratch.Count; i++)
            {
                output.Add(Wrap(_portScratch[i]));
            }

            _portScratch.Clear();
        }

        /// <summary>The first element with a name, or null.</summary>
        public RbxUiElementHandle GetElement(string name)
        {
            RequireAlive("UiScreen:GetElement");
            return string.IsNullOrEmpty(name) ? null : Wrap(Port.GetElement(name));
        }

        /// <summary>Creates an element of a kind under <paramref name="parent"/> (the root when null).</summary>
        public RbxUiElementHandle CreateElement(string kind, RbxUiElementHandle parent, string name)
        {
            const string member = "UiScreen:CreateElement";
            RequireAlive(member);
            string type = RbxUiWorld.ValidateKind(kind, member);
            if (parent != null)
            {
                if (!ReferenceEquals(parent.Screen, this))
                {
                    throw RbxUiErrors.Create(RbxUiErrors.InvalidArgument,
                        member + " expects a parent element of screen '" + Id + "'",
                        "pass an element of the same screen, or nil for the screen root");
                }

                parent.RequireAlive(member);
            }

            World.RequireElementBudget(OwnerModId, 1, member);
            World.ChargeWrite(OwnerModId, member);
            IRbxUiElement created = Port.CreateElement(type, parent?.Port, name);
            if (created == null)
            {
                throw RbxUiErrors.Create(RbxUiErrors.UnsupportedOperation,
                    member + " was refused by the runtime UI host (see the screen's Diagnostics)",
                    "check the parent is alive and the screen has room for another element");
            }

            return Wrap(created);
        }

        /// <summary>Destroys the screen; its elements detach and its <see cref="Destroying"/> signal fires.</summary>
        public void Destroy()
        {
            if (_destroyed)
            {
                return;
            }

            if (!Port.IsDestroyed)
            {
                Port.Destroy();
            }

            OnPortDestroyed();
        }

        internal void Claim(IRbxUiLease lease)
        {
            if (lease == null)
            {
                return;
            }

            for (int i = _claims.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(_claims[i], lease))
                {
                    return;
                }

                if (!_claims[i].IsAlive)
                {
                    _claims.RemoveAt(i);
                }
            }

            _claims.Add(lease);
        }

        internal bool HasLiveClaim()
        {
            for (int i = 0; i < _claims.Count; i++)
            {
                if (_claims[i].IsAlive)
                {
                    return true;
                }
            }

            return false;
        }

        internal RbxUiElementHandle Wrap(IRbxUiElement port)
        {
            if (port == null)
            {
                return null;
            }

            if (!_elements.TryGetValue(port, out RbxUiElementHandle handle))
            {
                handle = new RbxUiElementHandle(this, port);
                _elements[port] = handle;
            }

            return handle;
        }

        internal void AfterRebuild()
        {
            _deadScratch.Clear();
            foreach (KeyValuePair<IRbxUiElement, RbxUiElementHandle> pair in _elements)
            {
                if (!pair.Key.IsAlive)
                {
                    _deadScratch.Add(pair.Key);
                }
            }

            for (int i = 0; i < _deadScratch.Count; i++)
            {
                _elements[_deadScratch[i]].Release();
                _elements.Remove(_deadScratch[i]);
            }

            _deadScratch.Clear();
        }

        private void OnPortDestroyed()
        {
            if (_destroyed)
            {
                return;
            }

            _destroyed = true;
            Port.Destroyed -= _onPortDestroyed;
            foreach (RbxUiElementHandle element in _elements.Values)
            {
                element.Release();
            }

            _elements.Clear();
            _claims.Clear();
            World.OnScreenDestroyed(this);
            Destroying.Fire();
        }

        private static void ThrowIfFailed(RbxUiResult result, string member)
        {
            if (result.Success)
            {
                return;
            }

            string code = string.IsNullOrEmpty(result.ErrorCode) ? RbxUiErrors.InvalidArgument : result.ErrorCode;
            throw RbxUiErrors.Create(code, member + ": " + result.Error,
                "read the message and the screen's Diagnostics, then correct the text");
        }
    }
}
