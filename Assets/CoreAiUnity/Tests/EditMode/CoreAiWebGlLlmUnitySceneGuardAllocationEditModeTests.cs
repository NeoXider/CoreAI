#if COREAI_HAS_LLMUNITY
using CoreAI.Composition;
using CoreAI.WebGl;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// The browser player's LLMUnity containment sweep visits EVERY enabled behaviour in the scene on ten
    /// consecutive frames after each scene load and on every rescan after that. Deciding "is this an
    /// LLMUnity behaviour" used to ask <c>Assembly.GetName().Name</c> per component - an AssemblyName plus
    /// a string per component per sweep. The verdict is per assembly and must be free once known.
    /// </summary>
    [Category("WebGL")]
    public sealed class CoreAiWebGlLlmUnitySceneGuardAllocationEditModeTests
    {
        private sealed class HostProbe : MonoBehaviour
        {
        }

        [Test]
        public void RepeatedSweep_OverMixedBehaviours_DoesNotAllocateGcMemory()
        {
            GameObject root = new("CoreAiWebGlLlmUnitySceneGuardAllocationEditModeTests");
            root.SetActive(false);
            try
            {
                LLMUnity.LLM llm = root.AddComponent<LLMUnity.LLM>();
                LLMUnity.LLMAgent agent = root.AddComponent<LLMUnity.LLMAgent>();
                MonoBehaviour[] behaviours = new MonoBehaviour[64];
                behaviours[0] = llm;
                behaviours[1] = agent;
                behaviours[2] = root.AddComponent<CoreAILifetimeScope>();
                for (int i = 3; i < behaviours.Length; i++)
                {
                    behaviours[i] = root.AddComponent<HostProbe>();
                }

                // Warm-up: the first sweep may still have to learn each assembly's verdict.
                Assert.AreEqual(2, CoreAiWebGlLlmUnitySceneGuard.DisableLlmUnityBehaviours(behaviours));
                llm.enabled = true;
                agent.enabled = true;

                int disabled = -1;
                // WHY the braces: `() => disabled = f()` is an EXPRESSION lambda whose value is the
                // int it assigned, so it binds as Func<int>, and the constraint takes a void
                // TestDelegate - NUnit rejected it with "must be a TestDelegate but was Int32". The
                // statement form assigns and yields nothing, which is what was meant all along.
                Assert.That(
                    () => { disabled = CoreAiWebGlLlmUnitySceneGuard.DisableLlmUnityBehaviours(behaviours); },
                    Is.Not.AllocatingGCMemory(),
                    "A rescan over a settled scene must not allocate per component.");

                Assert.AreEqual(2, disabled, "The cached verdict must still contain exactly the LLMUnity behaviours.");
                Assert.IsFalse(llm.enabled);
                Assert.IsFalse(agent.enabled);
                Assert.IsTrue(behaviours[2].enabled);
                Assert.IsTrue(behaviours[3].enabled);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }
    }
}
#endif
