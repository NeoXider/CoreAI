using Neo.Tools;
using UnityEngine;

namespace CoreAI.ExampleGame.ArenaProgression.Infrastructure
{
    /// <summary>Сдвигает веса редкости на <b>копии</b> ChanceManager (оригинальный SO не трогаем).</summary>
    public sealed class ArenaRarityLuckModifier
    {
        public void Apply(ChanceManager runtimeCopy, float luck01)
        {
            if (runtimeCopy == null || luck01 <= 0f)
                return;
            luck01 = Mathf.Clamp01(luck01);
            var entries = runtimeCopy.Entries;
            if (entries == null || entries.Count < 2)
                return;

            float commonW = runtimeCopy.GetChanceValue(0);
            float takeFromCommon = commonW * (0.35f * luck01);
            if (takeFromCommon <= 0f)
                return;

            runtimeCopy.SetChanceValue(0, Mathf.Max(0f, commonW - takeFromCommon));
            float distribute = takeFromCommon / (entries.Count - 1);
            for (int i = 1; i < entries.Count; i++)
                runtimeCopy.SetChanceValue(i, runtimeCopy.GetChanceValue(i) + distribute);
        }
    }
}
