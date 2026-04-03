using CoreAI.ExampleGame.ArenaWaves.Domain;
using UnityEngine;

namespace CoreAI.ExampleGame.ArenaWaves.Infrastructure
{
    /// <summary>
    /// Кривая в духе Vampire Survivors: к концу забега давление растёт, но отдельные волны мягче за счёт синуса
    /// (передышка), без монотонного «каждая волна жёстче предыдущей».
    /// </summary>
    [CreateAssetMenu(fileName = "ArenaVsWaveDifficulty", menuName = "CoreAI Example/Arena/VS-style Wave Difficulty", order = 30)]
    public sealed class ArenaVsStyleWaveDifficulty : ScriptableObject, IArenaWaveDifficulty
    {
        [Header("База числа врагов (множитель к IArenaWaveSchedule / плану)")]
        [Tooltip("Амплитуда «лёгкой» волны: чуть меньше врагов в минимуме синуса.")]
        [SerializeField] [Range(0f, 0.25f)] private float enemyCountBreathAmplitude = 0.1f;

        [Tooltip("Период колебания в волнах (например 3.5 — паттерн сложнее-легче).")]
        [SerializeField] [Min(0.5f)] private float enemyCountBreathPeriodWaves = 4f;

        [SerializeField] private float enemyCountPhaseOffset;

        [Header("Суммарный рост угрозы (HP / урон / скорость к концу рана)")]
        [SerializeField] [Min(0.1f)] private float rampStart = 1f;

        [SerializeField] [Min(0.1f)] private float rampEnd = 2.45f;

        [Tooltip("Опционально: по оси X нормализованный прогресс 0..1 по волне, по Y множитель 0..1 к интерполяции rampStart→rampEnd. Пусто = SmoothStep.")]
        [SerializeField] private AnimationCurve rampProgress01;

        [Header("Локальные колебания статов (легче / жёстче волна)")]
        [Tooltip("Насколько сильно одна волна может быть мягче среднего по HP.")]
        [SerializeField] [Range(0f, 0.35f)] private float statBreathAmplitude = 0.16f;

        [SerializeField] [Min(0.5f)] private float statBreathPeriodWaves = 3.25f;

        [SerializeField] private float statBreathPhaseOffset;

        [Tooltip("Урон колеблется слабее HP (меньше случайных ваншотов в «лёгкой» волне).")]
        [SerializeField] [Range(0f, 1f)] private float damageBreathBlend = 0.4f;

        [Header("Темп спавна")]
        [Tooltip("К последней волне интервал умножается на это (меньше — плотнее поток).")]
        [SerializeField] [Range(0.35f, 1f)] private float spawnIntervalEndFactor = 0.62f;

        [Tooltip("В «жёстких» фазах синуса спавн чуть ускоряется.")]
        [SerializeField] [Range(0f, 0.25f)] private float spawnBreathCoupling = 0.12f;

        public ArenaWaveDifficultySample Evaluate(int waveIndex1Based, int totalWavesInRun)
        {
            int wave = Mathf.Max(1, waveIndex1Based);
            int total = Mathf.Max(1, totalWavesInRun);
            float t = total <= 1 ? 1f : (wave - 1) / (float)(total - 1);

            float alpha = rampProgress01 != null && rampProgress01.length > 0
                ? Mathf.Clamp01(rampProgress01.Evaluate(t))
                : Mathf.SmoothStep(0f, 1f, t);
            float ramp = Mathf.Lerp(rampStart, rampEnd, alpha);

            float countOsc = 1f +
                enemyCountBreathAmplitude *
                Mathf.Sin((wave + enemyCountPhaseOffset) * 2f * Mathf.PI /
                    Mathf.Max(0.5f, enemyCountBreathPeriodWaves));
            countOsc = Mathf.Max(0.82f, countOsc);

            float statOsc = 1f +
                statBreathAmplitude *
                Mathf.Sin((wave + statBreathPhaseOffset) * 2f * Mathf.PI /
                    Mathf.Max(0.5f, statBreathPeriodWaves));
            statOsc = Mathf.Clamp(statOsc, 1f - statBreathAmplitude, 1f + statBreathAmplitude);

            float hpMult = ramp * statOsc;
            float dmgOsc = Mathf.Lerp(1f, statOsc, damageBreathBlend);
            float dmgMult = ramp * dmgOsc;

            float spdRamp = Mathf.Lerp(1f, ramp, 0.45f);
            float spdOsc = Mathf.Lerp(1f, statOsc, 0.55f);
            float spdMult = spdRamp * spdOsc;

            float spawnT = Mathf.Lerp(1f, spawnIntervalEndFactor, alpha);
            float breathPush = 1f - spawnBreathCoupling * (statOsc - 1f);
            float spawnMult = spawnT * Mathf.Clamp(breathPush, 0.75f, 1.15f);

            return new ArenaWaveDifficultySample(countOsc, hpMult, dmgMult, spdMult, spawnMult);
        }
    }
}
