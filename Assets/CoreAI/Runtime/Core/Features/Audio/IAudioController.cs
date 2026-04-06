namespace CoreAI.Ai
{
    /// <summary>
    /// Абстрактный интерфейс для управления звуком.
    /// Реализуется для каждого движка отдельно (Unity, Unreal, Godot).
    /// </summary>
    public interface IAudioController
    {
        /// <summary>
        /// Воспроизвести звук.
        /// </summary>
        /// <param name="clipName">Имя аудио клипа</param>
        /// <param name="volume">Громкость 0-1</param>
        /// <returns>true если звук начал воспроизводиться</returns>
        bool PlaySound(string clipName, float volume = 1f);

        /// <summary>
        /// Остановить звук.
        /// </summary>
        /// <param name="clipName">Имя аудио клипа</param>
        bool StopSound(string clipName);

        /// <summary>
        /// Установить громкость.
        /// </summary>
        /// <param name="volume">Громкость 0-1</param>
        bool SetVolume(float volume);
    }
}
