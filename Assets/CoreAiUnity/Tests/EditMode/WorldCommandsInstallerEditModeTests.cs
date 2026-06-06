using CoreAI.Composition;
using CoreAI.Infrastructure.World;
using NUnit.Framework;
using UnityEngine;
using VContainer;

namespace CoreAI.Tests.EditMode
{
    public sealed class WorldCommandsInstallerEditModeTests
    {
        [Test]
        public void RegisterWorldCommands_BuildsContainer_WithPrefabRegistryContracts()
        {
            CoreAiPrefabRegistryAsset registry = ScriptableObject.CreateInstance<CoreAiPrefabRegistryAsset>();
            try
            {
                ContainerBuilder builder = new();

                builder.RegisterWorldCommands(registry);

                using IObjectResolver container = builder.Build();
                ICoreAiPrefabRegistry asInterface = container.Resolve<ICoreAiPrefabRegistry>();
                CoreAiPrefabRegistryAsset asConcrete = container.Resolve<CoreAiPrefabRegistryAsset>();

                Assert.AreSame(registry, asInterface);
                Assert.AreSame(registry, asConcrete);
            }
            finally
            {
                Object.DestroyImmediate(registry);
            }
        }
    }
}