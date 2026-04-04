#if !COREAI_NO_LLM
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// Global setup for all LLM PlayMode tests.
    /// </summary>
    public class LlmUnityGlobalSetup
    {
        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            Debug.Log("[GlobalSetup] OneTimeSetUp called");
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            Debug.Log("[GlobalSetup] OneTimeTearDown called");
            SharedLlmUnity.Cleanup();
        }
    }
}
#endif