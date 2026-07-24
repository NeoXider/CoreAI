using CoreAI.Hub.UI;
using NUnit.Framework;
using UnityEngine.UIElements;

namespace CoreAI.Tests.EditMode
{
    public sealed class HubWorldStatePageEditModeTests
    {
        [Test]
        public void WorldStatePage_WithoutManager_StillRendersItsContent()
        {
            WorldStateHubPage page = new(null);

            VisualElement content = page.CreatePageContent() as VisualElement;

            Assert.IsNotNull(content, "A null world-state manager must not break the World tab.");
            Label status = content.Q<Label>("coreai-hub-worldstate-status");
            Assert.IsNotNull(status);
            Assert.AreEqual("Has saved state: No", status.text);
        }

        [Test]
        public void WorldStatePage_WithoutManager_ActionButtonsAreNoOps()
        {
            WorldStateHubPage page = new(null);
            VisualElement content = (VisualElement)page.CreatePageContent();

            Assert.IsNotNull(content.Q<Button>("coreai-hub-worldstate-reset"));
            Assert.IsNotNull(content.Q<Button>("coreai-hub-worldstate-save"));
            Assert.DoesNotThrow(() =>
            {
                Invoke(page, "OnResetClicked");
                Invoke(page, "OnSaveClicked");
                page.OnDestroyed();
            });
        }

        private static void Invoke(object target, string method)
        {
            target.GetType()
                .GetMethod(method, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .Invoke(target, null);
        }
    }
}
