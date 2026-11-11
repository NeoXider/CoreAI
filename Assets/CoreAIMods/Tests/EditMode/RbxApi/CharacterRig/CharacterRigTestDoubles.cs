using System.Collections.Generic;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.CharacterRig;

namespace CoreAI.Tests.EditMode.RbxApi.CharacterRig
{
    /// <summary>
    /// An in-memory pose port for the character-rig tests: poses and Anchored flags by id, the parts held
    /// for the rig, and a count of writes. Writing a pose allocates nothing once the part is known.
    /// </summary>
    internal sealed class FakeRigPosePort : IRbxRigPosePort, IRbxCharacterPartSeeder
    {
        private readonly Dictionary<InstanceId, RbxCFrame> _poses = new();
        private readonly Dictionary<InstanceId, bool> _anchored = new();

        public HashSet<InstanceId> Driven { get; } = new();

        public Dictionary<InstanceId, int> Writes { get; } = new();

        public List<(RbxInstance Part, RbxCharacterPartSeed Seed)> Seeds { get; } = new();

        public int TotalWrites { get; private set; }

        public void Place(RbxInstance part, RbxCFrame cframe, bool anchored = false)
        {
            _poses[part.Id] = cframe;
            _anchored[part.Id] = anchored;
        }

        public void SetAnchored(RbxInstance part, bool anchored)
        {
            _anchored[part.Id] = anchored;
        }

        public RbxCFrame PoseOf(RbxInstance part)
        {
            return _poses[part.Id];
        }

        public int WritesOf(RbxInstance part)
        {
            return Writes.TryGetValue(part.Id, out int count) ? count : 0;
        }

        public void Seed(RbxInstance part, in RbxCharacterPartSeed seed)
        {
            Seeds.Add((part, seed));
            Place(part, seed.CFrame, seed.Anchored);
        }

        public bool TryGetPose(InstanceId part, out RbxCFrame cframe, out bool anchored)
        {
            anchored = _anchored.TryGetValue(part, out bool flag) && flag;
            return _poses.TryGetValue(part, out cframe);
        }

        public bool IsAnchored(InstanceId part)
        {
            return _anchored.TryGetValue(part, out bool flag) && flag;
        }

        public void SetPose(InstanceId part, RbxCFrame cframe)
        {
            _poses[part] = cframe;
            Writes[part] = Writes.TryGetValue(part, out int count) ? count + 1 : 1;
            TotalWrites++;
        }

        public void SetDriven(InstanceId part, bool driven)
        {
            if (driven)
            {
                Driven.Add(part);
            }
            else
            {
                Driven.Remove(part);
            }
        }
    }

    /// <summary>A character motor whose speed, grounding and flight the test sets.</summary>
    internal sealed class FakeRigMotor : IRbxCharacterMotor
    {
        public double? Speed { get; set; }

        public RbxVector3 Direction { get; set; } = RbxVector3.Zero;

        public bool Grounded { get; set; } = true;

        public bool Flying { get; private set; }

        public bool LastAutoRotate { get; private set; } = true;

        public RbxVector3 Position => RbxVector3.Zero;

        public RbxVector3 MoveDirection => Direction;

        public double? MeasuredSpeed => Speed;

        public bool IsGrounded => Grounded;

        public bool IsFlying => Flying;

        public void SetWalkSpeed(double studsPerSecond)
        {
        }

        public void Jump(double jumpPower, double jumpHeight, bool useJumpPower)
        {
        }

        public void MoveTo(RbxVector3? targetStuds)
        {
        }

        public bool SetFlying(bool flying)
        {
            Flying = flying;
            return true;
        }

        public void SetAutoRotate(bool autoRotate)
        {
            LastAutoRotate = autoRotate;
        }
    }

    /// <summary>Builds R6-named and other-named character models in a world for the rig tests.</summary>
    internal static class CharacterRigTestWorld
    {
        public static RbxInstance BuildModel(InstanceRegistry registry, string name, params string[] partNames)
        {
            RbxInstance model = registry.Create("Model");
            model.Name = name;
            foreach (string partName in partNames)
            {
                RbxInstance part = registry.Create("Part");
                part.Name = partName;
                part.Parent = model;
            }

            RbxInstance humanoid = registry.Create("Humanoid");
            humanoid.Parent = model;
            model.Parent = registry.WorldRoot;
            return model;
        }

        public static RbxInstance BuildR6(InstanceRegistry registry, FakeRigPosePort port, RbxCFrame root,
            FakeRigMotor motor, bool rootAnchored = false)
        {
            RbxInstance model = BuildModel(registry, "Villager", RbxR6Rig.PartNames);
            for (int index = 0; index < RbxR6Rig.PartCount; index++)
            {
                RbxInstance part = model.FindFirstChild(RbxR6Rig.PartNames[index]);
                port.Place(part, root * RbxR6Rig.RestOffsets[index], index == 0 && rootAnchored);
            }

            Humanoid(model).AttachHost(null, motor, model.FindFirstChild("HumanoidRootPart"));
            return model;
        }

        public static RbxHumanoid Humanoid(RbxInstance model)
        {
            return (RbxHumanoid)model.FindFirstChildOfClass("Humanoid");
        }

        public static RbxInstance Part(RbxInstance model, RbxR6Part part)
        {
            return model.FindFirstChild(RbxR6Rig.NameOf(part));
        }
    }
}
