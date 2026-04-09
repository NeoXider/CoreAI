using CoreAI.ExampleGame.ArenaWaves.Infrastructure;
using UnityEngine;

namespace CoreAI.ExampleGame.ArenaSurvival.Domain
{
    [CreateAssetMenu(fileName = "ArenaDirectorSettings", menuName = "CoreAI Example/Arena/Director Settings")]
    public class ArenaDirectorSettings : ScriptableObject
    {
        public int WavesToWin = 10;
        public float SpawnInterval = 0.45f;
        public float PauseBetweenWaves = 2f;
        public float SpawnRadius = 17.5f;

        [Tooltip("Сколько секунд ждать ответ Creator (LLM) перед запасным планом из линейного расписания.")]
        public float CreatorPlanWaitSeconds = 14f;

        [Header("Предзапрос плана следующей волны")]
        [Tooltip("Когда оставшихся врагов не больше этого числа — запросить план волны N+1 (один раз за волну).")]
        [Min(0)]
        public int PreRequestNextWaveWhenAliveAtMost = 2;

        [Header("Пост-волна Analyzer")]
        public bool RunPostWaveAnalyzer = true;

        [Header("Сложность волн (VS-style)")]
        [Header("Сложность волн (VS-style)")]
        [Tooltip("Нелинейная кривая: суммарно сложнее к концу рана, отдельные волны мягче/жёстче. Пусто — только план / линейное расписание.")]
        public ArenaVsStyleWaveDifficulty WaveDifficultyProfile;

        [Header("Расписание (Fallback)")]
        [Tooltip("Линейное расписание, используется если план от Creator недоступен или пуст.")]
        public ArenaLinearWaveSchedule WaveSchedule = new ArenaLinearWaveSchedule();
    }
}
