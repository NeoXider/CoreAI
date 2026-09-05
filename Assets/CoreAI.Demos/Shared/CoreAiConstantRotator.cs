using UnityEngine;

namespace CoreAI.Demos.Shared
{
    /// <summary>
    /// Spins a transform at a constant rate, for demo props that need to be seen from every side.
    /// </summary>
    /// <remarks>
    /// WHY CoreAI ships its own three-line rotator: the demos are how someone evaluates the package,
    /// and a demo that pulls a component from a different product means the evaluator either
    /// installs that product or sees a broken scene. CoreAI's demos depend on CoreAI only.
    /// </remarks>
    [AddComponentMenu("CoreAI/Demos/Constant Rotator")]
    public sealed class CoreAiConstantRotator : MonoBehaviour
    {
        [Tooltip("Axis to spin around, in the space selected below.")]
        [SerializeField] private Vector3 _axis = Vector3.up;

        [Tooltip("Degrees per second.")]
        [SerializeField] private float _degreesPerSecond = 18f;

        [Tooltip("Spin in the object's own space rather than the world's.")]
        [SerializeField] private bool _localSpace;

        /// <summary>Configures the spin from a scene builder.</summary>
        public void Configure(Vector3 axis, float degreesPerSecond, bool localSpace)
        {
            _axis = axis == Vector3.zero ? Vector3.up : axis;
            _degreesPerSecond = degreesPerSecond;
            _localSpace = localSpace;
        }

        private void Update()
        {
            transform.Rotate(_axis.normalized, _degreesPerSecond * Time.deltaTime,
                _localSpace ? Space.Self : Space.World);
        }
    }
}
