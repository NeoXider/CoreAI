using System;

namespace CoreAI.Mods.Rbx.Instances.DataStores
{
    /// <summary>
    /// The class descriptors of the data store surface and their registration, the way <c>RbxAiClasses</c> adds the AI
    /// classes: <see cref="EnsureRegistered"/> lets the Lua adapter add them to a catalog that does not have them yet.
    /// The option classes Roblox scripts create with <c>Instance.new</c> are known-unimplemented classes, so their
    /// <c>Instance.new</c> names the rung that brings them instead of saying they do not exist.
    /// </summary>
    public static class RbxDataStoreClasses
    {
        /// <summary>ClassName of the service.</summary>
        public const string Service = "DataStoreService";

        /// <summary>ClassName of the base of every store.</summary>
        public const string GlobalDataStore = "GlobalDataStore";

        /// <summary>ClassName of the stores <c>GetDataStore</c> returns.</summary>
        public const string DataStore = "DataStore";

        /// <summary>ClassName of the second value of <c>GetAsync</c>, <c>UpdateAsync</c> and <c>RemoveAsync</c>.</summary>
        public const string KeyInfo = "DataStoreKeyInfo";

        /// <summary>The scope a store has when <c>GetDataStore</c> names none (mirror default <c>global</c>).</summary>
        public const string DefaultScope = "global";

        /// <summary>The scope of <c>GetGlobalDataStore</c> (mirror: "always uses the scope u").</summary>
        public const string GlobalDataStoreScope = "u";

        /// <summary>The roadmap rung of every member and class of the surface that is not built yet.</summary>
        public const string PlannedRung = "MVP16";

        private const string OptionsWorkaround =
            "leave the options argument out: the store has no cache to bypass and no metadata options yet; return "
            + "metadata as the third value of an UpdateAsync function instead";

        /// <summary>Registers the classes <paramref name="catalog"/> does not know yet; safe to call for every world.</summary>
        public static void EnsureRegistered(ClassCatalog catalog)
        {
            if (catalog == null)
            {
                throw new ArgumentNullException(nameof(catalog));
            }

            if (!catalog.TryGet(Service, out _))
            {
                catalog.Register(new ClassDescriptor(Service, "Instance", false, false, true));
            }

            if (!catalog.TryGet(GlobalDataStore, out _))
            {
                catalog.Register(new ClassDescriptor(GlobalDataStore, "Instance", false, false, false));
            }

            if (!catalog.TryGet(DataStore, out _))
            {
                catalog.Register(new ClassDescriptor(DataStore, GlobalDataStore, false, false, false));
            }

            if (!catalog.TryGet(KeyInfo, out _))
            {
                catalog.Register(new ClassDescriptor(KeyInfo, "Instance", false, false, false));
            }

            RegisterUnimplementedClass(catalog, "DataStoreSetOptions");
            RegisterUnimplementedClass(catalog, "DataStoreIncrementOptions");
            RegisterUnimplementedClass(catalog, "DataStoreGetOptions");
            RegisterUnimplementedClass(catalog, "DataStoreOptions");
        }

        /// <summary>The descriptor of <paramref name="className"/>, registering the surface first when needed.</summary>
        public static ClassDescriptor Descriptor(ClassCatalog catalog, string className)
        {
            EnsureRegistered(catalog);
            catalog.TryGet(className, out ClassDescriptor descriptor);
            return descriptor;
        }

        /// <summary>
        /// True when <paramref name="className"/> is <paramref name="ancestor"/> or inherits from it, by the mirror's
        /// ancestry (the instances are detached, so the catalog of a registry does not answer for them).
        /// </summary>
        public static bool IsA(string className, string ancestor)
        {
            if (string.Equals(ancestor, "Instance", StringComparison.Ordinal)
                || string.Equals(ancestor, "Object", StringComparison.Ordinal)
                || string.Equals(className, ancestor, StringComparison.Ordinal))
            {
                return true;
            }

            return string.Equals(className, DataStore, StringComparison.Ordinal)
                   && string.Equals(ancestor, GlobalDataStore, StringComparison.Ordinal);
        }

        private static void RegisterUnimplementedClass(ClassCatalog catalog, string className)
        {
            if (catalog.TryGet(className, out _) || catalog.TryGetKnownUnimplementedClass(className, out _))
            {
                return;
            }

            catalog.RegisterKnownUnimplementedClasses(
                RbxKnownUnimplementedClassDescriptor.Planned(className, PlannedRung, OptionsWorkaround));
        }
    }
}
