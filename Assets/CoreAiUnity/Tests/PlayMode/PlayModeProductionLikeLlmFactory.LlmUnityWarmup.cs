using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode
{
    public static partial class PlayModeProductionLikeLlmFactory
    {
        /// <summary>После <see cref="TryCreate"/> для бэкенда LLMUnity — дождаться поднятия модели.</summary>
        public static IEnumerator EnsureLlmUnityModelReady(PlayModeProductionLikeLlmHandle handle)
        {
#if !COREAI_NO_LLM
            if (handle == null || handle.ResolvedBackend != PlayModeProductionLikeLlmBackend.LlmUnity)
                yield break;

            var setupTask = LLMUnity.LLM.WaitUntilModelSetup();
            yield return new WaitUntil(() => setupTask.IsCompleted);
            if (setupTask.IsFaulted)
                Assert.Fail(setupTask.Exception?.GetBaseException().Message ?? "LLM.WaitUntilModelSetup faulted");
            Assert.IsTrue(setupTask.Result, "LLMUnity: model setup failed (см. консоль LLMUnity).");
#else
            yield break;
#endif
        }
    }
}
