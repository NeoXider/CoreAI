using System;
using System.Collections.Generic;

namespace CoreAI.Mods.Rbx.Instances
{
    /// <summary>
    /// One class descriptor row: ancestry is data, not C# inheritance depth (roadmap §5.1.7 risk
    /// table) — adding a class is one row plus an optional behavior class via <see cref="Factory"/>.
    /// </summary>
    public sealed class ClassDescriptor
    {
        /// <summary>Roblox ClassName.</summary>
        public string Name { get; }

        /// <summary>Parent ClassName in the IsA hierarchy; null only for "Instance".</summary>
        public string BaseClassName { get; }

        /// <summary>Abstract classes exist only as IsA ancestors; they are never instantiated.</summary>
        public bool IsAbstract { get; }

        /// <summary>Creatable via the script-facing Instance.new path.</summary>
        public bool IsCreatable { get; }

        /// <summary>Resolvable through ServiceProvider.GetService.</summary>
        public bool IsService { get; }

        /// <summary>Optional behavior-class constructor; null uses the plain RbxInstance shape.</summary>
        public Func<ClassDescriptor, RbxInstance> Factory { get; }

        public ClassDescriptor(string name, string baseClassName, bool isAbstract,
            bool isCreatable, bool isService, Func<ClassDescriptor, RbxInstance> factory = null)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            BaseClassName = baseClassName;
            IsAbstract = isAbstract;
            IsCreatable = isCreatable;
            IsService = isService;
            Factory = factory;
        }
    }

    /// <summary>
    /// Data-driven class registry powering IsA and instance creation for the MVP1 class set.
    /// The same catalog later feeds the API manifest generator (§MVP6).
    /// </summary>
    public sealed class ClassCatalog
    {
        private readonly Dictionary<string, ClassDescriptor> _byName =
            new Dictionary<string, ClassDescriptor>(StringComparer.Ordinal);

        public void Register(ClassDescriptor descriptor)
        {
            if (descriptor == null)
            {
                throw new ArgumentNullException(nameof(descriptor));
            }

            if (_byName.ContainsKey(descriptor.Name))
            {
                throw new InvalidOperationException("Class already registered: " + descriptor.Name);
            }

            if (descriptor.BaseClassName != null && !_byName.ContainsKey(descriptor.BaseClassName))
            {
                throw new InvalidOperationException(
                    "Base class must be registered first: " + descriptor.BaseClassName);
            }

            _byName.Add(descriptor.Name, descriptor);
        }

        public bool TryGet(string className, out ClassDescriptor descriptor)
        {
            return _byName.TryGetValue(className, out descriptor);
        }

        public IEnumerable<ClassDescriptor> All => _byName.Values;

        /// <summary>Walks the ancestry chain: true when <paramref name="className"/> is
        /// <paramref name="ancestorClassName"/> or inherits from it.</summary>
        public bool IsA(string className, string ancestorClassName)
        {
            string current = className;
            while (current != null)
            {
                if (string.Equals(current, ancestorClassName, StringComparison.Ordinal))
                {
                    return true;
                }

                current = _byName.TryGetValue(current, out ClassDescriptor descriptor)
                    ? descriptor.BaseClassName
                    : null;
            }

            return false;
        }

        /// <summary>
        /// The MVP1 class set (roadmap §5.1.3): Instance, Folder, Model, Part (geometry-free
        /// placeholder — spatial properties arrive with the property/datatype slice), Workspace,
        /// DataModel, the container services so paths resolve, and Lighting as a structural
        /// service node (its ClockTime/Ambient properties stay absent — the loud stub answers).
        /// </summary>
        public static ClassCatalog CreateMvp1()
        {
            var catalog = new ClassCatalog();
            catalog.Register(new ClassDescriptor("Instance", null, true, false, false));
            catalog.Register(new ClassDescriptor("PVInstance", "Instance", true, false, false));
            catalog.Register(new ClassDescriptor("Folder", "Instance", false, true, false));
            catalog.Register(new ClassDescriptor("Model", "PVInstance", false, true, false));
            catalog.Register(new ClassDescriptor("WorldRoot", "Model", true, false, false));
            catalog.Register(new ClassDescriptor("Workspace", "WorldRoot", false, false, true));
            catalog.Register(new ClassDescriptor("BasePart", "PVInstance", true, false, false));
            catalog.Register(new ClassDescriptor("Part", "BasePart", false, true, false));
            catalog.Register(new ClassDescriptor("ServiceProvider", "Instance", true, false, false));
            catalog.Register(new ClassDescriptor("DataModel", "ServiceProvider", false, false, false,
                descriptor => new RbxDataModel(descriptor)));
            // TODO: MVP-later — Lighting sun/ambient property mapping (ClockTime, Ambient,
            // GeographicLatitude ...) lands with the lighting slice; today it is structure only.
            catalog.Register(new ClassDescriptor("Lighting", "Instance", false, false, true));
            catalog.Register(new ClassDescriptor("ReplicatedStorage", "Instance", false, false, true));
            catalog.Register(new ClassDescriptor("ServerStorage", "Instance", false, false, true));
            catalog.Register(new ClassDescriptor("ServerScriptService", "Instance", false, false, true));
            catalog.Register(new ClassDescriptor("StarterPlayer", "Instance", false, false, true));
            return catalog;
        }
    }
}
