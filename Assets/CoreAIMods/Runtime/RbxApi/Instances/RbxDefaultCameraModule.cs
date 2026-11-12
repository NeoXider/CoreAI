using System;
using CoreAI.Mods.Rbx.Datatypes;

namespace CoreAI.Mods.Rbx.Instances
{
    /// <summary>Tuning of <see cref="RbxDefaultCameraModule"/>; the defaults are the PlayerModule's.</summary>
    public sealed class RbxCameraModuleOptions
    {
        /// <summary>Subject distance of a fresh camera, studs.</summary>
        public double DefaultZoomDistance { get; set; } = RbxCameraOrbitMath.DefaultZoomDistance;

        /// <summary>Yaw per pixel of pointer movement, degrees.</summary>
        public double YawDegreesPerPixel { get; set; } = 0.5d;

        /// <summary>Pitch per pixel of pointer movement, degrees.</summary>
        public double PitchDegreesPerPixel { get; set; } = 0.5d * 0.77d;

        /// <summary>Yaw per second at full right-thumbstick deflection, degrees.</summary>
        public double StickYawDegreesPerSecond { get; set; } = 240d;

        /// <summary>Pitch per second at full right-thumbstick deflection, degrees.</summary>
        public double StickPitchDegreesPerSecond { get; set; } = 240d * 0.77d;

        /// <summary>Right-thumbstick deflection below which the stick reads as centred.</summary>
        public double StickDeadZone { get; set; } = 0.2d;

        /// <summary>Highest look elevation, degrees; the lowest is its negative.</summary>
        public double MaxPitchDegrees { get; set; } = RbxCameraOrbitMath.MaxPitchDegrees;

        /// <summary>How far short of an obstruction the pulled-in camera stops, studs.</summary>
        public double CollisionClearanceStuds { get; set; } = 0.25d;
    }

    /// <summary>
    /// The PlayerModule's TransparencyController rule: how transparent the local character is drawn for a camera
    /// at a distance from its focus. Fully hidden in first person, fading between 0.5 and 1.25 studs, drawn
    /// beyond; a fade moves at most <see cref="MaxRatePerSecond"/>, while entering and leaving first person
    /// happen at once. Values are rounded to hundredths, so a steady camera writes nothing.
    /// </summary>
    public static class RbxCameraTransparency
    {
        /// <summary>The fastest a fade moves, per second.</summary>
        public const double MaxRatePerSecond = 2.8d;

        /// <summary>The transparency wanted for a camera <paramref name="distance"/> studs from its focus.</summary>
        public static double Target(double distance)
        {
            if (double.IsNaN(distance) || double.IsInfinity(distance))
            {
                return 0d;
            }

            double transparency = distance < 2d ? 1d - (distance - 0.5d) / 1.5d : 0d;
            return transparency < 0.5d ? 0d : Math.Min(1d, transparency);
        }

        /// <summary>The next transparency from the last one, the wanted one and the frame time.</summary>
        public static double Step(double last, bool hasLast, double target, double deltaSeconds)
        {
            double next = target;
            if (hasLast && target < 1d && last < 0.95d && deltaSeconds > 0d && !double.IsInfinity(deltaSeconds))
            {
                double maxDelta = MaxRatePerSecond * deltaSeconds;
                next = last + Math.Max(-maxDelta, Math.Min(maxDelta, target - last));
            }

            next = Math.Round(next, 2, MidpointRounding.AwayFromZero);
            return next < 0d ? 0d : next > 1d ? 1d : next;
        }
    }

    /// <summary>
    /// The default <see cref="IRbxCameraModule"/>: a third-person orbit around the focus with wheel zoom
    /// down to first person. The look turns with the pointer while it is locked or while the right button
    /// is held, and with the right thumbstick; the zoom follows the PlayerModule curve, is clamped to the
    /// player's min and max distances and snaps to first person below one stud; <c>LockFirstPerson</c>
    /// holds first person; an obstruction between the focus and the camera pulls the camera in without
    /// changing the zoom the player chose. Input the host owns (the chat is focused) turns and zooms nothing,
    /// and a free pointer over UI is neither locked nor turns the view, so a click on a button reaches it.
    /// </summary>
    public sealed class RbxDefaultCameraModule : IRbxCameraModule
    {
        private const double DegreesToRadians = Math.PI / 180d;

        private readonly RbxCameraModuleOptions _options;
        private bool _seeded;
        private double _yaw;
        private double _pitch;
        private double _zoom;
        private double _localTransparency;
        private bool _hasLocalTransparency;

        /// <summary>Builds the module with <paramref name="options"/>, or the PlayerModule defaults.</summary>
        public RbxDefaultCameraModule(RbxCameraModuleOptions options = null)
        {
            _options = options ?? new RbxCameraModuleOptions();
            _zoom = _options.DefaultZoomDistance;
        }

        /// <summary>The tuning in effect; changes apply on the next frame.</summary>
        public RbxCameraModuleOptions Options => _options;

        /// <summary>The look yaw, radians (0 looks along -Z).</summary>
        public double Yaw => _yaw;

        /// <summary>The look pitch, radians (positive looks up).</summary>
        public double Pitch => _pitch;

        /// <summary>The zoom the player chose, studs, before the pull-in.</summary>
        public double ZoomDistance => _zoom;

        /// <inheritdoc />
        public void Reset()
        {
            _seeded = false;
            _hasLocalTransparency = false;
            _localTransparency = 0d;
        }

        /// <inheritdoc />
        public RbxCameraModuleOutput Update(in RbxCameraModuleInput input)
        {
            if (!_seeded)
            {
                RbxCameraOrbitMath.YawPitchFromLook(input.CurrentCFrame.LookVector, out _yaw, out _pitch);
                _pitch = RbxCameraOrbitMath.ClampPitch(_pitch, _options.MaxPitchDegrees);
                _seeded = true;
            }

            bool firstPersonBefore = RbxCameraOrbitMath.IsFirstPerson(
                RbxCameraOrbitMath.ResolveZoom(_zoom, input.MinZoomDistance, input.MaxZoomDistance, input.CameraMode));
            // WHY a free pointer over UI is the UI's, in first person too: a browser grants a pointer lock only on
            // the next click, so a lock asked for while the pointer rests on a button takes that very click (the
            // press lands on the button, the release at the centre of the locked view, and the button never fires),
            // and a view that turned with it would move the button away from the pointer.
            bool freePointerOnUi = input.PointerOverUi && !input.PointerLocked;
            if (!input.InputCaptured)
            {
                if (input.PointerLocked || (input.RotateHeld && !input.PointerOverUi)
                                        || (firstPersonBefore && !freePointerOnUi))
                {
                    RbxCameraOrbitMath.ApplyLook(ref _yaw, ref _pitch, input.PointerDelta,
                        _options.YawDegreesPerPixel * DegreesToRadians,
                        _options.PitchDegreesPerPixel * DegreesToRadians,
                        _options.MaxPitchDegrees);
                }

                ApplyStick(input.LookStick, input.DeltaSeconds);
                if (input.Wheel != 0f && !float.IsNaN(input.Wheel) && !input.PointerOverUi)
                {
                    // WHY the sign only: devices report a notch as 1, 3 or 120, and the PlayerModule
                    // reads InputObject.Position.Z, which is the direction alone.
                    _zoom = RbxCameraOrbitMath.StepZoom(_zoom, input.Wheel > 0f ? -1d : 1d);
                }
            }

            _zoom = RbxCameraOrbitMath.ResolveZoom(_zoom, input.MinZoomDistance, input.MaxZoomDistance,
                input.CameraMode);
            bool firstPerson = RbxCameraOrbitMath.IsFirstPerson(_zoom);
            double distance = _zoom;
            if (!firstPerson && input.Occlusion != null)
            {
                RbxVector3 wanted = input.Focus - RbxCameraOrbitMath.LookVector(_yaw, _pitch) * (float)_zoom;
                if (input.Occlusion.TryFindObstruction(input.Focus, wanted, out double hit))
                {
                    distance = RbxCameraOrbitMath.PullIn(_zoom, hit, _options.CollisionClearanceStuds);
                }
            }

            RbxMouseLock mouseLock = RbxMouseLock.Default;
            if (!input.InputCaptured)
            {
                if (firstPerson)
                {
                    if (!freePointerOnUi)
                    {
                        mouseLock = RbxMouseLock.LockCenter;
                    }
                }
                else if (input.RotateHeld && !input.PointerOverUi)
                {
                    mouseLock = RbxMouseLock.LockCurrentPosition;
                }
            }

            _localTransparency = RbxCameraTransparency.Step(_localTransparency, _hasLocalTransparency,
                RbxCameraTransparency.Target(distance), input.DeltaSeconds);
            _hasLocalTransparency = true;
            return new RbxCameraModuleOutput
            {
                HasCFrame = true,
                CFrame = RbxCameraOrbitMath.OrbitCFrame(input.Focus, _yaw, _pitch, distance),
                MouseLock = mouseLock,
                IsFirstPerson = firstPerson,
                ZoomDistance = _zoom,
                Distance = distance,
                LocalTransparency = _localTransparency
            };
        }

        private void ApplyStick(RbxVector2 stick, double deltaSeconds)
        {
            if (!(deltaSeconds > 0d) || double.IsInfinity(deltaSeconds))
            {
                return;
            }

            double x = DeadZone(stick.X);
            double y = DeadZone(stick.Y);
            if (x == 0d && y == 0d)
            {
                return;
            }

            // WHY expressed as a pointer movement: one turning rule for both devices; a stick pushed
            // right turns right and a stick pushed up looks up, so its y enters as a negative pixel y.
            double yawPerSecond = _options.StickYawDegreesPerSecond * DegreesToRadians;
            double pitchPerSecond = _options.StickPitchDegreesPerSecond * DegreesToRadians;
            RbxVector2 asPixels = new((float)(x * deltaSeconds), (float)(-y * deltaSeconds));
            RbxCameraOrbitMath.ApplyLook(ref _yaw, ref _pitch, asPixels, yawPerSecond, pitchPerSecond,
                _options.MaxPitchDegrees);
        }

        private double DeadZone(float axis)
        {
            if (float.IsNaN(axis) || float.IsInfinity(axis))
            {
                return 0d;
            }

            double clamped = Math.Max(-1d, Math.Min(1d, axis));
            return Math.Abs(clamped) < _options.StickDeadZone ? 0d : clamped;
        }
    }
}
