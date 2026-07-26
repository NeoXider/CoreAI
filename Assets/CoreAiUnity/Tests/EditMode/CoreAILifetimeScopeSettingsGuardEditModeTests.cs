using System;
using CoreAI.Composition;
using CoreAI.Infrastructure.Llm;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// EditMode coverage for the fail-fast settings guard on <see cref="CoreAILifetimeScope"/>.
    /// </summary>
    public sealed class CoreAILifetimeScopeSettingsGuardEditModeTests
    {
        [Test]
        public void EnsureSettingsPresent_Null_ThrowsActionableMessage()
        {
            InvalidOperationException ex =
                Assert.Throws<InvalidOperationException>(() => CoreAILifetimeScope.EnsureSettingsPresent(null));

            StringAssert.Contains("CoreAISettings", ex.Message,
                "The failure must name the missing asset so the fix is obvious.");
            StringAssert.Contains("Resources/CoreAISettings", ex.Message,
                "The failure must point at where to add or assign the asset.");
        }

        [Test]
        public void EnsureSettingsPresent_NonNull_DoesNotThrow()
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            try
            {
                Assert.DoesNotThrow(() => CoreAILifetimeScope.EnsureSettingsPresent(settings));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }
    }
}
