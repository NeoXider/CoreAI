using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace CoreAI.Editor
{
    public static class CoreAIBuildMenu
    {
        const string MainCoreAiScene = "Assets/CoreAiUnity/Scenes/_mainCoreAI.unity";
        const string RogueliteArenaScene = "Assets/_exampleGame/Scenes/RogueliteArena.unity";

        [MenuItem("CoreAI/Development/Set _mainCoreAI as first build scene")]
        public static void SetMainCoreAiFirstInBuild()
        {
            MoveSceneFirstInBuild(MainCoreAiScene, "_mainCoreAI");
        }

        [MenuItem("CoreAI/Development/Open _mainCoreAI scene")]
        public static void OpenMainCoreAiScene()
        {
            EditorSceneManager.OpenScene(MainCoreAiScene);
        }

        [MenuItem("CoreAI/Development/Example Game/Open RogueliteArena scene")]
        public static void OpenRogueliteArenaScene()
        {
            EditorSceneManager.OpenScene(RogueliteArenaScene);
        }

        [MenuItem("CoreAI/Development/Example Game/Set RogueliteArena as first build scene")]
        public static void SetRogueliteArenaFirstInBuild()
        {
            MoveSceneFirstInBuild(RogueliteArenaScene, "RogueliteArena");
        }

        static void MoveSceneFirstInBuild(string path, string labelForLog)
        {
            var scenes = EditorBuildSettings.scenes;
            var found = false;
            for (var i = 0; i < scenes.Length; i++)
            {
                if (scenes[i].path != path)
                    continue;
                found = true;
                if (i == 0)
                {
                    Debug.Log($"[CoreAI] {labelForLog} уже первая в Build Settings.");
                    return;
                }

                var first = scenes[0];
                scenes[0] = scenes[i];
                scenes[i] = first;
                break;
            }

            if (!found)
            {
                var list = new EditorBuildSettingsScene[scenes.Length + 1];
                list[0] = new EditorBuildSettingsScene(path, true);
                for (var i = 0; i < scenes.Length; i++)
                    list[i + 1] = scenes[i];
                scenes = list;
            }

            EditorBuildSettings.scenes = scenes;
            Debug.Log($"[CoreAI] Build Settings: первая сцена — {labelForLog}.");
        }
    }
}
