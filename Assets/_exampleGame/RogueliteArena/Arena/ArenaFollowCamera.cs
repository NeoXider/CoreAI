using UnityEngine;

namespace CoreAI.ExampleGame.Arena
{
    public sealed class ArenaFollowCamera : MonoBehaviour
    {
        [SerializeField] private Transform target;
        [SerializeField] private Vector3 offset = new Vector3(0f, 4.2f, -7.5f);
        [SerializeField] private float smooth = 8f;

        public void SetTarget(Transform t) => target = t;

        private void LateUpdate()
        {
            if (target == null)
                return;
            var desired = target.position + target.TransformVector(offset);
            transform.position = Vector3.Lerp(transform.position, desired, 1f - Mathf.Exp(-smooth * Time.deltaTime));
            transform.LookAt(target.position + Vector3.up * 1.2f);
        }
    }
}
