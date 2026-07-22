using System;
using System.Globalization;

namespace CoreAI.Mods.Rbx.Datatypes
{
    /// <summary>
    /// Pure-spec Roblox CFrame: position + right-handed rotation matrix, LookVector = -Z
    /// (ROBLOX_API_ROADMAP.md D1, LOCKED). Stored row-major; the matrix columns are the
    /// world-space axis vectors: column 0 = RightVector, column 1 = UpVector,
    /// column 2 = ZVector (so LookVector = -ZVector). World point = R * local + Position.
    /// No Unity types appear here — conversion happens only in the RobloxSpace adapter.
    /// </summary>
    public readonly struct RbxCFrame : IEquatable<RbxCFrame>
    {
        // WHY: row-major fields named after Roblox's GetComponents order so goldens map 1:1.
        private readonly float _x, _y, _z;
        private readonly float _r00, _r01, _r02;
        private readonly float _r10, _r11, _r12;
        private readonly float _r20, _r21, _r22;

        public RbxCFrame(
            float x, float y, float z,
            float r00, float r01, float r02,
            float r10, float r11, float r12,
            float r20, float r21, float r22)
        {
            _x = x; _y = y; _z = z;
            _r00 = r00; _r01 = r01; _r02 = r02;
            _r10 = r10; _r11 = r11; _r12 = r12;
            _r20 = r20; _r21 = r21; _r22 = r22;
        }

        // ---- Construction ------------------------------------------------------------

        public static RbxCFrame Identity => new RbxCFrame(
            0f, 0f, 0f,
            1f, 0f, 0f,
            0f, 1f, 0f,
            0f, 0f, 1f);

        public static RbxCFrame FromPosition(float x, float y, float z) => new RbxCFrame(
            x, y, z,
            1f, 0f, 0f,
            0f, 1f, 0f,
            0f, 0f, 1f);

        public static RbxCFrame FromPosition(RbxVector3 pos) => FromPosition(pos.X, pos.Y, pos.Z);

        /// <summary>
        /// CFrame.new(x, y, z, qX, qY, qZ, qW) — position + quaternion; non-unit quaternions
        /// are normalized (Roblox parity).
        /// </summary>
        public static RbxCFrame FromQuaternion(float x, float y, float z, float qx, float qy, float qz, float qw)
        {
            float m = MathF.Sqrt(qx * qx + qy * qy + qz * qz + qw * qw);
            qx /= m; qy /= m; qz /= m; qw /= m;

            return new RbxCFrame(
                x, y, z,
                1f - 2f * (qy * qy + qz * qz), 2f * (qx * qy - qz * qw), 2f * (qx * qz + qy * qw),
                2f * (qx * qy + qz * qw), 1f - 2f * (qx * qx + qz * qz), 2f * (qy * qz - qx * qw),
                2f * (qx * qz - qy * qw), 2f * (qy * qz + qx * qw), 1f - 2f * (qx * qx + qy * qy));
        }

        /// <summary>
        /// CFrame.lookAt(at, lookAt, up = Vector3.yAxis). Degenerate case per the official docs:
        /// when the look direction is parallel to up, the up vector switches to the X axis.
        /// </summary>
        public static RbxCFrame LookAt(RbxVector3 at, RbxVector3 lookAt, RbxVector3? up = null)
        {
            return LookAlong(at, lookAt - at, up);
        }

        /// <summary>CFrame.lookAlong(at, direction, up) — equivalent to lookAt(at, at + direction).</summary>
        public static RbxCFrame LookAlong(RbxVector3 at, RbxVector3 direction, RbxVector3? up = null)
        {
            RbxVector3 f = direction.Unit;
            RbxVector3 upVec = up ?? RbxVector3.YAxis;

            RbxVector3 xCross = f.Cross(upVec);
            if (xCross.Magnitude < 1e-6f)
            {
                // WHY: documented Roblox fallback — looking straight up/down switches up to +X.
                upVec = RbxVector3.XAxis;
                xCross = f.Cross(upVec);
                if (xCross.Magnitude < 1e-6f)
                {
                    upVec = RbxVector3.YAxis;
                    xCross = f.Cross(upVec);
                }
            }

            RbxVector3 xVec = xCross.Unit;
            RbxVector3 zVec = -f;
            RbxVector3 yVec = zVec.Cross(xVec);
            return FromAxes(at, xVec, yVec, zVec);
        }

        /// <summary>
        /// Deprecated CFrame.new(pos, lookAt) overload — kept because tutorial-corpus scripts use it.
        /// </summary>
        public static RbxCFrame FromPositionLookAt(RbxVector3 pos, RbxVector3 lookAt) => LookAt(pos, lookAt);

        /// <summary>CFrame.fromMatrix(pos, vX, vY, vZ?) — vZ defaults to vX:Cross(vY).Unit.</summary>
        public static RbxCFrame FromMatrix(RbxVector3 pos, RbxVector3 vX, RbxVector3 vY, RbxVector3? vZ = null)
        {
            RbxVector3 z = vZ ?? vX.Cross(vY).Unit;
            return FromAxes(pos, vX, vY, z);
        }

        /// <summary>CFrame.fromEulerAngles(rx, ry, rz, order = XYZ); angles in radians.</summary>
        public static RbxCFrame FromEulerAngles(
            float rx, float ry, float rz, RbxRotationOrder order = RbxRotationOrder.XYZ)
        {
            RbxCFrame x = RotationX(rx);
            RbxCFrame y = RotationY(ry);
            RbxCFrame z = RotationZ(rz);

            // WHY: order letters are multiplication order, e.g. XYZ = Rx * Ry * Rz (Z applied first).
            switch (order)
            {
                case RbxRotationOrder.XYZ: return x * y * z;
                case RbxRotationOrder.XZY: return x * z * y;
                case RbxRotationOrder.YZX: return y * z * x;
                case RbxRotationOrder.YXZ: return y * x * z;
                case RbxRotationOrder.ZXY: return z * x * y;
                case RbxRotationOrder.ZYX: return z * y * x;
                default:
                    throw RobloxApiStubException.BadArgument(
                        $"Unknown RotationOrder '{order}'.",
                        "pass one of Enum.RotationOrder.XYZ/XZY/YZX/YXZ/ZXY/ZYX");
            }
        }

        /// <summary>CFrame.fromEulerAnglesXYZ == CFrame.Angles.</summary>
        public static RbxCFrame FromEulerAnglesXYZ(float rx, float ry, float rz) =>
            FromEulerAngles(rx, ry, rz, RbxRotationOrder.XYZ);

        /// <summary>CFrame.Angles(rx, ry, rz) — alias of fromEulerAnglesXYZ.</summary>
        public static RbxCFrame Angles(float rx, float ry, float rz) => FromEulerAnglesXYZ(rx, ry, rz);

        /// <summary>CFrame.fromEulerAnglesYXZ == CFrame.fromOrientation.</summary>
        public static RbxCFrame FromEulerAnglesYXZ(float rx, float ry, float rz) =>
            FromEulerAngles(rx, ry, rz, RbxRotationOrder.YXZ);

        /// <summary>CFrame.fromOrientation(rx, ry, rz) — alias of fromEulerAnglesYXZ.</summary>
        public static RbxCFrame FromOrientation(float rx, float ry, float rz) => FromEulerAnglesYXZ(rx, ry, rz);

        /// <summary>CFrame.fromAxisAngle(v, r) — rotation of r radians around unit axis v.</summary>
        public static RbxCFrame FromAxisAngle(RbxVector3 axis, float angle)
        {
            RbxVector3 a = axis.Unit;
            float c = MathF.Cos(angle);
            float s = MathF.Sin(angle);
            float t = 1f - c;

            return new RbxCFrame(
                0f, 0f, 0f,
                t * a.X * a.X + c, t * a.X * a.Y - s * a.Z, t * a.X * a.Z + s * a.Y,
                t * a.X * a.Y + s * a.Z, t * a.Y * a.Y + c, t * a.Y * a.Z - s * a.X,
                t * a.X * a.Z - s * a.Y, t * a.Y * a.Z + s * a.X, t * a.Z * a.Z + c);
        }

        /// <summary>CFrame.fromRotationBetweenVectors(from, to) — rotation carrying from onto to.</summary>
        public static RbxCFrame FromRotationBetweenVectors(RbxVector3 from, RbxVector3 to)
        {
            RbxVector3 f = from.Unit;
            RbxVector3 t = to.Unit;
            RbxVector3 axis = f.Cross(t);
            float dot = f.Dot(t);

            if (axis.Magnitude < 1e-6f)
            {
                if (dot > 0f)
                {
                    return Identity;
                }

                // WHY: antiparallel vectors need any axis orthogonal to `from`.
                RbxVector3 ortho = f.Cross(RbxVector3.XAxis);
                if (ortho.Magnitude < 1e-6f)
                {
                    ortho = f.Cross(RbxVector3.YAxis);
                }

                return FromAxisAngle(ortho, MathF.PI);
            }

            return FromAxisAngle(axis, MathF.Atan2(axis.Magnitude, dot));
        }

        private static RbxCFrame FromAxes(RbxVector3 pos, RbxVector3 x, RbxVector3 y, RbxVector3 z) =>
            new RbxCFrame(
                pos.X, pos.Y, pos.Z,
                x.X, y.X, z.X,
                x.Y, y.Y, z.Y,
                x.Z, y.Z, z.Z);

        private static RbxCFrame RotationX(float t)
        {
            float c = MathF.Cos(t), s = MathF.Sin(t);
            return new RbxCFrame(0f, 0f, 0f, 1f, 0f, 0f, 0f, c, -s, 0f, s, c);
        }

        private static RbxCFrame RotationY(float t)
        {
            float c = MathF.Cos(t), s = MathF.Sin(t);
            return new RbxCFrame(0f, 0f, 0f, c, 0f, s, 0f, 1f, 0f, -s, 0f, c);
        }

        private static RbxCFrame RotationZ(float t)
        {
            float c = MathF.Cos(t), s = MathF.Sin(t);
            return new RbxCFrame(0f, 0f, 0f, c, -s, 0f, s, c, 0f, 0f, 0f, 1f);
        }

        // ---- Components --------------------------------------------------------------

        public RbxVector3 Position => new RbxVector3(_x, _y, _z);
        public float X => _x;
        public float Y => _y;
        public float Z => _z;

        /// <summary>The rotation-only copy (position zero).</summary>
        public RbxCFrame Rotation => new RbxCFrame(
            0f, 0f, 0f, _r00, _r01, _r02, _r10, _r11, _r12, _r20, _r21, _r22);

        public RbxVector3 XVector => new RbxVector3(_r00, _r10, _r20);
        public RbxVector3 YVector => new RbxVector3(_r01, _r11, _r21);
        public RbxVector3 ZVector => new RbxVector3(_r02, _r12, _r22);

        public RbxVector3 RightVector => XVector;
        public RbxVector3 UpVector => YVector;

        /// <summary>Forward direction: the negated Z column (right-handed Roblox convention).</summary>
        public RbxVector3 LookVector => new RbxVector3(-_r02, -_r12, -_r22);

        /// <summary>GetComponents(): (x, y, z, R00, R01, R02, R10, R11, R12, R20, R21, R22).</summary>
        public float[] GetComponents() => new[]
        {
            _x, _y, _z, _r00, _r01, _r02, _r10, _r11, _r12, _r20, _r21, _r22
        };

        // ---- Transformations ---------------------------------------------------------

        public RbxCFrame Inverse()
        {
            // WHY: rigid transform inverse — transpose rotation, back-rotate the translation.
            float ix = -(_r00 * _x + _r10 * _y + _r20 * _z);
            float iy = -(_r01 * _x + _r11 * _y + _r21 * _z);
            float iz = -(_r02 * _x + _r12 * _y + _r22 * _z);
            return new RbxCFrame(
                ix, iy, iz,
                _r00, _r10, _r20,
                _r01, _r11, _r21,
                _r02, _r12, _r22);
        }

        public RbxCFrame ToWorldSpace(RbxCFrame cf) => this * cf;
        public RbxCFrame ToObjectSpace(RbxCFrame cf) => Inverse() * cf;

        public RbxVector3 PointToWorldSpace(RbxVector3 p) => new RbxVector3(
            _r00 * p.X + _r01 * p.Y + _r02 * p.Z + _x,
            _r10 * p.X + _r11 * p.Y + _r12 * p.Z + _y,
            _r20 * p.X + _r21 * p.Y + _r22 * p.Z + _z);

        public RbxVector3 PointToObjectSpace(RbxVector3 p)
        {
            float dx = p.X - _x, dy = p.Y - _y, dz = p.Z - _z;
            return new RbxVector3(
                _r00 * dx + _r10 * dy + _r20 * dz,
                _r01 * dx + _r11 * dy + _r21 * dz,
                _r02 * dx + _r12 * dy + _r22 * dz);
        }

        public RbxVector3 VectorToWorldSpace(RbxVector3 v) => new RbxVector3(
            _r00 * v.X + _r01 * v.Y + _r02 * v.Z,
            _r10 * v.X + _r11 * v.Y + _r12 * v.Z,
            _r20 * v.X + _r21 * v.Y + _r22 * v.Z);

        public RbxVector3 VectorToObjectSpace(RbxVector3 v) => new RbxVector3(
            _r00 * v.X + _r10 * v.Y + _r20 * v.Z,
            _r01 * v.X + _r11 * v.Y + _r21 * v.Z,
            _r02 * v.X + _r12 * v.Y + _r22 * v.Z);

        /// <summary>Position lerp + shortest-path rotation slerp (Roblox CFrame:Lerp).</summary>
        public RbxCFrame Lerp(RbxCFrame goal, float alpha)
        {
            RbxVector3 pos = Position.Lerp(goal.Position, alpha);
            (float ax, float ay, float az, float aw) = ToQuaternion();
            (float bx, float by, float bz, float bw) = goal.ToQuaternion();

            float dot = ax * bx + ay * by + az * bz + aw * bw;
            if (dot < 0f)
            {
                bx = -bx; by = -by; bz = -bz; bw = -bw;
                dot = -dot;
            }

            float wa, wb;
            if (dot > 0.9995f)
            {
                // WHY: nearly identical rotations — nlerp avoids sin(theta) ~ 0 division.
                wa = 1f - alpha;
                wb = alpha;
            }
            else
            {
                float theta = MathF.Acos(Math.Clamp(dot, -1f, 1f));
                float sinTheta = MathF.Sin(theta);
                wa = MathF.Sin((1f - alpha) * theta) / sinTheta;
                wb = MathF.Sin(alpha * theta) / sinTheta;
            }

            float qx = wa * ax + wb * bx;
            float qy = wa * ay + wb * by;
            float qz = wa * az + wb * bz;
            float qw = wa * aw + wb * bw;
            return FromQuaternion(pos.X, pos.Y, pos.Z, qx, qy, qz, qw);
        }

        /// <summary>Gram-Schmidt re-orthonormalization preserving handedness (det = +1).</summary>
        public RbxCFrame Orthonormalize()
        {
            RbxVector3 z = ZVector.Unit;
            RbxVector3 x = (XVector - z * XVector.Dot(z)).Unit;
            RbxVector3 y = z.Cross(x);
            return FromAxes(Position, x, y, z);
        }

        // ---- Decomposition -----------------------------------------------------------

        /// <summary>Angles (rx, ry, rz) such that FromEulerAnglesXYZ reconstructs the rotation.</summary>
        public (float rx, float ry, float rz) ToEulerAnglesXYZ()
        {
            float ry = MathF.Asin(Math.Clamp(_r02, -1f, 1f));
            float rx = MathF.Atan2(-_r12, _r22);
            float rz = MathF.Atan2(-_r01, _r00);
            return (rx, ry, rz);
        }

        /// <summary>Angles (rx, ry, rz) such that FromEulerAnglesYXZ reconstructs the rotation.</summary>
        public (float rx, float ry, float rz) ToEulerAnglesYXZ()
        {
            float rx = MathF.Asin(Math.Clamp(-_r12, -1f, 1f));
            float ry = MathF.Atan2(_r02, _r22);
            float rz = MathF.Atan2(_r10, _r11);
            return (rx, ry, rz);
        }

        /// <summary>ToOrientation() — alias of ToEulerAnglesYXZ (Roblox parity).</summary>
        public (float rx, float ry, float rz) ToOrientation() => ToEulerAnglesYXZ();

        /// <summary>Rotation as (unit axis, angle in radians); identity yields (xAxis, 0).</summary>
        public (RbxVector3 axis, float angle) ToAxisAngle()
        {
            (float qx, float qy, float qz, float qw) = ToQuaternion();
            float sinHalf = MathF.Sqrt(qx * qx + qy * qy + qz * qz);
            if (sinHalf < 1e-7f)
            {
                return (RbxVector3.XAxis, 0f);
            }

            float angle = 2f * MathF.Atan2(sinHalf, qw);
            return (new RbxVector3(qx / sinHalf, qy / sinHalf, qz / sinHalf), angle);
        }

        /// <summary>Angle in radians between this rotation and another (relative rotation angle).</summary>
        public float AngleBetween(RbxCFrame other)
        {
            (float ax, float ay, float az, float aw) = ToQuaternion();
            (float bx, float by, float bz, float bw) = other.ToQuaternion();
            float dot = MathF.Abs(ax * bx + ay * by + az * bz + aw * bw);
            return 2f * MathF.Acos(Math.Clamp(dot, 0f, 1f));
        }

        public bool FuzzyEq(RbxCFrame other, float epsilon = 1e-5f) =>
            Position.FuzzyEq(other.Position, epsilon) &&
            XVector.FuzzyEq(other.XVector, epsilon) &&
            YVector.FuzzyEq(other.YVector, epsilon) &&
            ZVector.FuzzyEq(other.ZVector, epsilon);

        /// <summary>
        /// Rotation as quaternion components (x, y, z, w). Public because the RobloxSpace
        /// adapter needs it for the single-boundary handedness conversion.
        /// </summary>
        public (float qx, float qy, float qz, float qw) ToQuaternion()
        {
            float trace = _r00 + _r11 + _r22;
            float qx, qy, qz, qw;
            if (trace > 0f)
            {
                float s = MathF.Sqrt(trace + 1f) * 2f;
                qw = 0.25f * s;
                qx = (_r21 - _r12) / s;
                qy = (_r02 - _r20) / s;
                qz = (_r10 - _r01) / s;
            }
            else if (_r00 > _r11 && _r00 > _r22)
            {
                float s = MathF.Sqrt(1f + _r00 - _r11 - _r22) * 2f;
                qw = (_r21 - _r12) / s;
                qx = 0.25f * s;
                qy = (_r01 + _r10) / s;
                qz = (_r02 + _r20) / s;
            }
            else if (_r11 > _r22)
            {
                float s = MathF.Sqrt(1f + _r11 - _r00 - _r22) * 2f;
                qw = (_r02 - _r20) / s;
                qx = (_r01 + _r10) / s;
                qy = 0.25f * s;
                qz = (_r12 + _r21) / s;
            }
            else
            {
                float s = MathF.Sqrt(1f + _r22 - _r00 - _r11) * 2f;
                qw = (_r10 - _r01) / s;
                qx = (_r02 + _r20) / s;
                qy = (_r12 + _r21) / s;
                qz = 0.25f * s;
            }

            return (qx, qy, qz, qw);
        }

        // ---- Operators ---------------------------------------------------------------

        public static RbxCFrame operator *(RbxCFrame a, RbxCFrame b)
        {
            float[] c = a.GetComponents();
            float[] d = b.GetComponents();
            // WHY: GetComponents layout is [x y z r00 r01 r02 r10 r11 r12 r20 r21 r22], so the
            // rotation block starts at index 3 — the indices below multiply the two 3x3 matrices.
            float r00 = c[3] * d[3] + c[4] * d[6] + c[5] * d[9];
            float r01 = c[3] * d[4] + c[4] * d[7] + c[5] * d[10];
            float r02 = c[3] * d[5] + c[4] * d[8] + c[5] * d[11];
            float r10 = c[6] * d[3] + c[7] * d[6] + c[8] * d[9];
            float r11 = c[6] * d[4] + c[7] * d[7] + c[8] * d[10];
            float r12 = c[6] * d[5] + c[7] * d[8] + c[8] * d[11];
            float r20 = c[9] * d[3] + c[10] * d[6] + c[11] * d[9];
            float r21 = c[9] * d[4] + c[10] * d[7] + c[11] * d[10];
            float r22 = c[9] * d[5] + c[10] * d[8] + c[11] * d[11];
            RbxVector3 pos = a.PointToWorldSpace(b.Position);
            return new RbxCFrame(pos.X, pos.Y, pos.Z, r00, r01, r02, r10, r11, r12, r20, r21, r22);
        }

        /// <summary>CFrame * Vector3 — transforms the point into world space.</summary>
        public static RbxVector3 operator *(RbxCFrame cf, RbxVector3 p) => cf.PointToWorldSpace(p);

        /// <summary>CFrame + Vector3 — world-space translation (rotation unchanged).</summary>
        public static RbxCFrame operator +(RbxCFrame cf, RbxVector3 v) => new RbxCFrame(
            cf._x + v.X, cf._y + v.Y, cf._z + v.Z,
            cf._r00, cf._r01, cf._r02, cf._r10, cf._r11, cf._r12, cf._r20, cf._r21, cf._r22);

        public static RbxCFrame operator -(RbxCFrame cf, RbxVector3 v) => cf + (-v);

        public static bool operator ==(RbxCFrame a, RbxCFrame b) => a.Equals(b);
        public static bool operator !=(RbxCFrame a, RbxCFrame b) => !a.Equals(b);

        public bool Equals(RbxCFrame other)
        {
            float[] a = GetComponents();
            float[] b = other.GetComponents();
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                {
                    return false;
                }
            }

            return true;
        }

        public override bool Equals(object obj) => obj is RbxCFrame cf && Equals(cf);

        public override int GetHashCode() => HashCode.Combine(
            HashCode.Combine(_x, _y, _z),
            HashCode.Combine(_r00, _r01, _r02, _r10),
            HashCode.Combine(_r11, _r12, _r20, _r21),
            _r22);

        /// <summary>Roblox tostring format: all 12 components comma-separated.</summary>
        public override string ToString()
        {
            float[] c = GetComponents();
            string[] parts = new string[c.Length];
            for (int i = 0; i < c.Length; i++)
            {
                parts[i] = c[i].ToString(CultureInfo.InvariantCulture);
            }

            return string.Join(", ", parts);
        }
    }
}
