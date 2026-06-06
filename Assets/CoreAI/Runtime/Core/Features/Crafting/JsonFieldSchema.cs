namespace CoreAI.Crafting
{
    /// <summary>
    /// Describes one field in a JSON tool schema.
    /// </summary>
    public sealed class JsonFieldSchema
    {
        /// <summary>Public name.</summary>
        public string Name { get; set; }

        /// <summary>Expected JSON value type.</summary>
        public string Type { get; set; }

        /// <summary>Whether this field is required.</summary>
        public bool Required { get; set; }

        /// <summary>Minimum numeric value accepted by this field.</summary>
        public double? Min { get; set; }

        /// <summary>Maximum numeric value accepted by this field.</summary>
        public double? Max { get; set; }

        /// <summary>Allowed literal values for this field.</summary>
        public string[] AllowedValues { get; set; }

        /// <summary>Human-readable description.</summary>
        public string Description { get; set; }
    }
}