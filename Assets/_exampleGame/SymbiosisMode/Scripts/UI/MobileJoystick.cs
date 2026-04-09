using UnityEngine;
using UnityEngine.EventSystems;

namespace CoreAI.ExampleGame.SymbiosisMode.UI
{
    public class MobileJoystick : MonoBehaviour, IPointerDownHandler, IDragHandler, IPointerUpHandler
    {
        public static Vector2 InputVector { get; private set; }

        public RectTransform backgroundWindow;
        public RectTransform handle;

        private Vector2 _joystickPosition = Vector2.zero;
        private float _bgRadius;

        private void Start()
        {
            if (backgroundWindow != null)
                _bgRadius = backgroundWindow.sizeDelta.x / 2f;
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            OnDrag(eventData);
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (backgroundWindow == null || handle == null) return;

            Vector2 position;
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(backgroundWindow, eventData.position, eventData.pressEventCamera, out position))
            {
                // Find distance from visual center, regardless of pivot
                Vector2 pivotOffset = new Vector2((0.5f - backgroundWindow.pivot.x) * backgroundWindow.rect.width, 
                                                  (0.5f - backgroundWindow.pivot.y) * backgroundWindow.rect.height);
                
                position -= pivotOffset; // Center is now (0,0)
                
                position.x = (position.x / _bgRadius);
                position.y = (position.y / _bgRadius);

                InputVector = new Vector2(position.x, position.y);
                InputVector = (InputVector.magnitude > 1.0f) ? InputVector.normalized : InputVector;

                // Move handle visually from the center
                handle.localPosition = (Vector3)(InputVector * _bgRadius + pivotOffset);
            }
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            InputVector = Vector2.zero;
            if (handle != null && backgroundWindow != null)
            {
                Vector2 pivotOffset = new Vector2((0.5f - backgroundWindow.pivot.x) * backgroundWindow.rect.width, 
                                                  (0.5f - backgroundWindow.pivot.y) * backgroundWindow.rect.height);
                handle.localPosition = (Vector3)pivotOffset;
            }
        }

        private void OnDisable()
        {
            InputVector = Vector2.zero;
        }
    }
}
