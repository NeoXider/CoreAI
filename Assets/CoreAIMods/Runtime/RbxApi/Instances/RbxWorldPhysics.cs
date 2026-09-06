using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Datatypes;

namespace CoreAI.Mods.Rbx.Instances
{
    /// <summary>
    /// A part that can be touched. Carries the two contact signals so they belong to the part the way
    /// the mirror describes them, instead of living in a side table keyed by id.
    /// </summary>
    public sealed class RbxBasePart : RbxInstance
    {
        /// <summary>Constructed by the class catalog for <c>Part</c>.</summary>
        protected internal RbxBasePart(ClassDescriptor descriptor) : base(descriptor)
        {
        }

        /// <summary>Mirror <c>BasePart.Touched(otherPart)</c>.</summary>
        public RbxScriptSignal Touched => GetOrCreateSignal("Touched");

        /// <summary>Mirror <c>BasePart.TouchEnded(otherPart)</c>.</summary>
        public RbxScriptSignal TouchEnded => GetOrCreateSignal("TouchEnded");

        /// <summary>True when a script has asked for either contact signal.</summary>
        /// <remarks>
        /// WHY it matters: contacts are the highest-frequency event a world produces, and relaying
        /// them to a part nobody listens to is pure waste. The relay checks this before it allocates
        /// or fires, so a world full of colliding scenery costs nothing until something subscribes.
        /// </remarks>
        internal bool HasContactListeners =>
            HasSignalConnections("Touched") || HasSignalConnections("TouchEnded");

        /// <summary>Fires <c>Touched</c> with the other part, deferred like every signal.</summary>
        internal void FireTouched(RbxInstance otherPart)
        {
            FireSignal("Touched", otherPart);
        }

        /// <summary>Fires <c>TouchEnded</c> with the other part.</summary>
        internal void FireTouchEnded(RbxInstance otherPart)
        {
            FireSignal("TouchEnded", otherPart);
        }
    }

    /// <summary>
    /// The engine-free half of MVP8's physics slice: <c>workspace:Raycast</c> argument rules,
    /// <c>Workspace.Gravity</c>, and the relay that turns raw contact pairs into
    /// <c>Touched</c>/<c>TouchEnded</c> on both parts.
    /// </summary>
    /// <remarks>
    /// WHY the rules live here rather than in the Unity adapter: every one of them is a Roblox
    /// semantic, not a physics-engine detail — the 15,000-stud cap, the filter meaning descendants,
    /// gravity being read in studs, contacts firing on BOTH parts. Written here they are tested
    /// without a scene and cannot be reimplemented differently by a second backend.
    /// </remarks>
    public sealed class RbxWorldPhysics
    {
        /// <summary>Mirror default: 196.2 studs/s².</summary>
        public const double DefaultGravity = 196.2d;

        /// <summary>Mirror cap: a direction longer than this is refused, not clamped.</summary>
        public const double MaxRayLengthStuds = 15000d;

        private readonly InstanceRegistry _registry;
        private readonly Dictionary<(InstanceId, InstanceId), bool> _openContacts = new();
        private readonly HashSet<InstanceId> _pendingTeleports = new();
        private readonly HashSet<InstanceId> _activeTeleports = new();
        private IRbxPhysicsPort _port = NullRbxPhysicsPort.Instance;
        private double _gravity = DefaultGravity;

        /// <summary>Creates the world-physics facade over a registry.</summary>
        public RbxWorldPhysics(InstanceRegistry registry)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _registry.Unregistered += OnInstanceUnregistered;
        }

        /// <summary>Mirror <c>Workspace.Gravity</c>, in studs per second squared.</summary>
        public double Gravity
        {
            get => _gravity;
            set
            {
                if (double.IsNaN(value) || double.IsInfinity(value))
                {
                    throw RbxError.BadArgument(
                        "Workspace.Gravity must be a finite number",
                        "assign a studs/s² value such as the default 196.2");
                }

                _gravity = value;
                _port.SetGravity(value);
            }
        }

        /// <summary>The port currently backing queries and contacts.</summary>
        public IRbxPhysicsPort Port => _port;

        /// <summary>
        /// Attaches the engine adapter and pushes the current gravity into it.
        /// </summary>
        /// <remarks>
        /// WHY gravity is re-pushed: a world can be scripted before the host finishes wiring physics
        /// (mods load, then the scene binds), and a Gravity written in that window would otherwise be
        /// remembered by Lua and silently absent from the simulation.
        /// </remarks>
        public void AttachPort(IRbxPhysicsPort port)
        {
            IRbxPhysicsPort replacement = port ?? NullRbxPhysicsPort.Instance;
            if (ReferenceEquals(replacement, _port))
            {
                return;
            }

            _port.ContactBegan -= OnContactBegan;
            _port.ContactEnded -= OnContactEnded;
            _openContacts.Clear();
            _pendingTeleports.Clear();
            _activeTeleports.Clear();
            _port = replacement;
            _port.ContactBegan += OnContactBegan;
            _port.ContactEnded += OnContactEnded;
            _port.SetGravity(_gravity);
        }

        /// <summary>
        /// Records that an instance was moved by assignment rather than by simulation, so contacts
        /// it produces in the next physics step are not reported.
        /// </summary>
        /// <remarks>
        /// WHY: the mirror says Touched "will not fire if the CFrame property was changed such that
        /// the part overlaps another part" — it is an event about physical movement, and gameplay
        /// leans on that (a teleporting checkpoint pad must not read as a hit). A physics engine
        /// cannot tell the two apart on its own: teleporting a body into a wall generates exactly
        /// the same contact as falling into it, so the distinction has to be recorded where the move
        /// is made.
        /// <para>
        /// WHY a pending set rather than writing straight into the active one: Lua ticks (and so
        /// every <c>CFrame</c>/<c>Position</c> assignment) run from <c>Update()</c>, which happens
        /// AFTER the fixed step whose contacts it should still see, but BEFORE the fixed step that
        /// follows. A note taken here therefore has to survive until <see cref="BeginPhysicsStep"/>
        /// promotes it, not be visible to contacts already in flight this frame.
        /// </para>
        /// </remarks>
        public void NoteTeleport(InstanceId id)
        {
            _pendingTeleports.Add(id);
        }

        /// <summary>
        /// Opens a physics step: promotes teleports noted since the last step into the set that
        /// suppresses this step's contacts, and forgets the ones from before that.
        /// </summary>
        /// <remarks>
        /// WHY promote here and not just clear: Unity's order for one fixed step is
        /// FixedUpdate (this call) -&gt; simulate -&gt; contact callbacks -&gt; Update. A teleport noted
        /// during the PREVIOUS Update is exactly the one that must suppress the contacts THIS
        /// simulate is about to produce, and must stop suppressing anything once this step's
        /// contacts have been delivered. Clearing <c>_pendingTeleports</c> unconditionally at the
        /// start of every step (the previous, dead-code version of this method) discarded that note
        /// before the simulate step it was meant for ever ran.
        /// </remarks>
        public void BeginPhysicsStep()
        {
            _activeTeleports.Clear();
            foreach (InstanceId id in _pendingTeleports)
            {
                _activeTeleports.Add(id);
            }

            _pendingTeleports.Clear();
        }

        /// <summary>Detaches the adapter and stops relaying contacts.</summary>
        public void DetachPort()
        {
            AttachPort(NullRbxPhysicsPort.Instance);
        }

        /// <summary>
        /// Mirror <c>WorldRoot:Raycast</c>. Returns null on a miss, which the Lua layer renders as nil.
        /// </summary>
        /// <exception cref="RbxError">
        /// The direction is zero-length, non-finite, or longer than <see cref="MaxRayLengthStuds"/>.
        /// </exception>
        public RbxRaycastResult Raycast(RbxVector3 origin, RbxVector3 direction,
            RbxRaycastParams raycastParams)
        {
            RequireFinite(origin, "origin");
            RequireFinite(direction, "direction");

            double length = direction.Magnitude;
            if (length <= 0d)
            {
                throw RbxError.BadArgument(
                    "WorldRoot:Raycast direction must have a length; a zero vector tests nothing",
                    "multiply a unit direction by the range you want to test");
            }

            if (length > MaxRayLengthStuds)
            {
                throw RbxError.BadArgument(
                    "WorldRoot:Raycast direction length " + length.ToString("0.###")
                    + " studs exceeds the maximum of " + MaxRayLengthStuds.ToString("0")
                    + " studs",
                    "shorten the direction vector; its length is the range that gets tested");
            }

            RbxRaycastParams effective = raycastParams ?? new RbxRaycastParams();
            if (!_port.TryRaycast(origin, direction, effective.RespectCanCollide,
                    id => IsEligible(id, effective), out RbxPhysicsRaycastHit hit))
            {
                return null;
            }

            if (!_registry.TryGet(hit.Instance, out RbxInstance instance) || instance.IsDestroyed)
            {
                // WHY not a result with a null instance: a hit on something the tree no longer knows
                // is indistinguishable to a script from a hit on nothing, and RaycastResult.Instance
                // is typed as a BasePart in the mirror. A miss is the honest answer.
                return null;
            }

            return new RbxRaycastResult(instance, hit.Position, hit.Normal,
                hit.Material, hit.Distance);
        }

        private bool IsEligible(InstanceId id, RbxRaycastParams raycastParams)
        {
            return _registry.TryGet(id, out RbxInstance instance)
                   && !instance.IsDestroyed
                   && raycastParams.Accepts(instance);
        }

        private void OnContactBegan(InstanceId first, InstanceId second)
        {
            (InstanceId, InstanceId) key = ContactKey(first, second);
            if (_openContacts.ContainsKey(key))
            {
                // WHY dedupe: one collision between two multi-collider bodies produces several engine
                // contact pairs, and Roblox fires Touched once for the pair, not once per contact
                // point.
                return;
            }

            if (_activeTeleports.Contains(first) || _activeTeleports.Contains(second))
            {
                // WHY the pair is not tracked either: a withheld Touched followed later by a
                // TouchEnded would be a contact that ended without ever having begun, which is
                // harder for a script to reason about than no events at all.
                return;
            }

            _openContacts[key] = true;
            DeliverContact(first, second, began: true);
        }

        private void OnContactEnded(InstanceId first, InstanceId second)
        {
            (InstanceId, InstanceId) key = ContactKey(first, second);
            if (!_openContacts.Remove(key))
            {
                return;
            }

            DeliverContact(first, second, began: false);
        }

        private void DeliverContact(InstanceId first, InstanceId second, bool began)
        {
            if (!_registry.TryGet(first, out RbxInstance firstInstance)
                || !_registry.TryGet(second, out RbxInstance secondInstance))
            {
                return;
            }

            // The mirror is explicit that PartA.Touched fires with PartB and PartB.Touched with
            // PartA — both, not one.
            FireContact(firstInstance as RbxBasePart, secondInstance, began);
            FireContact(secondInstance as RbxBasePart, firstInstance, began);
        }

        private static void FireContact(RbxBasePart part, RbxInstance other, bool began)
        {
            if (part == null || part.IsDestroyed || other == null || !part.HasContactListeners)
            {
                return;
            }

            if (began)
            {
                part.FireTouched(other);
            }
            else
            {
                part.FireTouchEnded(other);
            }
        }

        private static (InstanceId, InstanceId) ContactKey(InstanceId first, InstanceId second)
        {
            return first.Value <= second.Value ? (first, second) : (second, first);
        }

        /// <summary>
        /// Drops any open contact pair that named a just-destroyed instance.
        /// </summary>
        /// <remarks>
        /// WHY: <see cref="_openContacts"/> was previously pruned only on <see cref="OnContactEnded"/>
        /// or <see cref="AttachPort"/>. A part destroyed while still touching another leaves its pair
        /// key resident forever — harmless by itself, but <see cref="InstanceId"/> values are reused
        /// once the id space wraps, and a resident stale key would then dedupe away the next genuine
        /// Touched for the reused id instead of firing it.
        /// </remarks>
        private void OnInstanceUnregistered(InstanceRecord record)
        {
            if (_openContacts.Count == 0)
            {
                return;
            }

            InstanceId destroyedId = record.Id;
            List<(InstanceId, InstanceId)> stale = null;
            foreach ((InstanceId, InstanceId) key in _openContacts.Keys)
            {
                if (key.Item1.Value == destroyedId.Value || key.Item2.Value == destroyedId.Value)
                {
                    stale ??= new List<(InstanceId, InstanceId)>();
                    stale.Add(key);
                }
            }

            if (stale == null)
            {
                return;
            }

            for (int index = 0; index < stale.Count; index++)
            {
                _openContacts.Remove(stale[index]);
            }
        }

        private static void RequireFinite(RbxVector3 vector, string argumentName)
        {
            if (float.IsNaN(vector.X) || float.IsInfinity(vector.X)
                || float.IsNaN(vector.Y) || float.IsInfinity(vector.Y)
                || float.IsNaN(vector.Z) || float.IsInfinity(vector.Z))
            {
                throw RbxError.BadArgument(
                    "WorldRoot:Raycast " + argumentName + " must be finite",
                    "check the vector arithmetic that produced it for a division by zero");
            }
        }
    }
}
