namespace CoreAI.Crafting
{
    /// <summary>
    /// Определение поля JSON-схемы.
    /// </summary>
    public sealed class JsonFieldSchema
    {
        /// <summary>Имя поля в JSON объекте.</summary>
        public string Name { get; set; }

        /// <summary>Ожидаемый тип: string, number, integer, boolean, array, object.</summary>
        public string Type { get; set; }

        /// <summary>Обязательное ли поле.</summary>
        public bool Required { get; set; }

        /// <summary>Минимальное значение (для number/integer).</summary>
        public double? Min { get; set; }

        /// <summary>Максимальное значение (для number/integer).</summary>
        public double? Max { get; set; }

        /// <summary>Допустимые значения (enum constraint).</summary>
        public string[] AllowedValues { get; set; }

        /// <summary>Описание поля (для отчётов об ошибках).</summary>
        public string Description { get; set; }
    }
}
