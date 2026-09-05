#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using TMPro;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// The authority demo tells the truth: refused, then allowed, then refused again.
    /// </summary>
    /// <remarks>
    /// WHY this sequence is the test: MVP11 and MVP12 are made of refusals, and a demo of refusals is
    /// exactly the kind that can be faked with a label. Driving the real buttons and reading the real
    /// panel is what separates "the ledger decides" from "the text says it does".
    /// </remarks>
    public sealed class OnlineAuthorityDemoPlayModeTests
    {
        private const string ScenePath =
            "Assets/CoreAI.Demos/OnlineAuthority/OnlineAuthorityDemo.unity";

        [UnityTest]
        public IEnumerator GuestIsRefused_ThenGranted_ThenRefusedAgain()
        {
            LogAssert.ignoreFailingMessages = true;
            EditorSceneManager.LoadSceneInPlayMode(ScenePath,
                new LoadSceneParameters(LoadSceneMode.Single));
            yield return null;
            yield return null;

            Button guestMove = FindButton("Guest: move statue");
            Button grant = FindButton("Host: grant");
            Button revoke = FindButton("Host: revoke");
            Button hostMove = FindButton("Host: move statue");
            Assert.IsNotNull(guestMove, "the demo must let a visitor play the guest");
            Assert.IsNotNull(grant);
            Assert.IsNotNull(revoke);
            Assert.IsNotNull(hostMove);

            guestMove.onClick.Invoke();
            yield return null;
            StringAssert.Contains("refused", ReadStatus(),
                "with no grant the guest must be refused, with the reason shown");

            grant.onClick.Invoke();
            yield return null;
            guestMove.onClick.Invoke();
            yield return null;
            StringAssert.Contains("moved the statue", ReadStatus(),
                "once the host grants write access the same request must succeed");

            revoke.onClick.Invoke();
            yield return null;
            guestMove.onClick.Invoke();
            yield return null;
            StringAssert.Contains("refused", ReadStatus(),
                "revocation takes effect on the next request, not eventually");
        }

        [UnityTest]
        public IEnumerator TheHostNeedsNoGrantOfItsOwn()
        {
            // The host holds every right because its writes never enter the client path — not
            // because it holds a row in the ledger. Pressing its button with the guest ungranted is
            // what makes that visible.
            EditorSceneManager.LoadSceneInPlayMode(ScenePath,
                new LoadSceneParameters(LoadSceneMode.Single));
            yield return null;
            yield return null;

            FindButton("Host: move statue").onClick.Invoke();
            yield return null;

            StringAssert.Contains("no grant needed", ReadStatus());
        }

        private static string ReadStatus()
        {
            List<string> lines = new();
            foreach (TextMeshProUGUI label in Object.FindObjectsByType<TextMeshProUGUI>(
                         FindObjectsSortMode.None))
            {
                lines.Add(label.text);
            }

            return string.Join("\n", lines);
        }

        private static Button FindButton(string caption)
        {
            foreach (Button button in Object.FindObjectsByType<Button>(FindObjectsSortMode.None))
            {
                if (button.name.Contains(caption, System.StringComparison.OrdinalIgnoreCase))
                {
                    return button;
                }
            }

            return null;
        }
    }
}
#endif
