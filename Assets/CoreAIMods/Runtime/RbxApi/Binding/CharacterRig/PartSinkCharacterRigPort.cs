using System;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.CharacterRig;

namespace CoreAI.Mods.Rbx.Binding.CharacterRig
{
    /// <summary>
    /// The character rig's seams over a part-property sink: <see cref="IRbxCharacterPartSeeder"/> for the parts
    /// the character factory builds and <see cref="IRbxRigPosePort"/> for the poses
    /// <see cref="RbxCharacterRigs"/> writes each frame. Works over the Unity binder and the headless sink alike.
    /// </summary>
    /// <remarks>
    /// A pose goes through <see cref="IPartPropertySink.SetCFrame"/>, below the Lua layer, so it fires no
    /// <c>Changed</c>, advances no revision and replicates nothing — the way physics moves a part. A sink that
    /// also implements <see cref="IRbxPoseDrivenParts"/> (the Unity binder) is told which parts the rig holds,
    /// so an unanchored limb does not fall between two poses.
    /// </remarks>
    public sealed class PartSinkCharacterRigPort : IRbxRigPosePort, IRbxCharacterPartSeeder
    {
        private readonly IPartPropertySink _sink;
        private readonly IRbxPoseDrivenParts _drivenParts;

        /// <summary>Adapts <paramref name="sink"/>.</summary>
        public PartSinkCharacterRigPort(IPartPropertySink sink)
        {
            _sink = sink ?? throw new ArgumentNullException(nameof(sink));
            _drivenParts = sink as IRbxPoseDrivenParts;
        }

        /// <inheritdoc />
        public void Seed(RbxInstance part, in RbxCharacterPartSeed seed)
        {
            if (part == null)
            {
                return;
            }

            PartProperties properties = PartProperties.CreateDefault();
            properties.Size = seed.Size;
            properties.CFrame = seed.CFrame;
            if (seed.HasColor)
            {
                properties.Color = seed.Color;
                properties.ColorWasExplicitlySet = true;
            }

            properties.Transparency = seed.Transparency;
            properties.CanCollide = seed.CanCollide;
            properties.Anchored = seed.Anchored;
            _sink.SetPartProperties(part.Id, in properties);
        }

        /// <inheritdoc />
        public bool TryGetPose(InstanceId part, out RbxCFrame cframe, out bool anchored)
        {
            if (!_sink.TryGetPartProperties(part, out PartProperties properties))
            {
                cframe = RbxCFrame.Identity;
                anchored = false;
                return false;
            }

            cframe = properties.CFrame;
            anchored = properties.Anchored;
            return true;
        }

        /// <inheritdoc />
        public bool IsAnchored(InstanceId part)
        {
            return _sink.TryGetPartProperties(part, out PartProperties properties) && properties.Anchored;
        }

        /// <inheritdoc />
        public void SetPose(InstanceId part, RbxCFrame cframe)
        {
            _sink.SetCFrame(part, in cframe);
        }

        /// <inheritdoc />
        public void SetDriven(InstanceId part, bool driven)
        {
            _drivenParts?.SetPoseDriven(part, driven);
        }
    }
}
