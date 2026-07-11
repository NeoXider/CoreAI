using UnityEngine;
using UnityEngine.InputSystem;

namespace CoreAI.ExampleGame.ArenaCombat.Infrastructure
{
    [RequireComponent(typeof(CharacterController))]
    public sealed class ArenaPlayerMotor : MonoBehaviour
    {
        [SerializeField]
        private float moveSpeed = 7f;

        [SerializeField]
        private float gravity = -25f;

        private CharacterController _cc;
        private float _vy;

        private void Awake()
        {
            _cc = GetComponent<CharacterController>();
        }

        private void Update()
        {
            Camera cam = Camera.main;
            Vector3 forward = cam != null ? cam.transform.forward : Vector3.forward;
            Vector3 right = cam != null ? cam.transform.right : Vector3.right;
            forward.y = 0f;
            right.y = 0f;
            forward.Normalize();
            right.Normalize();

            Keyboard kb = Keyboard.current;
            float x = 0f;
            float z = 0f;
            if (kb != null)
            {
                if (kb.aKey.isPressed)
                {
                    x = -1f;
                }
                else if (kb.dKey.isPressed)
                {
                    x = 1f;
                }

                if (kb.wKey.isPressed)
                {
                    z = 1f;
                }
                else if (kb.sKey.isPressed)
                {
                    z = -1f;
                }
            }

            Vector3 dir = (forward * z + right * x).normalized;
            Vector3 move = dir * (moveSpeed * Time.deltaTime);

            if (_cc.isGrounded && _vy < 0f)
            {
                _vy = -2f;
            }

            _vy += gravity * Time.deltaTime;
            move.y = _vy * Time.deltaTime;
            _cc.Move(move);
        }
    }
}
