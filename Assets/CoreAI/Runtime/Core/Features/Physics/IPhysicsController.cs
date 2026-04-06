namespace CoreAI.Ai
{
    /// <summary>
    /// Абстрактный интерфейс для управления физикой.
    /// Реализуется для каждого движка отдельно (Unity, Unreal, Godot).
    /// </summary>
    public interface IPhysicsController
    {
        /// <summary>
        /// Применить силу к объекту.
        /// </summary>
        /// <param name="objectId">ID объекта</param>
        /// <param name="forceX">Сила по X</param>
        /// <param name="forceY">Сила по Y</param>
        /// <param name="forceZ">Сила по Z</param>
        /// <returns>true если сила применена</returns>
        bool ApplyForce(string objectId, float forceX, float forceY, float forceZ);

        /// <summary>
        /// Создать эффект частиц.
        /// </summary>
        /// <param name="objectId">ID объекта-эмиттера</param>
        /// <param name="effectName">Имя эффекта</param>
        /// <returns>true если эффект создан</returns>
        bool SpawnParticles(string objectId, string effectName);

        /// <summary>
        /// Включить/выключить физику для объекта.
        /// </summary>
        /// <param name="objectId">ID объекта</param>
        /// <param name="enabled">true = включить, false = выключить</param>
        bool SetPhysicsEnabled(string objectId, bool enabled);
    }
}
