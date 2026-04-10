using System.Collections.Generic;
using CoreAI.Crafting;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// EditMode тесты для CompatibilityChecker и JsonSchemaValidator.
    /// </summary>
    public sealed class CompatibilityAndSchemaEditModeTests
    {
        // ═══════════════════════════════════════════════════
        // CompatibilityChecker Tests
        // ═══════════════════════════════════════════════════

        [Test]
        public void Compatibility_EmptyIngredients_ReturnsIncompatible()
        {
            CompatibilityChecker checker = new();
            CompatibilityResult result = checker.Check(new List<string>());
            Assert.IsFalse(result.IsCompatible);
            Assert.AreEqual(0f, result.CompatibilityScore);
        }

        [Test]
        public void Compatibility_SingleIngredient_ReturnsCompatible()
        {
            CompatibilityChecker checker = new();
            CompatibilityResult result = checker.Check("IronOre");
            Assert.IsTrue(result.IsCompatible);
            Assert.AreEqual(1.0f, result.CompatibilityScore);
        }

        [Test]
        public void Compatibility_TwoIngredients_NoRules_ReturnsDefault()
        {
            CompatibilityChecker checker = new(defaultScore: 0.8f);
            CompatibilityResult result = checker.Check("IronOre", "Coal");
            Assert.IsTrue(result.IsCompatible);
            Assert.AreEqual(0.8f, result.CompatibilityScore);
        }

        [Test]
        public void Compatibility_PairRule_BlockingScore_ReturnsIncompatible()
        {
            CompatibilityChecker checker = new();
            checker.AddRule("Fire", "Water", 0f, "Fire and Water cancel each other");

            CompatibilityResult result = checker.Check("Fire", "Water");
            Assert.IsFalse(result.IsCompatible);
            Assert.AreEqual(0f, result.CompatibilityScore);
            Assert.IsTrue(result.Warnings.Count > 0);
        }

        [Test]
        public void Compatibility_PairRule_SynergyScore_ReturnsBonuses()
        {
            CompatibilityChecker checker = new();
            checker.AddRule("Fire", "Earth", 1.5f, "Fire and Earth form lava");

            CompatibilityResult result = checker.Check("Fire", "Earth");
            Assert.IsTrue(result.IsCompatible);
            Assert.Greater(result.CompatibilityScore, 1.0f);
            Assert.IsTrue(result.Bonuses.Count > 0);
        }

        [Test]
        public void Compatibility_PairRule_OrderIndependent()
        {
            CompatibilityChecker checker = new();
            checker.AddRule("Fire", "Water", 0f, "Incompatible");

            // Проверяем что порядок не важен
            CompatibilityResult r1 = checker.Check("Fire", "Water");
            CompatibilityResult r2 = checker.Check("Water", "Fire");
            Assert.IsFalse(r1.IsCompatible);
            Assert.IsFalse(r2.IsCompatible);
        }

        [Test]
        public void Compatibility_GroupRule_ThreeElements_Matches()
        {
            CompatibilityChecker checker = new();
            checker.AddGroupRule(1.8f, "Triple synergy: Fire+Earth+Air", "Fire", "Earth", "Air");

            CompatibilityResult result = checker.Check("Fire", "Earth", "Air");
            Assert.IsTrue(result.IsCompatible);
            Assert.Greater(result.CompatibilityScore, 1.0f);
            Assert.IsTrue(result.Bonuses.Count > 0);
        }

        [Test]
        public void Compatibility_GroupRule_FourElements_Matches()
        {
            CompatibilityChecker checker = new();
            checker.AddGroupRule(2.0f, "Master recipe: all four elements", "Fire", "Water", "Earth", "Air");

            CompatibilityResult result = checker.Check("Fire", "Water", "Earth", "Air");
            Assert.IsTrue(result.IsCompatible);
            // Score capped at 2.0
            Assert.AreEqual(2.0f, result.CompatibilityScore);
        }

        [Test]
        public void Compatibility_GroupRule_DoesNotMatchSubset()
        {
            CompatibilityChecker checker = new();
            checker.AddGroupRule(0f, "Four elements cancel out", "Fire", "Water", "Earth", "Air");

            // Только 3 из 4 — правило на 4 не должно матчить
            CompatibilityResult result = checker.Check("Fire", "Water", "Earth");
            Assert.IsTrue(result.IsCompatible); // default score 1.0
        }

        [Test]
        public void Compatibility_GroupRule_MatchesAsSubsetOfLargerInput()
        {
            CompatibilityChecker checker = new();
            checker.AddGroupRule(1.5f, "Fire+Earth synergy", "Fire", "Earth");

            // 3 ингредиента, но парное правило Fire+Earth всё равно матчит
            CompatibilityResult result = checker.Check("Fire", "Earth", "Wind");
            Assert.IsTrue(result.IsCompatible);
            Assert.Greater(result.CompatibilityScore, 1.0f);
        }

        [Test]
        public void Compatibility_GroupRule_LargerRuleHasMoreWeight()
        {
            CompatibilityChecker checker = new();
            // Парное правило: Fire+Earth = 0.5 (плохо)
            checker.AddRule("Fire", "Earth", 0.5f, "Pair: weak");
            // Тройное правило: Fire+Earth+Air = 1.8 (синергия, перевешивает)
            checker.AddGroupRule(1.8f, "Triple: synergy!", "Fire", "Earth", "Air");

            CompatibilityResult result = checker.Check("Fire", "Earth", "Air");
            Assert.IsTrue(result.IsCompatible);
            // Тройное правило весит 3 сравнительно с парным весом 2
            // (0.5*2 + 1.8*3) / (2+3) = (1.0 + 5.4) / 5 = 1.28
            Assert.Greater(result.CompatibilityScore, 1.0f, "Triple rule should outweigh pair rule");
        }

        [Test]
        public void Compatibility_ElementGroups_MatchesByGroup()
        {
            CompatibilityChecker checker = new();
            checker.RegisterElement("IronOre", "Metal");
            checker.RegisterElement("CopperOre", "Metal");
            checker.RegisterElement("WaterFlask", "Water");
            checker.AddRule("Metal", "Water", 0.3f, "Metal rusts in water");

            // IronOre → Metal, WaterFlask → Water → правило Metal+Water матчит
            CompatibilityResult result = checker.Check("IronOre", "WaterFlask");
            Assert.IsTrue(result.IsCompatible);
            Assert.Less(result.CompatibilityScore, 1.0f);
            Assert.IsTrue(result.Warnings.Count > 0);
        }

        [Test]
        public void Compatibility_ElementGroups_ThreeIngredients_GroupMatch()
        {
            CompatibilityChecker checker = new();
            checker.RegisterElement("FireStone", "Fire");
            checker.RegisterElement("IronOre", "Metal");
            checker.RegisterElement("WaterFlask", "Water");
            checker.AddGroupRule(0f, "Fire+Metal+Water = explosion!", "Fire", "Metal", "Water");

            CompatibilityResult result = checker.Check("FireStone", "IronOre", "WaterFlask");
            Assert.IsFalse(result.IsCompatible);
            Assert.AreEqual(0f, result.CompatibilityScore);
        }

        [Test]
        public void Compatibility_CustomValidator_Rejects()
        {
            CompatibilityChecker checker = new();
            checker.AddValidator(new RejectAllValidator());

            CompatibilityResult result = checker.Check("A", "B");
            Assert.IsFalse(result.IsCompatible);
        }

        [Test]
        public void Compatibility_CustomValidator_AddsBonuses()
        {
            CompatibilityChecker checker = new();
            checker.AddValidator(new BonusValidator());

            CompatibilityResult result = checker.Check("A", "B");
            Assert.IsTrue(result.IsCompatible);
            Assert.IsTrue(result.Bonuses.Count > 0);
        }

        [Test]
        public void Compatibility_RuleCount_TracksAddedRules()
        {
            CompatibilityChecker checker = new();
            Assert.AreEqual(0, checker.RuleCount);
            checker.AddRule("A", "B", 1.0f);
            Assert.AreEqual(1, checker.RuleCount);
            checker.AddGroupRule(1.0f, "triple", "A", "B", "C");
            Assert.AreEqual(2, checker.RuleCount);
        }

        [Test]
        public void Compatibility_ElementCount_TracksRegistered()
        {
            CompatibilityChecker checker = new();
            Assert.AreEqual(0, checker.ElementCount);
            checker.RegisterElement("IronOre", "Metal");
            Assert.AreEqual(1, checker.ElementCount);
        }

        [Test]
        public void Compatibility_MixedPairAndGroupRules_BothApply()
        {
            CompatibilityChecker checker = new();
            checker.AddRule("A", "B", 0.8f, "A+B weak");
            checker.AddRule("C", "D", 1.2f, "C+D good");
            checker.AddGroupRule(1.5f, "A+B+C+D together = great", "A", "B", "C", "D");

            CompatibilityResult result = checker.Check("A", "B", "C", "D");
            Assert.IsTrue(result.IsCompatible);
            // All 3 rules match. Weighted: (0.8*2 + 1.2*2 + 1.5*4) / (2+2+4) = (1.6+2.4+6.0)/8 = 1.25
            Assert.Greater(result.CompatibilityScore, 1.0f);
        }

        [Test]
        public void CompatibilityRule_Pair_FactoryMethod()
        {
            CompatibilityRule rule = CompatibilityRule.Pair("A", "B", 0.5f, "test");
            Assert.AreEqual(2, rule.Size);
            Assert.AreEqual(0.5f, rule.Score);
            Assert.AreEqual("test", rule.Reason);
        }

        [Test]
        public void CompatibilityRule_Group_FactoryMethod()
        {
            CompatibilityRule rule = CompatibilityRule.Group(1.5f, "synergy", "A", "B", "C");
            Assert.AreEqual(3, rule.Size);
            Assert.AreEqual(1.5f, rule.Score);
        }

        [Test]
        public void CompatibilityRule_IsBlocking_WhenScoreZero()
        {
            CompatibilityRule rule = CompatibilityRule.Pair("A", "B", 0f);
            Assert.IsTrue(rule.IsBlocking);
        }

        [Test]
        public void CompatibilityRule_IsNotBlocking_WhenScorePositive()
        {
            CompatibilityRule rule = CompatibilityRule.Pair("A", "B", 0.1f);
            Assert.IsFalse(rule.IsBlocking);
        }

        // ═══════════════════════════════════════════════════
        // JsonSchemaValidator Tests
        // ═══════════════════════════════════════════════════

        [Test]
        public void Schema_EmptyJson_ReturnsErrors()
        {
            JsonSchemaValidator schema = new("Test");
            JsonValidationResult result = schema.Validate("");
            Assert.IsFalse(result.IsValid);
            Assert.IsTrue(result.Errors.Count > 0);
        }

        [Test]
        public void Schema_InvalidJson_ReturnsErrors()
        {
            JsonSchemaValidator schema = new("Test");
            JsonValidationResult result = schema.Validate("not json at all");
            Assert.IsFalse(result.IsValid);
            Assert.IsTrue(result.ErrorSummary.Contains("Invalid JSON"));
        }

        [Test]
        public void Schema_MissingRequiredField_ReturnsError()
        {
            JsonSchemaValidator schema = new("CraftResult");
            schema.AddField("itemName", "string", required: true);

            JsonValidationResult result = schema.Validate("{}");
            Assert.IsFalse(result.IsValid);
            Assert.IsTrue(result.ErrorSummary.Contains("Missing required field 'itemName'"));
        }

        [Test]
        public void Schema_AllRequiredFieldsPresent_IsValid()
        {
            JsonSchemaValidator schema = new("CraftResult");
            schema.AddField("itemName", "string", required: true);
            schema.AddField("quality", "number", required: true);

            JsonValidationResult result = schema.Validate("{\"itemName\":\"Sword\",\"quality\":85}");
            Assert.IsTrue(result.IsValid);
            Assert.IsNotNull(result.ParsedObject);
        }

        [Test]
        public void Schema_WrongType_String_ReturnsError()
        {
            JsonSchemaValidator schema = new("Test");
            schema.AddField("name", "string", required: true);

            JsonValidationResult result = schema.Validate("{\"name\": 42}");
            Assert.IsFalse(result.IsValid);
            Assert.IsTrue(result.ErrorSummary.Contains("expected string"));
        }

        [Test]
        public void Schema_WrongType_Number_ReturnsError()
        {
            JsonSchemaValidator schema = new("Test");
            schema.AddField("value", "number", required: true);

            JsonValidationResult result = schema.Validate("{\"value\": \"not a number\"}");
            Assert.IsFalse(result.IsValid);
            Assert.IsTrue(result.ErrorSummary.Contains("expected number"));
        }

        [Test]
        public void Schema_NumberBelowMin_ReturnsError()
        {
            JsonSchemaValidator schema = new("Test");
            schema.AddField("quality", "number", required: true, min: 0, max: 100);

            JsonValidationResult result = schema.Validate("{\"quality\": -5}");
            Assert.IsFalse(result.IsValid);
            Assert.IsTrue(result.ErrorSummary.Contains("below minimum"));
        }

        [Test]
        public void Schema_NumberAboveMax_ReturnsError()
        {
            JsonSchemaValidator schema = new("Test");
            schema.AddField("quality", "number", required: true, min: 0, max: 100);

            JsonValidationResult result = schema.Validate("{\"quality\": 150}");
            Assert.IsFalse(result.IsValid);
            Assert.IsTrue(result.ErrorSummary.Contains("exceeds maximum"));
        }

        [Test]
        public void Schema_NumberInRange_IsValid()
        {
            JsonSchemaValidator schema = new("Test");
            schema.AddField("quality", "number", required: true, min: 0, max: 100);

            JsonValidationResult result = schema.Validate("{\"quality\": 85.5}");
            Assert.IsTrue(result.IsValid);
        }

        [Test]
        public void Schema_EnumValue_Valid()
        {
            JsonSchemaValidator schema = new("Test");
            schema.AddField("rarity", "string", required: true,
                allowedValues: new[] { "common", "rare", "epic", "legendary" });

            JsonValidationResult result = schema.Validate("{\"rarity\": \"epic\"}");
            Assert.IsTrue(result.IsValid);
        }

        [Test]
        public void Schema_EnumValue_Invalid()
        {
            JsonSchemaValidator schema = new("Test");
            schema.AddField("rarity", "string", required: true,
                allowedValues: new[] { "common", "rare", "epic", "legendary" });

            JsonValidationResult result = schema.Validate("{\"rarity\": \"mythic\"}");
            Assert.IsFalse(result.IsValid);
            Assert.IsTrue(result.ErrorSummary.Contains("not in allowed values"));
        }

        [Test]
        public void Schema_EnumValue_CaseInsensitive()
        {
            JsonSchemaValidator schema = new("Test");
            schema.AddField("rarity", "string", required: true,
                allowedValues: new[] { "common", "rare" });

            JsonValidationResult result = schema.Validate("{\"rarity\": \"Rare\"}");
            Assert.IsTrue(result.IsValid);
        }

        [Test]
        public void Schema_OptionalField_MissingIsOk()
        {
            JsonSchemaValidator schema = new("Test");
            schema.AddField("name", "string", required: true);
            schema.AddField("description", "string", required: false);

            JsonValidationResult result = schema.Validate("{\"name\": \"Sword\"}");
            Assert.IsTrue(result.IsValid);
        }

        [Test]
        public void Schema_BooleanField_Validates()
        {
            JsonSchemaValidator schema = new("Test");
            schema.AddField("isActive", "boolean", required: true);

            Assert.IsTrue(schema.Validate("{\"isActive\": true}").IsValid);
            Assert.IsFalse(schema.Validate("{\"isActive\": \"yes\"}").IsValid);
        }

        [Test]
        public void Schema_ArrayField_Validates()
        {
            JsonSchemaValidator schema = new("Test");
            schema.AddField("ingredients", "array", required: true);

            Assert.IsTrue(schema.Validate("{\"ingredients\": [\"a\",\"b\"]}").IsValid);
            Assert.IsFalse(schema.Validate("{\"ingredients\": \"not array\"}").IsValid);
        }

        [Test]
        public void Schema_ObjectField_Validates()
        {
            JsonSchemaValidator schema = new("Test");
            schema.AddField("stats", "object", required: true);

            Assert.IsTrue(schema.Validate("{\"stats\": {\"hp\": 100}}").IsValid);
            Assert.IsFalse(schema.Validate("{\"stats\": 42}").IsValid);
        }

        [Test]
        public void Schema_IntegerField_AcceptsWholeFloat()
        {
            JsonSchemaValidator schema = new("Test");
            schema.AddField("count", "integer", required: true);

            Assert.IsTrue(schema.Validate("{\"count\": 5}").IsValid);
            Assert.IsTrue(schema.Validate("{\"count\": 5.0}").IsValid);
            Assert.IsFalse(schema.Validate("{\"count\": 5.7}").IsValid);
        }

        [Test]
        public void Schema_MarkdownFences_StrippedBeforeParsing()
        {
            JsonSchemaValidator schema = new("Test");
            schema.AddField("name", "string", required: true);

            JsonValidationResult result = schema.Validate("```json\n{\"name\": \"Sword\"}\n```");
            Assert.IsTrue(result.IsValid);
        }

        [Test]
        public void Schema_MultipleErrors_AllReported()
        {
            JsonSchemaValidator schema = new("CraftResult");
            schema.AddField("itemName", "string", required: true);
            schema.AddField("quality", "number", required: true, min: 0, max: 100);
            schema.AddField("rarity", "string", required: true,
                allowedValues: new[] { "common", "rare" });

            // All three fields wrong or missing
            JsonValidationResult result = schema.Validate("{\"quality\": -1, \"rarity\": \"mythic\"}");
            Assert.IsFalse(result.IsValid);
            Assert.AreEqual(3, result.Errors.Count); // missing itemName, quality below min, rarity invalid
        }

        [Test]
        public void Schema_ToPromptDescription_ContainsFieldNames()
        {
            JsonSchemaValidator schema = new("CraftResult");
            schema.AddField("itemName", "string", required: true, description: "Name of the crafted item");
            schema.AddField("quality", "number", required: true, min: 0, max: 100);

            string prompt = schema.ToPromptDescription();
            Assert.IsTrue(prompt.Contains("itemName"));
            Assert.IsTrue(prompt.Contains("quality"));
            Assert.IsTrue(prompt.Contains("REQUIRED"));
            Assert.IsTrue(prompt.Contains("CraftResult"));
        }

        [Test]
        public void Schema_FieldCount_TracksAddedFields()
        {
            JsonSchemaValidator schema = new("Test");
            Assert.AreEqual(0, schema.FieldCount);
            schema.AddField("a", "string");
            Assert.AreEqual(1, schema.FieldCount);
            schema.AddField("b", "number");
            Assert.AreEqual(2, schema.FieldCount);
        }

        [Test]
        public void Schema_SchemaName_Accessible()
        {
            JsonSchemaValidator schema = new("MyCraft");
            Assert.AreEqual("MyCraft", schema.SchemaName);
        }

        [Test]
        public void Schema_FluentApi_Chainable()
        {
            JsonSchemaValidator schema = new JsonSchemaValidator("Test")
                .AddField("a", "string", required: true)
                .AddField("b", "number")
                .AddField("c", "boolean");

            Assert.AreEqual(3, schema.FieldCount);
        }

        [Test]
        public void Schema_ComplexCraftResult_FullValidation()
        {
            // Реалистичный сценарий: CoreMechanicAI возвращает результат крафта
            JsonSchemaValidator schema = new JsonSchemaValidator("CraftResult")
                .AddField("itemName", "string", required: true, description: "Name of crafted item")
                .AddField("quality", "number", required: true, min: 0, max: 100)
                .AddField("rarity", "string", required: true,
                    allowedValues: new[] { "common", "uncommon", "rare", "epic", "legendary" })
                .AddField("durability", "integer", required: true, min: 1, max: 1000)
                .AddField("effects", "array", required: false)
                .AddField("isCursed", "boolean", required: false);

            string validJson = @"{
                ""itemName"": ""Flaming Sword"",
                ""quality"": 87.5,
                ""rarity"": ""epic"",
                ""durability"": 450,
                ""effects"": [""fire_damage"", ""glow""],
                ""isCursed"": false
            }";

            JsonValidationResult result = schema.Validate(validJson);
            Assert.IsTrue(result.IsValid, $"Expected valid but got errors: {result.ErrorSummary}");
            Assert.AreEqual("Flaming Sword", result.ParsedObject["itemName"].ToString());
        }

        // ═══════════════════════════════════════════════════
        // CompatibilityLlmTool Tests
        // ═══════════════════════════════════════════════════

        [Test]
        public void CompatibilityTool_Properties_AreValid()
        {
            CompatibilityChecker checker = new();
            CompatibilityLlmTool tool = new(checker);

            Assert.AreEqual("check_compatibility", tool.Name);
            Assert.IsTrue(tool.Description.Contains("compatibility"));
            Assert.IsTrue(tool.ParametersSchema.Contains("ingredients"));
        }

        [Test]
        public void CompatibilityTool_NullChecker_Throws()
        {
            Assert.Throws<System.ArgumentNullException>(() => new CompatibilityLlmTool(null));
        }

        [Test]
        public void CompatibilityTool_EmptyIngredients_ReturnsError()
        {
            CompatibilityChecker checker = new();
            CompatibilityLlmTool tool = new(checker);

            string result = tool.ExecuteAsync(new string[0]).Result;
            Assert.IsTrue(result.Contains("\"Success\":false") || result.Contains("\"Success\": false"));
        }

        [Test]
        public void CompatibilityTool_SingleIngredient_ReturnsError()
        {
            CompatibilityChecker checker = new();
            CompatibilityLlmTool tool = new(checker);

            string result = tool.ExecuteAsync(new[] { "OnlyOne" }).Result;
            Assert.IsTrue(result.Contains("at least 2") || result.Contains("At least 2"));
        }

        [Test]
        public void CompatibilityTool_ValidIngredients_ReturnsResult()
        {
            CompatibilityChecker checker = new();
            checker.AddRule("Fire", "Earth", 1.5f, "Lava synergy");
            CompatibilityLlmTool tool = new(checker);

            string result = tool.ExecuteAsync(new[] { "Fire", "Earth" }).Result;
            Assert.IsTrue(result.Contains("\"Success\":true") || result.Contains("\"Success\": true"));
            Assert.IsTrue(result.Contains("\"IsCompatible\":true") || result.Contains("\"IsCompatible\": true"));
        }

        [Test]
        public void CompatibilityTool_ThreeIngredients_Works()
        {
            CompatibilityChecker checker = new();
            checker.AddGroupRule(0f, "Explosive combo!", "Fire", "Oil", "Gunpowder");
            CompatibilityLlmTool tool = new(checker);

            string result = tool.ExecuteAsync(new[] { "Fire", "Oil", "Gunpowder" }).Result;
            Assert.IsTrue(result.Contains("\"IsCompatible\":false") || result.Contains("\"IsCompatible\": false"));
        }

        // ═══════════════════════════════════════════════════
        // Test helpers
        // ═══════════════════════════════════════════════════

        private sealed class RejectAllValidator : ICompatibilityValidator
        {
            public CompatibilityResult Validate(IReadOnlyList<string> ingredients)
            {
                return new CompatibilityResult
                {
                    IsCompatible = false,
                    CompatibilityScore = 0f,
                    Reason = "Rejected by custom validator"
                };
            }
        }

        private sealed class BonusValidator : ICompatibilityValidator
        {
            public CompatibilityResult Validate(IReadOnlyList<string> ingredients)
            {
                return new CompatibilityResult
                {
                    IsCompatible = true,
                    CompatibilityScore = 1.2f,
                    Bonuses = new List<string> { "Custom bonus applied" }
                };
            }
        }
    }
}
