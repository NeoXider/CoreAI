using UnityEngine;
using UnityEngine.InputSystem;

namespace CoreAI.ExampleGame.Arena
{
    public sealed class ArenaFollowCamera : MonoBehaviour
    {
        [SerializeField] private Transform target;
        [Header("Orbit")]
        [SerializeField] private float distance = 7.5f;
        [SerializeField] private float height = 2.2f;
        [SerializeField] private float yawDegrees = 0f;
        [SerializeField] private float pitchDegrees = 18f;
        [SerializeField] private float rotationSpeed = 0.12f;
        [SerializeField] private float minPitch = -20f;
        [SerializeField] private float maxPitch = 65f;

        [Header("Smoothing")]
        [SerializeField] private float positionSmooth = 10f;
        [SerializeField] private float lookSmooth = 12f;

        [Header("Input")]
        [Tooltip("Вращать камеру при зажатой ПКМ.")]
        [SerializeField] private bool rotateWhileRightMouseHeld = true;

        public void SetTarget(Transform t) => target = t;

        private void LateUpdate()
        {
            if (target == null)
                return;

            UpdateRotationFromInput();

            pitchDegrees = Mathf.Clamp(pitchDegrees, minPitch, maxPitch);
            var rot = Quaternion.Euler(pitchDegrees, yawDegrees, 0f);
            var pivot = target.position + Vector3.up * height;
            var desiredPos = pivot + rot * (Vector3.back * distance);
            transform.position = Vector3.Lerp(transform.position, desiredPos, 1f - Mathf.Exp(-positionSmooth * Time.deltaTime));

            var desiredRot = Quaternion.LookRotation((pivot - transform.position).normalized, Vector3.up);
            transform.rotation = Quaternion.Slerp(transform.rotation, desiredRot, 1f - Mathf.Exp(-lookSmooth * Time.deltaTime));
        }

        private void UpdateRotationFromInput()
        {
            var mouse = Mouse.current;
            if (mouse == null)
                return;

            if (rotateWhileRightMouseHeld && !mouse.rightButton.isPressed)
                return;

            var d = mouse.delta.ReadValue();
            yawDegrees += d.x * rotationSpeed;
            pitchDegrees -= d.y * rotationSpeed;
        }
    }
}
