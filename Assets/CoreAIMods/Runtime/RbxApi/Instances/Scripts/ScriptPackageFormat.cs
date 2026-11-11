using System;
using System.Collections.Generic;

namespace CoreAI.Mods.Rbx.Instances.Scripts
{
    /// <summary>
    /// Rule R4 of MVP4 step C1: a world is written in the lowest package format that can express it. Only a world whose
    /// saved tree holds a script instance, or one of whose mods declares a context, needs the format that carries script
    /// bindings; every other world keeps the format it was written in before scripts were instances, byte for byte.
    /// </summary>
    public static class ScriptPackageFormat
    {
        /// <summary>
        /// True when <paramref name="savedInstances"/> (the projected, world-owned tree a capture writes) holds a
        /// <c>Script</c>, <c>LocalScript</c> or <c>ModuleScript</c>, or one of <paramref name="manifestContexts"/> (the
        /// <c>Context</c> of every packaged mod manifest) is not empty. A mod's synthetic legacy script is mod-owned
        /// runtime infrastructure, which the projection leaves out, so a world of legacy mods needs no bindings.
        /// </summary>
        public static bool RequiresScriptBindings(IReadOnlyList<InstanceSnapshot> savedInstances,
            IReadOnlyList<string> manifestContexts)
        {
            if (savedInstances == null)
            {
                throw new ArgumentNullException(nameof(savedInstances));
            }

            if (manifestContexts != null)
            {
                for (int index = 0; index < manifestContexts.Count; index++)
                {
                    if (!string.IsNullOrWhiteSpace(manifestContexts[index]))
                    {
                        return true;
                    }
                }
            }

            for (int index = 0; index < savedInstances.Count; index++)
            {
                InstanceSnapshot node = savedInstances[index];
                if (node != null && ScriptClassNames.IsScriptClass(node.ClassName))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
