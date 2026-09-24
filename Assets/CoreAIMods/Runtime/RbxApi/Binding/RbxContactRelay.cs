using System;
using CoreAI.Mods.Rbx.Instances;
using UnityEngine;

namespace CoreAI.Mods.Rbx.Binding
{
    /// <summary>
    /// Sits on a bound part's GameObject (and on a Cylinder's binder-owned Shape child) and reports
    /// its collisions and trigger overlaps back to the binder.
    /// </summary>
    /// <remarks>
    /// WHY a component per part rather than one global listener: Unity delivers collision callbacks
    /// to the colliding objects themselves — there is no scene-wide contact event — so the only way
    /// to hear a contact is to be on the object. The component holds no logic beyond translating the
    /// other collider back into an instance id; every Roblox rule about which signal fires lives on
    /// the engine-free side.
    /// WHY trigger events too: a CanCollide=false part is a trigger on the Unity side, and Roblox
    /// still fires Touched for it, so an overlap is reported exactly like a collision.
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class RbxContactRelay : MonoBehaviour
    {
        private Action<GameObject, GameObject, bool> _sink;

        /// <summary>Points the relay at the binder that will resolve the two GameObjects.</summary>
        public void Attach(Action<GameObject, GameObject, bool> sink)
        {
            _sink = sink;
        }

        /// <summary>Stops reporting; used when the part is unbound.</summary>
        public void Detach()
        {
            _sink = null;
        }

        private void OnCollisionEnter(Collision collision)
        {
            Report(collision != null ? collision.collider : null, began: true);
        }

        private void OnCollisionExit(Collision collision)
        {
            Report(collision != null ? collision.collider : null, began: false);
        }

        private void OnTriggerEnter(Collider other)
        {
            Report(other, began: true);
        }

        private void OnTriggerExit(Collider other)
        {
            Report(other, began: false);
        }

        private void Report(Collider other, bool began)
        {
            if (_sink == null || other == null)
            {
                return;
            }

            _sink(gameObject, other.gameObject, began);
        }
    }
}
