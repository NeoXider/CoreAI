using UnityEngine;
using UnityEngine.EventSystems;

namespace CoreAI.ExampleGame.SymbiosisMode.UI
{
    public class MobileAttackButton : MonoBehaviour, IPointerDownHandler, IPointerUpHandler
    {
        public static bool IsPressed { get; private set; }

        // Consume-on-read instead of clearing in Update: script execution order between this
        // button and its reader is not guaranteed, so an Update-based reset could clear the
        // press BEFORE the player script sampled it, silently dropping mobile attacks.
        private static bool _pressLatched;

        public static bool ConsumePress()
        {
            bool pressed = _pressLatched;
            _pressLatched = false;
            return pressed;
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            IsPressed = true;
            _pressLatched = true;
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            IsPressed = false;
        }

        private void OnDisable()
        {
            IsPressed = false;
            _pressLatched = false;
        }
    }
}