using System;

namespace CoreAI.Mods.Rbx.Instances
{
    /// <summary>
    /// Roblox <c>TextGenerator</c> (creatable, superclass Instance): a large language model behind a system prompt,
    /// sampled with <see cref="Temperature"/>, <see cref="TopP"/> and <see cref="Seed"/>. The yielding
    /// <c>GenerateTextAsync</c> lives in the Lua binding layer; this class holds the four mirror properties with the
    /// mirror's defaults and clamps. Assignments are clamped as they are written, so a script reads back what the
    /// model will use (the mirror says "values outside the accepted range are clamped").
    /// </summary>
    public sealed class RbxTextGenerator : RbxInstance
    {
        /// <summary>Mirror default of <see cref="Temperature"/>.</summary>
        public const float DefaultTemperature = 0.7f;

        /// <summary>Mirror lower clamp of <see cref="Temperature"/>.</summary>
        public const float MinTemperature = 0.4f;

        /// <summary>Mirror upper clamp of <see cref="Temperature"/>.</summary>
        public const float MaxTemperature = 1.0f;

        /// <summary>Mirror default of <see cref="TopP"/>.</summary>
        public const float DefaultTopP = 0.9f;

        /// <summary>Mirror lower clamp of <see cref="TopP"/>.</summary>
        public const float MinTopP = 0.5f;

        /// <summary>Mirror upper clamp of <see cref="TopP"/>.</summary>
        public const float MaxTopP = 1.0f;

        private string _systemPrompt = "";
        private float _temperature = DefaultTemperature;
        private float _topP = DefaultTopP;
        private int _seed;

        internal RbxTextGenerator(ClassDescriptor descriptor)
            : base(descriptor)
        {
            Name = RbxAiClasses.TextGenerator;
        }

        /// <summary>Mirror <c>SystemPrompt</c>: the model's role, tone and behaviour for every request.</summary>
        public string SystemPrompt
        {
            get => _systemPrompt;
            set
            {
                string next = value ?? "";
                if (string.Equals(_systemPrompt, next, StringComparison.Ordinal))
                {
                    return;
                }

                _systemPrompt = next;
                NotifyPropertyChanged(nameof(SystemPrompt));
            }
        }

        /// <summary>Mirror <c>Temperature</c> (default 0.7), clamped to [0.4, 1.0]; NaN keeps the default.</summary>
        public float Temperature
        {
            get => _temperature;
            set
            {
                float next = ClampTemperature(value);
                if (_temperature.Equals(next))
                {
                    return;
                }

                _temperature = next;
                NotifyPropertyChanged(nameof(Temperature));
            }
        }

        /// <summary>Mirror <c>TopP</c> (default 0.9), clamped to [0.5, 1.0]; NaN keeps the default.</summary>
        public float TopP
        {
            get => _topP;
            set
            {
                float next = ClampTopP(value);
                if (_topP.Equals(next))
                {
                    return;
                }

                _topP = next;
                NotifyPropertyChanged(nameof(TopP));
            }
        }

        /// <summary>Mirror <c>Seed</c> (default 0): a fixed seed for reproducible answers.</summary>
        public int Seed
        {
            get => _seed;
            set
            {
                if (_seed == value)
                {
                    return;
                }

                _seed = value;
                NotifyPropertyChanged(nameof(Seed));
            }
        }

        /// <summary>The mirror clamp of a temperature; NaN becomes the default.</summary>
        public static float ClampTemperature(double value)
        {
            if (double.IsNaN(value))
            {
                return DefaultTemperature;
            }

            return (float)Math.Max(MinTemperature, Math.Min(MaxTemperature, value));
        }

        /// <summary>The mirror clamp of a top-p value; NaN becomes the default.</summary>
        public static float ClampTopP(double value)
        {
            if (double.IsNaN(value))
            {
                return DefaultTopP;
            }

            return (float)Math.Max(MinTopP, Math.Min(MaxTopP, value));
        }

        /// <summary>The mirror's seed conversion: non-integral values are truncated, the result saturates at the int range.</summary>
        public static int TruncateSeed(double value)
        {
            if (double.IsNaN(value))
            {
                return 0;
            }

            double truncated = Math.Truncate(value);
            if (truncated >= int.MaxValue)
            {
                return int.MaxValue;
            }

            return truncated <= int.MinValue ? int.MinValue : (int)truncated;
        }

        /// <summary>Copies the four properties into a clone.</summary>
        protected internal override void CopyCustomStateTo(RbxInstance copy)
        {
            RbxTextGenerator target = (RbxTextGenerator)copy;
            target._systemPrompt = _systemPrompt;
            target._temperature = _temperature;
            target._topP = _topP;
            target._seed = _seed;
        }
    }

    /// <summary>
    /// CoreAI-only <c>AIService</c>: the bridge from Lua to the host's agents (agents with a persona, memory and
    /// addressing rules, which Roblox has no shape for). The members live in the Lua binding layer; this instance only
    /// gives <c>game:GetService("AIService")</c> a real service to return. Not replicated (see the replication filter).
    /// </summary>
    public sealed class RbxAiService : RbxInstance
    {
        internal RbxAiService(ClassDescriptor descriptor)
            : base(descriptor)
        {
            Name = RbxAiClasses.AiService;
        }
    }

    /// <summary>
    /// The class descriptors of the AI surface and their registration. <see cref="Register"/> is the one line a class
    /// catalog calls; <see cref="EnsureRegistered"/> lets the Lua adapter add the classes to a catalog that does not
    /// have them yet, so the surface works before and after the catalog line lands.
    /// </summary>
    public static class RbxAiClasses
    {
        /// <summary>ClassName of the Roblox text generator.</summary>
        public const string TextGenerator = "TextGenerator";

        /// <summary>ClassName of CoreAI's agent service.</summary>
        public const string AiService = "AIService";

        /// <summary>Registers both classes; throws when either is already registered (a catalog builds once).</summary>
        public static void Register(ClassCatalog catalog)
        {
            if (catalog == null)
            {
                throw new ArgumentNullException(nameof(catalog));
            }

            catalog.Register(new ClassDescriptor(TextGenerator, "Instance", false, true, false,
                descriptor => new RbxTextGenerator(descriptor)));
            catalog.Register(new ClassDescriptor(AiService, "Instance", false, false, true,
                descriptor => new RbxAiService(descriptor)));
        }

        /// <summary>Registers whichever of the two classes <paramref name="catalog"/> does not know yet.</summary>
        public static void EnsureRegistered(ClassCatalog catalog)
        {
            if (catalog == null)
            {
                throw new ArgumentNullException(nameof(catalog));
            }

            if (!catalog.TryGet(TextGenerator, out _))
            {
                catalog.Register(new ClassDescriptor(TextGenerator, "Instance", false, true, false,
                    descriptor => new RbxTextGenerator(descriptor)));
            }

            if (!catalog.TryGet(AiService, out _))
            {
                catalog.Register(new ClassDescriptor(AiService, "Instance", false, false, true,
                    descriptor => new RbxAiService(descriptor)));
            }
        }

        /// <summary>
        /// A detached <c>AIService</c> instance for <c>ServiceCatalog.Register</c>: it is never parented under the
        /// DataModel, so world packages never save it and replication never sees it.
        /// </summary>
        public static RbxInstance CreateDetachedService(ClassCatalog catalog)
        {
            EnsureRegistered(catalog);
            catalog.TryGet(AiService, out ClassDescriptor descriptor);
            return new RbxAiService(descriptor);
        }
    }
}
