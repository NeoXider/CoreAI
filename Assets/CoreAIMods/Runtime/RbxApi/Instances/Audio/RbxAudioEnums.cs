using CoreAI.Mods.Rbx.Datatypes;

namespace CoreAI.Mods.Rbx.Instances
{
    /// <summary>Roblox <c>Enum.RollOffMode</c> values (RollOffMode.yaml).</summary>
    public enum RbxRollOffMode
    {
        Inverse = 0,
        Linear = 1,
        LinearSquare = 2,
        InverseTapered = 3
    }

    /// <summary>The enums of the audio, particle and animation classes (MVP17).</summary>
    public static class RbxAudioEnums
    {
        /// <summary>Registers every MVP17 enum the registry does not hold yet.</summary>
        public static void EnsureRegistered(RbxEnumRegistry registry)
        {
            if (registry == null)
            {
                return;
            }

            Add(registry, "RollOffMode", ("Inverse", 0), ("Linear", 1), ("LinearSquare", 2), ("InverseTapered", 3));
        }

        private static void Add(RbxEnumRegistry registry, string name, params (string name, int value)[] items)
        {
            if (!registry.TryGet(name, out _))
            {
                registry.Register(new RbxEnum(name, items));
            }
        }
    }
}
