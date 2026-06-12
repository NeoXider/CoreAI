using System;

namespace CoreAI.Ai
{
    /// <summary>
    /// Strongly-typed agent role identifier. Replaces inline string literals like
    /// <c>"SmartChat"</c>/<c>"Merchant"</c>: use the built-in statics (<see cref="SmartChat"/> etc.)
    /// or <see cref="RoleId(string)"/> for custom roles. Implicitly convertible to/from
    /// <see cref="string"/>, so it plugs into every existing string-based API
    /// (<c>AgentBuilder</c>, <c>AiTaskRequest.RoleId</c>, <c>CoreAi.AskAsync</c>).
    /// </summary>
    public readonly struct RoleId : IEquatable<RoleId>
    {
        private readonly string _value;

        /// <summary>Creates a role id from a non-empty string.</summary>
        public RoleId(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("Role id must be a non-empty string.", nameof(value));
            }

            _value = value;
        }

        /// <summary>Raw string value (empty for <c>default(RoleId)</c>).</summary>
        public string Value => _value ?? string.Empty;

        /// <summary>True when this id was never assigned (<c>default(RoleId)</c>).</summary>
        public bool IsEmpty => string.IsNullOrEmpty(_value);

        /// <summary>True when this id matches one of the built-in CoreAI roles.</summary>
        public bool IsBuiltIn => BuiltInAgentRoleIds.IsBuiltIn(_value);

        // Built-in roles (same string values as BuiltInAgentRoleIds).

        /// <summary>Creator.</summary>
        public static RoleId Creator => new(BuiltInAgentRoleIds.Creator);

        /// <summary>Analyzer.</summary>
        public static RoleId Analyzer => new(BuiltInAgentRoleIds.Analyzer);

        /// <summary>Programmer.</summary>
        public static RoleId Programmer => new(BuiltInAgentRoleIds.Programmer);

        /// <summary>Ai npc.</summary>
        public static RoleId AiNpc => new(BuiltInAgentRoleIds.AiNpc);

        /// <summary>Core gameplay mechanics agent.</summary>
        public static RoleId CoreMechanic => new(BuiltInAgentRoleIds.CoreMechanic);

        /// <summary>Plain chat.</summary>
        public static RoleId PlainChat => new(BuiltInAgentRoleIds.PlainChat);

        /// <summary>Smart chat.</summary>
        public static RoleId SmartChat => new(BuiltInAgentRoleIds.SmartChat);

        /// <summary>Merchant.</summary>
        public static RoleId Merchant => new(BuiltInAgentRoleIds.Merchant);

        public static implicit operator string(RoleId roleId)
        {
            return roleId.Value;
        }

        // Implicit conversions must not throw: a null/empty string maps to default(RoleId)
        // (IsEmpty == true) and is rejected later by the existing role validation.
        public static implicit operator RoleId(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? default : new RoleId(value);
        }

        public bool Equals(RoleId other)
        {
            return string.Equals(Value, other.Value, StringComparison.Ordinal);
        }

        public override bool Equals(object obj)
        {
            return obj is RoleId other && Equals(other);
        }

        public override int GetHashCode()
        {
            return StringComparer.Ordinal.GetHashCode(Value);
        }

        public override string ToString()
        {
            return Value;
        }

        public static bool operator ==(RoleId left, RoleId right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(RoleId left, RoleId right)
        {
            return !left.Equals(right);
        }
    }
}