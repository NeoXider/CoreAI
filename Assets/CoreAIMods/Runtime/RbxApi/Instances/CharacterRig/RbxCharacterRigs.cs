using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances.Scheduling;

namespace CoreAI.Mods.Rbx.Instances.CharacterRig
{
    /// <summary>
    /// Animates every R6 character in the world: each frame, every <c>Model</c> in the Workspace that holds a
    /// <c>Humanoid</c> and a part named <c>Torso</c> gets its Torso, Head, arms and legs posed from the
    /// Humanoid's state and running speed — the local player, another player, a villager a mod builds — with
    /// no code in the mod. A model with other part names is left alone.
    /// </summary>
    /// <remarks>
    /// The joints are implicit: <see cref="RbxR6Rig.Joints"/> carries Roblox's R6 Motor6D values keyed by part
    /// name, because <c>Motor6D</c>, <c>Weld</c> and <c>WeldConstraint</c> are not built yet. Parts are found
    /// by their Roblox names (<see cref="RbxR6Rig.PartNames"/>); the root is the <c>HumanoidRootPart</c>, or the
    /// Torso of a legacy rig without one. A missing limb is skipped, an anchored limb or Torso is left where
    /// it is (as a joint cannot move an anchored part in Roblox), and the root is never written — the motor
    /// owns it.
    /// <para>
    /// WHY posed at <see cref="SchedulerPhase.PreRender"/>: the limbs then follow the root as it is drawn,
    /// after physics, scripts and the NPC brains have moved it this frame. A DEV candidate: Roblox steps
    /// animations after PreAnimation, so a Heartbeat handler there reads this frame's limb pose, and here
    /// it reads last frame's. WHY a write is a pose and not a property write: the port moves the part as
    /// physics would — no Changed, no revision, no replication — so every machine animates its own copy from
    /// the replicated Humanoid state, as Roblox clients do.
    /// </para>
    /// <para>
    /// A frame allocates nothing: each rig keeps its part slots and poser from the moment its Humanoid is
    /// registered, a complete rig is validated by reference and name, and an incomplete one is searched again
    /// only every <see cref="IncompleteRigRecheckSeconds"/>.
    /// </para>
    /// </remarks>
    public sealed class RbxCharacterRigs : IDisposable
    {
        /// <summary>How often a Humanoid's model that lacks a rig part is searched again, in seconds.</summary>
        public const double IncompleteRigRecheckSeconds = 0.25d;

        private readonly InstanceRegistry _registry;
        private readonly ModScheduler _scheduler;
        private readonly IRbxRigPosePort _port;
        private readonly List<Rig> _rigs = new();
        private readonly Dictionary<RbxHumanoid, Rig> _byHumanoid = new();
        private bool _enabled = true;
        private bool _disposed;

        private sealed class Rig
        {
            public Rig(RbxHumanoid humanoid)
            {
                Humanoid = humanoid;
            }

            public readonly RbxHumanoid Humanoid;
            public readonly RbxBasePart[] Parts = new RbxBasePart[RbxR6Rig.PartCount];
            public readonly InstanceId[] DrivenIds = new InstanceId[RbxR6Rig.PartCount];
            public readonly RbxRigPoser Poser = new();
            public double RecheckIn;
        }

        /// <summary>
        /// Starts animating the characters of <paramref name="registry"/>, posing them at every PreRender of
        /// <paramref name="scheduler"/> (null: the host calls <see cref="Step"/> itself) through
        /// <paramref name="port"/>.
        /// </summary>
        public RbxCharacterRigs(InstanceRegistry registry, ModScheduler scheduler, IRbxRigPosePort port)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _port = port ?? throw new ArgumentNullException(nameof(port));
            _scheduler = scheduler;
            _registry.Registered += OnRegistered;
            _registry.Unregistered += OnUnregistered;
            IReadOnlyList<RbxInstance> live = _registry.GetLiveInstances();
            for (int index = 0; index < live.Count; index++)
            {
                if (live[index] is RbxHumanoid humanoid && !humanoid.IsDestroyed)
                {
                    Track(humanoid);
                }
            }

            if (_scheduler != null)
            {
                _scheduler.PhaseReached += OnPhaseReached;
            }
        }

        /// <summary>
        /// Host switch, on by default. Off releases every posed part (an unanchored limb then falls as an
        /// unjointed part does) and poses nothing until it is on again.
        /// </summary>
        public bool Enabled
        {
            get => _enabled;
            set
            {
                if (_enabled == value)
                {
                    return;
                }

                _enabled = value;
                if (!value)
                {
                    ReleaseAll();
                }
            }
        }

        /// <summary>Humanoids being watched, rig or not.</summary>
        public int TrackedCount => _rigs.Count;

        /// <summary>Characters posed by the last <see cref="Step"/>.</summary>
        public int PosedCount { get; private set; }

        /// <summary>The poser of <paramref name="humanoid"/>'s character, for diagnostics and tests.</summary>
        public bool TryGetPoser(RbxHumanoid humanoid, out RbxRigPoser poser)
        {
            if (humanoid != null && _byHumanoid.TryGetValue(humanoid, out Rig rig))
            {
                poser = rig.Poser;
                return true;
            }

            poser = null;
            return false;
        }

        /// <summary>Poses every rig by one frame of <paramref name="deltaSeconds"/> scaled seconds.</summary>
        public void Step(double deltaSeconds)
        {
            PosedCount = 0;
            if (_disposed || !_enabled)
            {
                return;
            }

            double dt = double.IsNaN(deltaSeconds) || deltaSeconds < 0d ? 0d : deltaSeconds;
            RbxInstance workspace = _registry.WorldRoot;
            for (int index = 0; index < _rigs.Count; index++)
            {
                Rig rig = _rigs[index];
                if (TryResolve(rig, workspace, dt))
                {
                    Pose(rig, dt);
                    PosedCount++;
                }
                else
                {
                    Release(rig);
                }
            }
        }

        /// <summary>Stops watching the world and releases every posed part.</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _registry.Registered -= OnRegistered;
            _registry.Unregistered -= OnUnregistered;
            if (_scheduler != null)
            {
                _scheduler.PhaseReached -= OnPhaseReached;
            }

            ReleaseAll();
            _rigs.Clear();
            _byHumanoid.Clear();
        }

        private void OnPhaseReached(SchedulerPhase phase, double deltaSeconds)
        {
            if (phase == SchedulerPhase.PreRender)
            {
                Step(deltaSeconds);
            }
        }

        private void OnRegistered(InstanceRecord record)
        {
            if (record?.Instance is RbxHumanoid humanoid)
            {
                Track(humanoid);
            }
        }

        private void OnUnregistered(InstanceRecord record)
        {
            if (!(record?.Instance is RbxHumanoid humanoid) || !_byHumanoid.TryGetValue(humanoid, out Rig rig))
            {
                return;
            }

            Release(rig);
            _byHumanoid.Remove(humanoid);
            _rigs.Remove(rig);
        }

        private void Track(RbxHumanoid humanoid)
        {
            if (_byHumanoid.ContainsKey(humanoid))
            {
                return;
            }

            Rig rig = new(humanoid);
            _byHumanoid.Add(humanoid, rig);
            _rigs.Add(rig);
        }

        /// <summary>
        /// Brings the rig's part slots up to date and answers whether it can be posed: its Humanoid sits in a
        /// Model inside the Workspace and the model has a Torso.
        /// </summary>
        private static bool TryResolve(Rig rig, RbxInstance workspace, double dt)
        {
            RbxHumanoid humanoid = rig.Humanoid;
            if (humanoid.IsDestroyed || !(humanoid.Parent is RbxModel model) || model.IsDestroyed
                || workspace == null || !model.IsDescendantOf(workspace))
            {
                return false;
            }

            bool complete = true;
            for (int slot = 0; slot < RbxR6Rig.PartCount; slot++)
            {
                RbxBasePart part = rig.Parts[slot];
                if (part != null && IsSlotMember(part, model, slot))
                {
                    continue;
                }

                rig.Parts[slot] = null;
                complete = false;
            }

            if (!complete)
            {
                rig.RecheckIn -= dt;
                if (rig.RecheckIn <= 0d)
                {
                    rig.RecheckIn = IncompleteRigRecheckSeconds;
                    for (int slot = 0; slot < RbxR6Rig.PartCount; slot++)
                    {
                        if (rig.Parts[slot] == null)
                        {
                            rig.Parts[slot] = model.FindFirstChild(RbxR6Rig.PartNames[slot]) as RbxBasePart;
                        }
                    }
                }
            }

            return rig.Parts[(int)RbxR6Part.Torso] != null;
        }

        private static bool IsSlotMember(RbxBasePart part, RbxInstance model, int slot)
        {
            return !part.IsDestroyed && ReferenceEquals(part.Parent, model)
                                     && string.Equals(part.Name, RbxR6Rig.PartNames[slot], StringComparison.Ordinal);
        }

        private void Pose(Rig rig, double dt)
        {
            RbxBasePart torsoPart = rig.Parts[(int)RbxR6Part.Torso];
            RbxBasePart root = rig.Parts[(int)RbxR6Part.HumanoidRootPart];
            bool torsoIsRoot = root == null;
            if (torsoIsRoot)
            {
                root = torsoPart;
            }

            ReleaseVacatedSlots(rig);
            if (!_port.TryGetPose(root.Id, out RbxCFrame rootCFrame, out bool rootAnchored))
            {
                Release(rig);
                return;
            }

            RbxHumanoid humanoid = rig.Humanoid;
            double speed = MeasureSpeed(humanoid);
            // TODO: pass Humanoid.Sit as seated once Seat and Humanoid.Sit/SeatPart land (the character-rig
            // slice in ClassCatalog); until then nothing seats a character and the Sit pose is never chosen.
            RbxRigPoseKind kind = RbxRigPoseSelector.Select(humanoid.GetState(), speed, rootAnchored, false,
                rig.Poser.IsWalking);
            rig.Poser.Step(dt, kind, speed);

            RbxCFrame torso;
            if (torsoIsRoot)
            {
                torso = rootCFrame;
                Undrive(rig, (int)RbxR6Part.Torso);
            }
            else if (_port.IsAnchored(torsoPart.Id))
            {
                Undrive(rig, (int)RbxR6Part.Torso);
                if (!_port.TryGetPose(torsoPart.Id, out torso, out _))
                {
                    torso = rootCFrame * RbxR6Rig.RestOffsets[(int)RbxR6Part.Torso];
                }
            }
            else
            {
                torso = RbxR6Rig.Joints[0].Part1CFrame(rootCFrame, rig.Poser.GetTransform(0));
                Drive(rig, (int)RbxR6Part.Torso);
                _port.SetPose(torsoPart.Id, torso);
            }

            for (int joint = 1; joint < RbxR6Rig.JointCount; joint++)
            {
                int slot = joint + 1;
                RbxBasePart limb = rig.Parts[slot];
                if (limb == null)
                {
                    continue;
                }

                if (_port.IsAnchored(limb.Id))
                {
                    Undrive(rig, slot);
                    continue;
                }

                Drive(rig, slot);
                _port.SetPose(limb.Id, RbxR6Rig.Joints[joint].Part1CFrame(torso, rig.Poser.GetTransform(joint)));
            }
        }

        /// <summary>
        /// The speed <c>Humanoid.Running</c> reports: what the motor measures, or its direction times WalkSpeed
        /// for a motor that cannot measure.
        /// </summary>
        private static double MeasureSpeed(RbxHumanoid humanoid)
        {
            IRbxCharacterMotor motor = humanoid.Motor;
            double? measured = motor.MeasuredSpeed;
            double speed = measured ?? motor.MoveDirection.Magnitude * humanoid.WalkSpeed;
            return double.IsNaN(speed) || speed < 0d ? 0d : speed;
        }

        private void Drive(Rig rig, int slot)
        {
            InstanceId id = rig.Parts[slot].Id;
            if (rig.DrivenIds[slot] == id)
            {
                return;
            }

            Undrive(rig, slot);
            rig.DrivenIds[slot] = id;
            _port.SetDriven(id, true);
        }

        private void Undrive(Rig rig, int slot)
        {
            InstanceId id = rig.DrivenIds[slot];
            if (!id.IsValid)
            {
                return;
            }

            rig.DrivenIds[slot] = InstanceId.None;
            _port.SetDriven(id, false);
        }

        /// <summary>Lets go of a driven part that left its slot (renamed, reparented, destroyed or replaced).</summary>
        private void ReleaseVacatedSlots(Rig rig)
        {
            for (int slot = 0; slot < RbxR6Rig.PartCount; slot++)
            {
                InstanceId driven = rig.DrivenIds[slot];
                if (!driven.IsValid)
                {
                    continue;
                }

                RbxBasePart part = rig.Parts[slot];
                if (part == null || part.Id != driven)
                {
                    Undrive(rig, slot);
                }
            }
        }

        private void Release(Rig rig)
        {
            for (int slot = 0; slot < RbxR6Rig.PartCount; slot++)
            {
                Undrive(rig, slot);
            }
        }

        private void ReleaseAll()
        {
            for (int index = 0; index < _rigs.Count; index++)
            {
                Release(_rigs[index]);
            }
        }
    }
}
