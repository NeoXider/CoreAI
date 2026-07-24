using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode
{
    public sealed class QwenDemoSafetyPlayModeTests
    {
        [UnityTest]
        public IEnumerator RequestScopedGuard_AllowsExactlyOneParallelSideEffectAndResetsNextTurn()
        {
            Type guardType = Type.GetType(
                "CoreAI.Demos.QwenDemo.QwenToolTurnGuard, CoreAI.Demos", false);
            if (guardType == null)
            {
                Assert.Ignore("CoreAI.Demos is not present in this project; skipping the Qwen demo guard test.");
                yield break;
            }

            object guard = Activator.CreateInstance(guardType);
            MethodInfo begin = guardType.GetMethod("BeginTurn");
            MethodInfo claim = guardType.GetMethod("TryClaim");
            MethodInfo end = guardType.GetMethod("EndTurn");

            int firstTurn = (int)begin.Invoke(guard, null);
            Task<bool>[] claims = Enumerable.Range(0, 8)
                .Select(_ => Task.Run(() => (bool)claim.Invoke(guard, null)))
                .ToArray();
            Task all = Task.WhenAll(claims);
            while (!all.IsCompleted)
            {
                yield return null;
            }

            Assert.IsFalse(all.IsFaulted, all.Exception?.ToString());
            Assert.AreEqual(1, claims.Count(task => task.Result));
            end.Invoke(guard, new object[] { firstTurn });
            Assert.IsFalse((bool)claim.Invoke(guard, null));

            int secondTurn = (int)begin.Invoke(guard, null);
            Assert.IsTrue((bool)claim.Invoke(guard, null));
            Assert.IsFalse((bool)claim.Invoke(guard, null));
            end.Invoke(guard, new object[] { secondTurn });
        }
    }
}
