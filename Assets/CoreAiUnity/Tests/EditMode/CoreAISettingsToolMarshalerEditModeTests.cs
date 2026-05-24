using CoreAI.Infrastructure.Llm;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Validates <see cref="ICoreAISettings.ToolInvocationMarshaler"/> wiring on the Unity asset.
    /// </summary>
    public sealed class CoreAISettingsToolMarshalerEditModeTests
    {
        [Test]
        public void CoreAISettingsAsset_ToolInvocationMarshaler_ReturnsUnityPlayerLoopMarshaler()
        {
            CoreAISettingsAsset asset = ScriptableObject.CreateInstance<CoreAISettingsAsset>();

            Assert.AreSame(UnityMainThreadLlmAsyncMarshaler.Instance, asset.ToolInvocationMarshaler);
        }
    }
}