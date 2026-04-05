using System;
using System.Collections.Generic;

namespace CoreAI.Ai
{
    /// <summary>
    /// Шаг мульти-агентного воркфлоу: какой агент, с каким hint, при каких условиях.
    /// </summary>
    public sealed class WorkflowStep
    {
        /// <summary>Уникальный ключ шага (для обращения в WorkflowContext).</summary>
        public string StepKey { get; set; } = "";

        /// <summary>Роль агента (Creator, Programmer, CoreMechanicAI и т.д.).</summary>
        public string RoleId { get; set; } = "";

        /// <summary>Hint для агента. Может содержать {VariableName} для подстановки из контекста.</summary>
        public string Hint { get; set; } = "";

        /// <summary>Приоритет задачи (0-10).</summary>
        public int Priority { get; set; } = 5;

        /// <summary>Условие выполнения. Если null — выполняется всегда.</summary>
        public Func<WorkflowContext, bool> Condition { get; set; }

        /// <summary>Трансформатор входных данных: получает контекст, возвращает модифицированный hint.</summary>
        public Func<WorkflowContext, string> InputTransformer { get; set; }

        /// <summary>
        /// Создаёт шаг с параметрами.
        /// </summary>
        public WorkflowStep(string stepKey, string roleId, string hint = null, int priority = 5)
        {
            StepKey = stepKey;
            RoleId = roleId;
            Hint = hint ?? "";
            Priority = priority;
        }

        /// <summary>
        /// Проверяет должен ли шаг выполниться.
        /// </summary>
        public bool ShouldExecute(WorkflowContext context)
        {
            return Condition == null || Condition(context);
        }

        /// <summary>
        /// Получает финальный hint с подстановкой переменных из контекста.
        /// </summary>
        public string GetResolvedHint(WorkflowContext context)
        {
            string hint = Hint;

            // Применяем трансформатор если есть
            if (InputTransformer != null)
            {
                hint = InputTransformer(context);
            }

            // Подставляем переменные {VariableName}
            foreach (KeyValuePair<string, string> kvp in context.Variables)
            {
                hint = hint.Replace("{" + kvp.Key + "}", kvp.Value);
            }

            return hint;
        }

        /// <summary>
        /// Создаёт задачу для оркестратора из этого шага.
        /// </summary>
        public AiTaskRequest ToTaskRequest(WorkflowContext context)
        {
            return new AiTaskRequest
            {
                RoleId = RoleId,
                Hint = GetResolvedHint(context),
                Priority = Priority,
                TraceId = context.TraceId,
                SourceTag = "workflow:" + StepKey
            };
        }
    }
}