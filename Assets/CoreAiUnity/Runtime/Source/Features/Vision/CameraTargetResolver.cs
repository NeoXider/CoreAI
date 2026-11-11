using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CoreAI.Vision
{
    /// <summary>A point a camera aims or orbits at, resolved from an object name.</summary>
    public readonly struct CameraTarget
    {
        public CameraTarget(string name, Vector3 point, float radius)
        {
            Name = name;
            Point = point;
            Radius = radius;
        }

        /// <summary>Name of the object the target was resolved from.</summary>
        public string Name { get; }

        /// <summary>World point to aim at: the centre of the object's rendered bounds, else its position.</summary>
        public Vector3 Point { get; }

        /// <summary>Radius of the object's rendered bounds in world units; 0 when it renders nothing.</summary>
        public float Radius { get; }
    }

    /// <summary>
    /// Resolves the object name an agent passes to <c>camera_look</c> (<c>lookAt</c>) or <c>camera_orbit</c>
    /// (<c>target</c>) to a <see cref="CameraTarget"/>. Replace it to map a game's own naming (a Rbx path, an entity
    /// id) to world points; the default is <see cref="SceneCameraTargetResolver"/>.
    /// </summary>
    public interface ICameraTargetResolver
    {
        /// <summary>
        /// Resolves <paramref name="objectName"/>. <paramref name="viewer"/> is the camera asking; its own object is
        /// never a target. Must run on the Unity main thread.
        /// </summary>
        bool TryResolve(string objectName, Camera viewer, out CameraTarget target);
    }

    /// <summary>
    /// Finds an active GameObject in a loaded scene by name (exact match first, then case-insensitive) and aims at
    /// the centre of the bounds of its enabled renderers, children included, so a model built from many parts is
    /// framed as a whole. An object that renders nothing is aimed at by its position.
    /// </summary>
    public sealed class SceneCameraTargetResolver : ICameraTargetResolver
    {
        /// <inheritdoc/>
        public bool TryResolve(string objectName, Camera viewer, out CameraTarget target)
        {
            target = default;
            string trimmed = objectName?.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                return false;
            }

            Transform found = Find(trimmed, viewer, StringComparison.Ordinal) ??
                              Find(trimmed, viewer, StringComparison.OrdinalIgnoreCase);
            if (found == null)
            {
                return false;
            }

            target = Describe(found);
            return true;
        }

        /// <summary>The target an object resolves to: its rendered bounds centre and radius, else its position.</summary>
        public static CameraTarget Describe(Transform transform)
        {
            if (transform == null)
            {
                throw new ArgumentNullException(nameof(transform));
            }

            Renderer[] renderers = transform.GetComponentsInChildren<Renderer>(false);
            bool any = false;
            Bounds bounds = default;
            foreach (Renderer renderer in renderers)
            {
                if (renderer == null || !renderer.enabled)
                {
                    continue;
                }

                if (!any)
                {
                    bounds = renderer.bounds;
                    any = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }

            return any
                ? new CameraTarget(transform.name, bounds.center, bounds.extents.magnitude)
                : new CameraTarget(transform.name, transform.position, 0f);
        }

        private static Transform Find(string objectName, Camera viewer, StringComparison comparison)
        {
            Transform viewerTransform = viewer != null ? viewer.transform : null;
            Transform[] transforms = UnityEngine.Object.FindObjectsByType<Transform>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            foreach (Transform transform in transforms)
            {
                if (transform == null || transform == viewerTransform ||
                    !string.Equals(transform.name, objectName, comparison))
                {
                    continue;
                }

                Scene scene = transform.gameObject.scene;
                if (scene.IsValid() && scene.isLoaded)
                {
                    return transform;
                }
            }

            return null;
        }
    }
}
