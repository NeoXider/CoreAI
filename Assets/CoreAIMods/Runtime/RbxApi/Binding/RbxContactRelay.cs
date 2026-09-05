using System;
using CoreAI.Mods.Rbx.Instances;
using UnityEngine;

namespace CoreAI.Mods.Rbx.Binding
{
    /// <summary>
    /// Sits on a bound part's GameObject and reports its collisions back to the binder.
    /// </summary>
    /// <remarks>
    /// WHY a component per part rather than one global listener: Unity delivers collision callbacks
    /// to the colliding objects themselves — there is no scene-wide contact event — so the only way
    /// to hear a contact is to be on the object. The component holds no logic beyond translating the
    /// other collider back into an instance id; every Roblox rule about which signal fires lives on
    /// the engine-free side.
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
            Report(collision, began: true);
        }

        private void OnCollisionExit(Collision collision)
        {
            Report(collision, began: false);
        }

        private void Report(Collision collision, bool began)
        {
            if (_sink == null || collision == null || collision.collider == null)
            {
                return;
            }

            _sink(gameObject, collision.collider.gameObject, began);
        }
    }
}
