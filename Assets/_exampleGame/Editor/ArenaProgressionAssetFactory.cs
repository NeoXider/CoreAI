using CoreAI.ExampleGame.ArenaProgression.Domain;
using CoreAI.ExampleGame.ArenaWaves.Infrastructure;
using CoreAI.ExampleGame.ArenaProgression.Infrastructure;
using Neo.Progression;
using Neo.Tools;
using UnityEditor;
using UnityEngine;

namespace CoreAI.ExampleGame.Editor
{
    public static class ArenaProgressionAssetFactory
    {
        private const string Root = "Assets/_exampleGame/Settings/Progression";
        private const string ArenaRoot = "Assets/_exampleGame/Settings/Arena";

        [MenuItem("CoreAI Example/Arena/Generate Progression Assets (Defaults)")]
        public static void GenerateAll()
        {
            EnsureDir(Root);

            var baseline = GetOrCreate<ArenaUnitBaselineConfig>($"{Root}/ArenaUnitBaseline.asset");
            var persistence = GetOrCreate<ArenaPersistenceConfig>($"{Root}/ArenaPersistence.asset");
            var runBalance = GetOrCreate<ArenaRunBalanceConfig>($"{Root}/ArenaRunBalance.asset");
            var presentation = GetOrCreate<ArenaUpgradePresentationConfig>($"{Root}/ArenaUpgradePresentation.asset");
            var sessionCurve = GetOrCreate<LevelCurveDefinition>($"{Root}/SessionLevelCurve.asset");
            var metaCurve = GetOrCreate<LevelCurveDefinition>($"{Root}/MetaLevelCurve.asset");
            FillLevelCurve(sessionCurve);
            FillLevelCurve(metaCurve);
            AssignCurves(runBalance, sessionCurve, metaCurve);

            var rarity = GetOrCreate<ChanceData>($"{Root}/Chance_Rarity.asset");
            FillRarity(rarity);
            var catCr = GetOrCreate<ChanceData>($"{Root}/Chance_Category_CommonRare.asset");
            FillCategoryCommonRare(catCr);
            var catEpic = GetOrCreate<ChanceData>($"{Root}/Chance_Category_Epic.asset");
            FillCategoryEpic(catEpic);
            var catLeg = GetOrCreate<ChanceData>($"{Root}/Chance_Category_Legendary.asset");
            FillCategoryLegendary(catLeg);

            var upHp = CreateUpgrade($"{Root}/Up_StatHp_Common.asset", "stat_hp", "HP+", "+10 макс. HP", ArenaUpgradeKind.StatHp,
                ArenaRarity.Common, 10f);
            var upDmg = CreateUpgrade($"{Root}/Up_StatDmg_Rare.asset", "stat_dmg", "Урон+", "+5 урона", ArenaUpgradeKind.StatDamage,
                ArenaRarity.Rare, 5f);
            var upAspd = CreateUpgrade($"{Root}/Up_StatAspd_Epic.asset", "stat_aspd", "Скорость атаки", "Быстрее удары",
                ArenaUpgradeKind.StatAttackSpeed, ArenaRarity.Epic, 3f);
            var upPassive = CreateUpgrade($"{Root}/Up_Passive_Epic.asset", "passive_slot", "Пассивный слот", "+1 слот",
                ArenaUpgradeKind.PassiveSlotPlusOne, ArenaRarity.Epic, 0f);
            var upChoices = CreateUpgrade($"{Root}/Up_Choices_Legendary.asset", "extra_choices", "Больше карт", "+1 карта выбора",
                ArenaUpgradeKind.OfferExtraChoices, ArenaRarity.Legendary, 0f);
            var upDouble = CreateUpgrade($"{Root}/Up_DoublePick_Legendary.asset", "double_pick", "Двойной выбор",
                "Два апгрейда на следующем экране", ArenaUpgradeKind.LegendaryDoublePickThisWave,
                ArenaRarity.Legendary, 0f);

            var statWeights = GetOrCreate<ChanceData>($"{Root}/Chance_StatUpgradePool.asset");
            FillStatWeights(statWeights, 3);

            var content = GetOrCreate<ArenaProgressionContent>($"{Root}/ArenaProgressionContent.asset");
            AssignContent(content, runBalance, persistence, presentation, rarity, catCr, catEpic, catLeg, statWeights,
                new[] { upHp, upDmg, upAspd, upPassive, upChoices, upDouble });

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[CoreAI Example] Progression assets written to " + Root);
        }

        [MenuItem("CoreAI Example/Arena/Generate VS Wave Difficulty Asset")]
        public static void GenerateVsWaveDifficulty()
        {
            EnsureDir(ArenaRoot);
            const string path = ArenaRoot + "/ArenaVsWaveDifficulty.asset";
            var a = AssetDatabase.LoadAssetAtPath<ArenaVsStyleWaveDifficulty>(path);
            if (a == null)
            {
                a = ScriptableObject.CreateInstance<ArenaVsStyleWaveDifficulty>();
                AssetDatabase.CreateAsset(a, path);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[CoreAI Example] VS wave difficulty: " + path);
        }

        private static void EnsureDir(string path)
        {
            if (AssetDatabase.IsValidFolder(path))
                return;
            var parts = path.Split('/');
            var cur = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                var next = cur + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(cur, parts[i]);
                cur = next;
            }
        }

        private static T GetOrCreate<T>(string path) where T : ScriptableObject
        {
            var a = AssetDatabase.LoadAssetAtPath<T>(path);
            if (a != null)
                return a;
            a = ScriptableObject.CreateInstance<T>();
            AssetDatabase.CreateAsset(a, path);
            return a;
        }

        private static void FillLevelCurve(LevelCurveDefinition curve)
        {
            var so = new SerializedObject(curve);
            var levels = so.FindProperty("_levels");
            levels.ClearArray();
            void Add(int level, int reqXp)
            {
                levels.InsertArrayElementAtIndex(levels.arraySize);
                var el = levels.GetArrayElementAtIndex(levels.arraySize - 1);
                el.FindPropertyRelative("_level").intValue = level;
                el.FindPropertyRelative("_requiredXp").intValue = reqXp;
            }

            Add(1, 0);
            Add(2, 50);
            Add(3, 120);
            Add(4, 220);
            Add(5, 360);
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(curve);
        }

        private static void AssignCurves(ArenaRunBalanceConfig run, LevelCurveDefinition session, LevelCurveDefinition meta)
        {
            var so = new SerializedObject(run);
            so.FindProperty("sessionLevelCurve").objectReferenceValue = session;
            so.FindProperty("metaLevelCurve").objectReferenceValue = meta;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(run);
        }

        private static void FillRarity(ChanceData data)
        {
            data.ClearChances();
            var m = data.Manager;
            m.AddChance(50f);
            m.AddChance(30f);
            m.AddChance(15f);
            m.AddChance(5f);
            EditorUtility.SetDirty(data);
        }

        private static void FillCategoryCommonRare(ChanceData data)
        {
            data.ClearChances();
            var m = data.Manager;
            m.AddChance(1f);
            EditorUtility.SetDirty(data);
        }

        private static void FillCategoryEpic(ChanceData data)
        {
            data.ClearChances();
            var m = data.Manager;
            m.AddChance(70f);
            m.AddChance(30f);
            EditorUtility.SetDirty(data);
        }

        private static void FillCategoryLegendary(ChanceData data)
        {
            data.ClearChances();
            var m = data.Manager;
            m.AddChance(50f);
            m.AddChance(25f);
            m.AddChance(25f);
            EditorUtility.SetDirty(data);
        }

        private static void FillStatWeights(ChanceData data, int count)
        {
            data.ClearChances();
            var m = data.Manager;
            for (int i = 0; i < count; i++)
                m.AddChance(1f);
            EditorUtility.SetDirty(data);
        }

        private static ArenaUpgradeDefinition CreateUpgrade(string path, string id, string title, string desc,
            ArenaUpgradeKind kind, ArenaRarity rarity, float delta)
        {
            var a = GetOrCreate<ArenaUpgradeDefinition>(path);
            var so = new SerializedObject(a);
            so.FindProperty("id").stringValue = id;
            so.FindProperty("title").stringValue = title;
            so.FindProperty("description").stringValue = desc;
            so.FindProperty("kind").enumValueIndex = (int)kind;
            so.FindProperty("rarity").enumValueIndex = (int)rarity;
            so.FindProperty("statDelta").floatValue = delta;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(a);
            return a;
        }

        private static void AssignContent(
            ArenaProgressionContent content,
            ArenaRunBalanceConfig runBalance,
            ArenaPersistenceConfig persistence,
            ArenaUpgradePresentationConfig presentation,
            ChanceData rarity,
            ChanceData catCr,
            ChanceData catEpic,
            ChanceData catLeg,
            ChanceData statW,
            ArenaUpgradeDefinition[] upgrades)
        {
            var so = new SerializedObject(content);
            so.FindProperty("runBalance").objectReferenceValue = runBalance;
            so.FindProperty("persistence").objectReferenceValue = persistence;
            so.FindProperty("presentation").objectReferenceValue = presentation;
            so.FindProperty("rarityRoll").objectReferenceValue = rarity;
            so.FindProperty("categoryCommonRare").objectReferenceValue = catCr;
            so.FindProperty("categoryEpic").objectReferenceValue = catEpic;
            so.FindProperty("categoryLegendary").objectReferenceValue = catLeg;
            so.FindProperty("statUpgradeWeights").objectReferenceValue = statW;
            var list = so.FindProperty("upgrades");
            list.ClearArray();
            for (int i = 0; i < upgrades.Length; i++)
            {
                list.InsertArrayElementAtIndex(i);
                list.GetArrayElementAtIndex(i).objectReferenceValue = upgrades[i];
            }

            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(content);
        }
    }
}
