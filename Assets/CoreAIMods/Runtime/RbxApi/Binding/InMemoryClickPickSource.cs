using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;

namespace CoreAI.Mods.Rbx.Binding
{
    /// <summary>
    /// Engine-free <see cref="IClickPickSource"/>: the headless/solo default and test double. There
    /// is no camera or physics world behind it, so it always reports "no hit" — clicks resolve to
    /// nothing until a live <see cref="UnityClickPickSource"/> is wired at composition, mirroring
    /// <see cref="InMemoryCameraRig"/> for the camera seam.
    /// </summary>
    public sealed class InMemoryClickPickSource : IClickPickSource
    {
        public bool TryPick(RbxVector2 screenPositionTopLeft, out InstanceId hitId,
            out double distanceStuds)
        {
            hitId = InstanceId.None;
            distanceStuds = 0d;
            return false;
        }
    }
}
