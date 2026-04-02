using UnityEngine;

namespace CoreAI.ExampleGame.Arena
{
    [RequireComponent(typeof(CharacterController))]
    public sealed class ArenaPlayerMotor : MonoBehaviour
    {
        [SerializeField] private float moveSpeed = 7f;
        [SerializeField] private float gravity = -25f;

        private CharacterController _cc;
        private float _vy;

        private void Awake()
        {
            _cc = GetComponent<CharacterController>();
        }

        private void Update()
        {
            var cam = Camera.main;
            var forward = cam != null ? cam.transform.forward : Vector3.forward;
            var right = cam != null ? cam.transform.right : Vector3.right;
            forward.y = 0f;
            right.y = 0f;
            forward.Normalize();
            right.Normalize();

            var x = Input.GetAxisRaw("Horizontal");
            var z = Input.GetAxisRaw("Vertical");
            var dir = (forward * z + right * x).normalized;
            var move = dir * (moveSpeed * Time.deltaTime);

            if (_cc.isGrounded && _vy < 0f)
                _vy = -2f;
            _vy += gravity * Time.deltaTime;
            move.y = _vy * Time.deltaTime;
            _cc.Move(move);
        }
    }
}
