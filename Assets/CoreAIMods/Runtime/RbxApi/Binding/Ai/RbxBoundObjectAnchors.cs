using System;
using CoreAI.Ai.Npc;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Spatial;
using UnityEngine;

namespace CoreAI.Mods.Rbx.Binding
{
    /// <summary>
    /// Speech-bubble anchors for agents whose body is an Rbx part: the top of the GameObject the binder created for
    /// the part, in renderer world space, so a bubble stays over a walking villager wherever the world host sits.
    /// A part with no bound object (a headless world) falls back to its stud position through <see cref="RbxSpace"/>.
    /// </summary>
    public static class RbxBoundObjectAnchors
    {
        /// <summary>
        /// The anchor resolver the Lua AI surface takes (<c>CoreAiLuaAiBindings.Attach</c>'s <c>bodyAnchor</c>).
        /// <paramref name="headroomMeters"/> is added above the top of the part's renderer bounds.
        /// </summary>
        public static Func<RbxInstance, NpcPosition?> CreateResolver(IPartPropertySink sink, float headroomMeters = 0.35f)
        {
            if (sink == null)
            {
                throw new ArgumentNullException(nameof(sink));
            }

            InstanceGameObjectBinder binder = sink as InstanceGameObjectBinder;
            return part =>
            {
                if (part == null || part.IsDestroyed)
                {
                    return null;
                }

                if (binder != null && binder.TryGetBoundObject(part.Id, out GameObject bound) && bound != null)
                {
                    Renderer renderer = bound.GetComponentInChildren<Renderer>();
                    Vector3 top = renderer != null
                        ? new Vector3(renderer.bounds.center.x, renderer.bounds.max.y, renderer.bounds.center.z)
                        : bound.transform.position;
                    top.y += headroomMeters;
                    return new NpcPosition(top.x, top.y, top.z);
                }

                RbxVector3 studs = sink.GetLivePositionStuds(part.Id);
                Vector3 world = RbxSpace.ToUnity(studs);
                world.y += headroomMeters + RbxSpace.LengthToUnity(2.5f);
                return new NpcPosition(world.x, world.y, world.z);
            };
        }
    }
}
