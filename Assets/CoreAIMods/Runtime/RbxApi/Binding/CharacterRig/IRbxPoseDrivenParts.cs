using CoreAI.Mods.Rbx.Instances;

namespace CoreAI.Mods.Rbx.Binding.CharacterRig
{
    /// <summary>
    /// A part sink that can hold a part as a joint holds it: while driven, an unanchored part keeps its
    /// collider and contacts but has no simulation of its own (no gravity, no velocity), and its pose is
    /// whatever the character rig writes. The Unity binder (<c>InstanceGameObjectBinder</c>) implements it with a
    /// kinematic Rigidbody.
    /// </summary>
    /// <remarks>
    /// WHY a separate capability and not a member of <see cref="IPartPropertySink"/>: a sink that simulates
    /// nothing (the headless <see cref="InMemoryPartPropertySink"/>) has nothing to switch, and an external
    /// sink keeps compiling without it.
    /// </remarks>
    public interface IRbxPoseDrivenParts
    {
        /// <summary>Starts (true) or stops (false) holding <paramref name="id"/> for the rig.</summary>
        void SetPoseDriven(InstanceId id, bool driven);

        /// <summary>True while <paramref name="id"/> is held for the rig.</summary>
        bool IsPoseDriven(InstanceId id);
    }
}
