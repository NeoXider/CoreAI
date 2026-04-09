using UnityEngine;
using UnityEngine.EventSystems;

namespace CoreAI.ExampleGame.SymbiosisMode.UI
{
    public class MobileAttackButton : MonoBehaviour, IPointerDownHandler, IPointerUpHandler
    {
        public static bool IsPressed { get; private set; }
        public static bool WasJustPressed { get; private set; }

        private void Update()
        {
            // Reset frame-based flag
            WasJustPressed = false;
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            IsPressed = true;
            WasJustPressed = true;
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            IsPressed = false;
        }

        private void OnDisable()
        {
            IsPressed = false;
            WasJustPressed = false;
        }
    }
}
