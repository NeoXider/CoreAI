using System;

namespace CoreAI.Mods.Rbx.Instances.RuntimeUi
{
    /// <summary>
    /// Per-mod limits of the <c>UxmlService</c> Lua surface. The host configures them (pass an instance
    /// to <c>RegisterRuntimeUiLua</c> or register one in the container); the runtime UI host's own
    /// limits stay the hard ceiling above them. Each exceeded limit raises its own stable error code.
    /// </summary>
    public sealed class RbxUiBudgets
    {
        private int _maxScreensPerMod = 32;
        private int _maxElementsPerMod = 4096;
        private int _maxTextLength = 64 * 1024;
        private int _maxWritesPerFrame = 8192;

        /// <summary>Live screens one mod may own (<c>UI_BUDGET_SCREENS</c>). Default 32.</summary>
        public int MaxScreensPerMod
        {
            get => _maxScreensPerMod;
            set => _maxScreensPerMod = Positive(value, nameof(MaxScreensPerMod));
        }

        /// <summary>Authored elements over all screens of one mod (<c>UI_BUDGET_ELEMENTS</c>). Default 4,096.</summary>
        public int MaxElementsPerMod
        {
            get => _maxElementsPerMod;
            set => _maxElementsPerMod = Positive(value, nameof(MaxElementsPerMod));
        }

        /// <summary>Characters of UXML, USS or element text one call may pass (<c>UI_TEXT_TOO_LARGE</c>). Default 65,536.</summary>
        public int MaxTextLength
        {
            get => _maxTextLength;
            set => _maxTextLength = Positive(value, nameof(MaxTextLength));
        }

        /// <summary>
        /// Element writes (text, value, visibility, enabled state, z-order, class, style, attribute, list
        /// items) one mod may make per frame (<c>UI_BUDGET_WRITES</c>). Default 8,192.
        /// </summary>
        public int MaxWritesPerFrame
        {
            get => _maxWritesPerFrame;
            set => _maxWritesPerFrame = Positive(value, nameof(MaxWritesPerFrame));
        }

        private static int Positive(int value, string name)
        {
            if (value <= 0)
            {
                throw new ArgumentOutOfRangeException(name, value, "A runtime UI budget must be positive.");
            }

            return value;
        }
    }
}
