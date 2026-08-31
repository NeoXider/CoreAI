using CoreAI.Infrastructure.World;
using CoreAI.Mods.Rbx.Instances;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CoreAI.Mods.Rbx.Binding
{
    /// <summary>
    /// Lazily reconciles the name-addressed CoreAI world with the Rbx instance registry. The first
    /// lookup adopts the existing GameObject as a host-owned Part; later lookups hit the registry.
    /// </summary>
    public sealed class WorldInstanceAdapter : IWorldInstanceAdapter
    {
        private readonly InstanceGameObjectBinder _binder;

        public WorldInstanceAdapter(InstanceGameObjectBinder binder)
        {
            _binder = binder ?? throw new System.ArgumentNullException(nameof(binder));
        }

        public bool TryWrap(InstanceRegistry registry, string worldName, out RbxInstance instance)
        {
            if (registry == null)
            {
                throw new System.ArgumentNullException(nameof(registry));
            }

            if (string.IsNullOrWhiteSpace(worldName))
            {
                instance = null;
                return false;
            }

            GameObject[] rootObjects = SceneManager.GetActiveScene().GetRootGameObjects();
            if (!WorldQuerySceneWalker.TryFindExact(rootObjects, worldName, out GameObject worldObject))
            {
                instance = null;
                return false;
            }

            if (_binder.TryGetInstanceId(worldObject, out InstanceId existingId)
                && registry.TryGet(existingId, out instance))
            {
                return true;
            }

            RbxInstance wrapper = registry.Create("Part");
            wrapper.Name = worldObject.name;
            _binder.AdoptWorldObject(wrapper.Id, worldObject);
            if (registry.WorldRoot != null)
            {
                wrapper.Parent = registry.WorldRoot;
            }

            instance = wrapper;
            return true;
        }
    }
}
