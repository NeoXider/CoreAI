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

            ArenaUnitBaselineConfig baseline = GetOrCreate<ArenaUnitBaselineConfig>($"{Root}/ArenaUnitBaseline.asset");
            ArenaPersistenceConfig persistence = GetOrCreate<ArenaPersistenceConfig>($"{Root}/ArenaPersistence.asset");
            ArenaRunBalanceConfig runBalance = GetOrCreate<ArenaRunBalanceConfig>($"{Root}/ArenaRunBalance.asset");
            ArenaUpgradePresentationConfig presentation =
                GetOrCreate<ArenaUpgradePresentationConfig>($"{Root}/ArenaUpgradePresentation.asset");
            LevelCurveDefinition sessionCurve = GetOrCreate<LevelCurveDefinition>($"{Root}/SessionLevelCurve.asset");
            LevelCurveDefinition metaCurve = GetOrCreate<LevelCurveDefinition>($"{Root}/MetaLevelCurve.asset");
            FillLevelCurve(sessionCurve);
            FillLevelCurve(metaCurve);
            AssignCurves(runBalance, sessionCurve, metaCurve);

            ChanceData rarity = GetOrCreate<ChanceData>($"{Root}/Chance_Rarity.asset");
            FillRarity(rarity);
            ChanceData catCr = GetOrCreate<ChanceData>($"{Root}/Chance_Category_CommonRare.asset");
            FillCategoryCommonRare(catCr);
            ChanceData catEpic = GetOrCreate<ChanceData>($"{Root}/Chance_Category_Epic.asset");
            FillCategoryEpic(catEpic);
            ChanceData catLeg = GetOrCreate<ChanceData>($"{Root}/Chance_Category_Legendary.asset");
            FillCategoryLegendary(catLeg);

            ArenaUpgradeDefinition upHp = CreateUpgrade($"{Root}/Up_StatHp_Common.asset", "stat_hp", "HP+",
                "+10 макс. HP", ArenaUpgradeKind.StatHp,
                ArenaRarity.Common, 10f);
            ArenaUpgradeDefinition upDmg = CreateUpgrade($"{Root}/Up_StatDmg_Rare.asset", "stat_dmg", "Урон+",
                "+5 урона", ArenaUpgradeKind.StatDamage,
                ArenaRarity.Rare, 5f);
            ArenaUpgradeDefinition upAspd = CreateUpgrade($"{Root}/Up_StatAspd_Epic.asset", "stat_aspd",
                "Скорость атаки", "Быстрее удары",
                ArenaUpgradeKind.StatAttackSpeed, ArenaRarity.Epic, 3f);
            ArenaUpgradeDefinition upPassive = CreateUpgrade($"{Root}/Up_Passive_Epic.asset", "passive_slot",
                "Пассивный слот", "+1 слот",
                ArenaUpgradeKind.PassiveSlotPlusOne, ArenaRarity.Epic, 0f);
            ArenaUpgradeDefinition upChoices = CreateUpgrade($"{Root}/Up_Choices_Legendary.asset", "extra_choices",
                "Больше карт", "+1 карта выбора",
                ArenaUpgradeKind.OfferExtraChoices, ArenaRarity.Legendary, 0f);
            ArenaUpgradeDefinition upDouble = CreateUpgrade($"{Root}/Up_DoublePick_Legendary.asset", "double_pick",
                "Двойной выбор",
                "Два апгрейда на следующем экране", ArenaUpgradeKind.LegendaryDoublePickThisWave,
                ArenaRarity.Legendary, 0f);

            ChanceData statWeights = GetOrCreate<ChanceData>($"{Root}/Chance_StatUpgradePool.asset");
            FillStatWeights(statWeights, 3);

            ArenaProgressionContent content =
                GetOrCreate<ArenaProgressionContent>($"{Root}/ArenaProgressionContent.asset");
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
            ArenaVsStyleWaveDifficulty a = AssetDatabase.LoadAssetAtPath<ArenaVsStyleWaveDifficulty>(path);
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
            {
                return;
            }

            string[] parts = path.Split('/');
            string cur = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = cur + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                {
                    AssetDatabase.CreateFolder(cur, parts[i]);
                }

                cur = next;
            }
        }

        private static T GetOrCreate<T>(string path) where T : ScriptableObject
        {
            T a = AssetDatabase.LoadAssetAtPath<T>(path);
            if (a != null)
            {
                return a;
            }

            a = ScriptableObject.CreateInstance<T>();
            AssetDatabase.CreateAsset(a, path);
            return a;
        }

        private static void FillLevelCurve(LevelCurveDefinition curve)
        {
            SerializedObject so = new(curve);
            SerializedProperty levels = so.FindProperty("_levels");
            levels.ClearArray();

            void Add(int level, int reqXp)
            {
                levels.InsertArrayElementAtIndex(levels.arraySize);
                SerializedProperty el = levels.GetArrayElementAtIndex(levels.arraySize - 1);
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

        private static void AssignCurves(ArenaRunBalanceConfig run, LevelCurveDefinition session,
            LevelCurveDefinition meta)
        {
            SerializedObject so = new(run);
            so.FindProperty("sessionLevelCurve").objectReferenceValue = session;
            so.FindProperty("metaLevelCurve").objectReferenceValue = meta;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(run);
        }

        private static void FillRarity(ChanceData data)
        {
            data.ClearChances();
            ChanceManager m = data.Manager;
            m.AddChance(50f);
            m.AddChance(30f);
            m.AddChance(15f);
            m.AddChance(5f);
            EditorUtility.SetDirty(data);
        }

        private static void FillCategoryCommonRare(ChanceData data)
        {
            data.ClearChances();
            ChanceManager m = data.Manager;
            m.AddChance(1f);
            EditorUtility.SetDirty(data);
        }

        private static void FillCategoryEpic(ChanceData data)
        {
            data.ClearChances();
            ChanceManager m = data.Manager;
            m.AddChance(70f);
            m.AddChance(30f);
            EditorUtility.SetDirty(data);
        }

        private static void FillCategoryLegendary(ChanceData data)
        {
            data.ClearChances();
            ChanceManager m = data.Manager;
            m.AddChance(50f);
            m.AddChance(25f);
            m.AddChance(25f);
            EditorUtility.SetDirty(data);
        }

        private static void FillStatWeights(ChanceData data, int count)
        {
            data.ClearChances();
            ChanceManager m = data.Manager;
            for (int i = 0; i < count; i++)
            {
                m.AddChance(1f);
            }

            EditorUtility.SetDirty(data);
        }

        private static ArenaUpgradeDefinition CreateUpgrade(string path, string id, string title, string desc,
            ArenaUpgradeKind kind, ArenaRarity rarity, float delta)
        {
            ArenaUpgradeDefinition a = GetOrCreate<ArenaUpgradeDefinition>(path);
            SerializedObject so = new(a);
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
            SerializedObject so = new(content);
            so.FindProperty("runBalance").objectReferenceValue = runBalance;
            so.FindProperty("persistence").objectReferenceValue = persistence;
            so.FindProperty("presentation").objectReferenceValue = presentation;
            so.FindProperty("rarityRoll").objectReferenceValue = rarity;
            so.FindProperty("categoryCommonRare").objectReferenceValue = catCr;
            so.FindProperty("categoryEpic").objectReferenceValue = catEpic;
            so.FindProperty("categoryLegendary").objectReferenceValue = catLeg;
            so.FindProperty("statUpgradeWeights").objectReferenceValue = statW;
            SerializedProperty list = so.FindProperty("upgrades");
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
