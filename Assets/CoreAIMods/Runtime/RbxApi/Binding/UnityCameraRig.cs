using System;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Spatial;
using UnityEngine;

namespace CoreAI.Mods.Rbx.Binding
{
    /// <summary>
    /// Unity adapter of <see cref="IRobloxCameraRig"/> over one camera Transform, resolved once
    /// at composition (RobloxWorldHost) — no scene searches in hot paths. Pose conversion goes
    /// through RobloxSpace (D2, this file is inside the lint-allowed Binding folder). Follow
    /// resolves the target's backing GameObject through the binder and drives a
    /// <see cref="RobloxCameraFollower"/> on the camera.
    /// </summary>
    public sealed class UnityCameraRig : IRobloxCameraRig
    {
        private readonly Transform _camera;
        private readonly InstanceGameObjectBinder _binder;
        private readonly RobloxCameraFollower _follower;

        /// <summary><paramref name="binder"/> may be null for pose-only rigs; Follow then always
        /// reports the target as missing.</summary>
        public UnityCameraRig(Transform camera, InstanceGameObjectBinder binder = null)
        {
            if (camera == null)
            {
                throw new ArgumentNullException(nameof(camera));
            }

            _camera = camera;
            _binder = binder;
            _follower = camera.GetComponent<RobloxCameraFollower>();
            if (_follower == null)
            {
                _follower = camera.gameObject.AddComponent<RobloxCameraFollower>();
            }

            _follower.enabled = false;
        }

        public RbxCFrame GetCFrame()
        {
            return RobloxSpace.FromUnity(_camera.position, _camera.rotation);
        }

        public void SetCFrame(in RbxCFrame cframe)
        {
            (Vector3 position, Quaternion rotation) = RobloxSpace.ToUnityPose(cframe);
            _camera.SetPositionAndRotation(position, rotation);
            if (_follower.enabled && _follower.Target != null)
            {
                // WHY: while following, a scripted CFrame re-bases the offset — otherwise the
                // next LateUpdate would snap the camera back and the write would look ignored.
                _follower.Offset = position - _follower.Target.position;
            }
        }

        public bool Follow(InstanceId id)
        {
            if (_binder == null || !_binder.TryGetBoundObject(id, out GameObject target))
            {
                return false;
            }

            _follower.Target = target.transform;
            _follower.Offset = _camera.position - target.transform.position;
            _follower.enabled = true;
            return true;
        }

        public void StopFollowing()
        {
            _follower.Target = null;
            _follower.enabled = false;
        }
    }
}
