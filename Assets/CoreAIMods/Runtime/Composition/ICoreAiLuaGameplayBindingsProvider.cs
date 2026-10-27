using CoreAI.Ai;
using CoreAI.Sandbox.LuaCs;

namespace CoreAI.Composition
{
    /// <summary>Explicit host-owned gameplay APIs for a scope's Lua states at their effective capability tier.</summary>
    public interface ICoreAiLuaGameplayBindingsProvider
    {
        /// <summary>Registers only APIs allowed by the supplied effective capabilities; must be safe before Start.</summary>
        void RegisterGameplayBindings(LuaCsApiRegistry registry, LuaCapabilities capabilities);
    }
}
