using System;
using System.Reflection;

namespace CoreAI.Infrastructure.Lua
{
    /// <summary>
    /// Host policy for denying Full-tier Lua reflection access to component types or members.
    /// </summary>
    public interface IFullLuaAccessBlacklistPolicy
    {
        /// <summary>Returns true when Full Lua may access the component type.</summary>
        bool IsTypeAllowed(Type componentType);

        /// <summary>Returns true when Full Lua may read, write, or call the reflected member.</summary>
        bool IsMemberAllowed(MemberInfo member);
    }

    /// <summary>
    /// Default Full Lua reflection policy that preserves the historical allow-all behavior.
    /// </summary>
    public sealed class AllowAllFullLuaAccessBlacklistPolicy : IFullLuaAccessBlacklistPolicy
    {
        /// <summary>Shared stateless instance.</summary>
        public static readonly AllowAllFullLuaAccessBlacklistPolicy Instance = new();

        private AllowAllFullLuaAccessBlacklistPolicy()
        {
        }

        /// <inheritdoc />
        public bool IsTypeAllowed(Type componentType)
        {
            return true;
        }

        /// <inheritdoc />
        public bool IsMemberAllowed(MemberInfo member)
        {
            return true;
        }
    }
}