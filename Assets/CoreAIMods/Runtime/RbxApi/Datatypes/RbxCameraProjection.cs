using System;

namespace CoreAI.Mods.Rbx.Datatypes
{
    /// <summary>
    /// Engine-free perspective maths behind the Roblox camera queries: <c>Camera:ViewportPointToRay</c>,
    /// <c>ScreenPointToRay</c>, <c>WorldToViewportPoint</c> and <c>WorldToScreenPoint</c>. Viewport points
    /// are pixels with the origin at the top-left and y growing downward; the field of view is vertical,
    /// in degrees, and the horizontal extent follows the viewport's aspect ratio, as the mirror describes.
    /// Depth is measured along the camera's look axis, so a point projected from a ray origin built at
    /// depth <c>d</c> reports <c>d</c> back.
    /// </summary>
    public static class RbxCameraProjection
    {
        /// <summary>Mirror default of <c>Camera.FieldOfView</c>, degrees.</summary>
        public const double DefaultFieldOfView = 70d;

        /// <summary>Smallest <c>Camera.FieldOfView</c> the mirror allows, degrees.</summary>
        public const double MinFieldOfView = 1d;

        /// <summary>Largest <c>Camera.FieldOfView</c> the mirror allows, degrees.</summary>
        public const double MaxFieldOfView = 120d;

        private const double MinimumDepth = 1e-6d;

        /// <summary>
        /// Clamps a field of view to the mirror's [1, 120] degrees; a non-finite value falls back to
        /// <paramref name="fallback"/>.
        /// </summary>
        public static double ClampFieldOfView(double degrees, double fallback = DefaultFieldOfView)
        {
            if (double.IsNaN(degrees) || double.IsInfinity(degrees))
            {
                return fallback;
            }

            return Math.Max(MinFieldOfView, Math.Min(MaxFieldOfView, degrees));
        }

        /// <summary>
        /// Mirror <c>Camera:ViewportPointToRay(x, y, depth)</c>: a unit ray through the viewport pixel, whose
        /// origin sits <paramref name="depth"/> studs in front of the camera along its look axis.
        /// </summary>
        public static RbxRay ViewportPointToRay(in RbxCFrame camera, double fieldOfViewDegrees,
            RbxVector2 viewportSize, double x, double y, double depth = 0d)
        {
            RbxVector3 local = ViewDirection(fieldOfViewDegrees, viewportSize, x, y);
            RbxVector3 direction = camera.VectorToWorldSpace(local).Unit;
            float safeDepth = double.IsNaN(depth) || double.IsInfinity(depth) ? 0f : (float)depth;
            RbxVector3 origin = camera.PointToWorldSpace(local * safeDepth);
            return new RbxRay(origin, direction);
        }

        /// <summary>
        /// Mirror <c>Camera:ScreenPointToRay(x, y, depth)</c>: the viewport ray of a screen point, whose
        /// coordinates start below the GUI inset.
        /// </summary>
        public static RbxRay ScreenPointToRay(in RbxCFrame camera, double fieldOfViewDegrees,
            RbxVector2 viewportSize, RbxVector2 guiInset, double x, double y, double depth = 0d)
        {
            return ViewportPointToRay(camera, fieldOfViewDegrees, viewportSize,
                x + guiInset.X, y + guiInset.Y, depth);
        }

        /// <summary>
        /// Mirror <c>Camera:WorldToViewportPoint(worldPoint)</c>: pixels from the viewport's top-left in X
        /// and Y, depth in front of the camera in Z, and whether the point is inside the viewport and in
        /// front of the camera. No raycast is made, so an obscured point still reports on screen.
        /// </summary>
        public static RbxVector3 WorldToViewportPoint(in RbxCFrame camera, double fieldOfViewDegrees,
            RbxVector2 viewportSize, RbxVector3 worldPoint, out bool onScreen)
        {
            RbxVector2 viewport = SanitizeViewport(viewportSize);
            double tanHalf = TanHalf(fieldOfViewDegrees);
            double aspect = viewport.X / (double)viewport.Y;
            RbxVector3 local = camera.PointToObjectSpace(worldPoint);
            double depth = -local.Z;
            double divisor = Math.Abs(depth) < MinimumDepth ? (depth < 0d ? -MinimumDepth : MinimumDepth) : depth;
            double ndcX = local.X / (divisor * tanHalf * aspect);
            double ndcY = local.Y / (divisor * tanHalf);
            double x = (ndcX + 1d) * 0.5d * viewport.X;
            double y = (1d - ndcY) * 0.5d * viewport.Y;
            onScreen = depth > 0d && x >= 0d && x <= viewport.X && y >= 0d && y <= viewport.Y;
            return new RbxVector3((float)x, (float)y, (float)depth);
        }

        /// <summary>
        /// Mirror <c>Camera:WorldToScreenPoint(worldPoint)</c>: <see cref="WorldToViewportPoint"/> measured
        /// from below the GUI inset.
        /// </summary>
        public static RbxVector3 WorldToScreenPoint(in RbxCFrame camera, double fieldOfViewDegrees,
            RbxVector2 viewportSize, RbxVector2 guiInset, RbxVector3 worldPoint, out bool onScreen)
        {
            RbxVector3 viewportPoint = WorldToViewportPoint(camera, fieldOfViewDegrees, viewportSize,
                worldPoint, out onScreen);
            return new RbxVector3(viewportPoint.X - guiInset.X, viewportPoint.Y - guiInset.Y, viewportPoint.Z);
        }

        /// <summary>A viewport with a non-positive or non-finite side reads as Roblox's unset <c>(1, 1)</c>.</summary>
        public static RbxVector2 SanitizeViewport(RbxVector2 viewportSize)
        {
            float width = IsPositive(viewportSize.X) ? viewportSize.X : 1f;
            float height = IsPositive(viewportSize.Y) ? viewportSize.Y : 1f;
            return new RbxVector2(width, height);
        }

        /// <summary>The camera-local direction through a viewport pixel, with Z = -1 (Roblox looks along -Z).</summary>
        private static RbxVector3 ViewDirection(double fieldOfViewDegrees, RbxVector2 viewportSize,
            double x, double y)
        {
            RbxVector2 viewport = SanitizeViewport(viewportSize);
            double tanHalf = TanHalf(fieldOfViewDegrees);
            double aspect = viewport.X / (double)viewport.Y;
            double ndcX = 2d * x / viewport.X - 1d;
            double ndcY = 1d - 2d * y / viewport.Y;
            return new RbxVector3((float)(ndcX * tanHalf * aspect), (float)(ndcY * tanHalf), -1f);
        }

        private static double TanHalf(double fieldOfViewDegrees)
        {
            double fov = ClampFieldOfView(fieldOfViewDegrees);
            return Math.Tan(fov * Math.PI / 360d);
        }

        private static bool IsPositive(float value)
        {
            return value > 0f && !float.IsInfinity(value);
        }
    }
}
