using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Umbreey;
using Umbreey.Editor;

namespace Suku.Editor
{
    /// <summary>
    /// 创建标题屏源 Prefab（直引用 Title.png）并走 UIGen 产出正式 Gen Prefab（按 name 加载贴图）。
    /// 同时配置 ABPack、写入启动场景，使点击 Play 直接进入 UITitle。
    /// </summary>
    public static class CreateTitleUIPrefab
    {
        private const string PrefabName = "UITitle";
        private const string TitleSpritePath = "Assets/AssetBundles/UI/Texture/Title.png";
        private const string SrcDir = "Assets/AssetBundles/UI/Prefab";
        private const string DstDir = "Assets/AssetBundles/UI/Prefab/Gen";
        private const string TextureDir = "Assets/AssetBundles/UI/Texture";
        private const string ConfigDir = "Assets/AssetBundles/UI/Config";
        private const string CodeDir = "Assets/HotUpdate/UI/Base";
        private const string LogicDir = "Assets/HotUpdate/UI";
        private const string ScenePath = "Assets/Scenes/SampleScene.unity";
        private const string AutoFlagKey = "Suku.AutoCreateTitleUI.Done";
        private const string FixBtnWhiteKey = "Suku.FixUITitleBtnWhite.v2";
        private const string PatchDifficultyKey = "Suku.PatchUITitleDifficulty.v2";

        [InitializeOnLoadMethod]
        private static void AutoCreateOnce()
        {
            if (EditorPrefs.GetBool(AutoFlagKey, false))
                return;
            if (File.Exists($"{SrcDir}/{PrefabName}.prefab") &&
                File.Exists($"{DstDir}/{PrefabName}.prefab"))
            {
                EditorPrefs.SetBool(AutoFlagKey, true);
                return;
            }

            EditorApplication.delayCall += () =>
            {
                if (EditorPrefs.GetBool(AutoFlagKey, false))
                    return;
                try
                {
                    Create();
                    EditorPrefs.SetBool(AutoFlagKey, true);
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"[Suku] AutoCreate Title UI failed: {e}");
                }
            };
        }

        [InitializeOnLoadMethod]
        private static void AutoFixButtonWhiteOnce()
        {
            if (EditorPrefs.GetBool(FixBtnWhiteKey, false))
                return;
            if (!File.Exists($"{DstDir}/{PrefabName}.prefab"))
                return;

            EditorApplication.delayCall += () =>
            {
                if (EditorPrefs.GetBool(FixBtnWhiteKey, false))
                    return;
                try
                {
                    FixGenButtonGraphics($"{DstDir}/{PrefabName}.prefab");
                    EditorPrefs.SetBool(FixBtnWhiteKey, true);
                    Debug.Log("[Suku] UITitle 按钮背板已钉到 White.png（Prefab）");
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"[Suku] Fix UITitle button white failed: {e.Message}");
                }
            };
        }

        [InitializeOnLoadMethod]
        private static void AutoPatchDifficultyOnce()
        {
            if (EditorPrefs.GetBool(PatchDifficultyKey, false))
                return;
            if (!File.Exists($"{DstDir}/{PrefabName}.prefab"))
                return;

            EditorApplication.delayCall += () =>
            {
                if (EditorPrefs.GetBool(PatchDifficultyKey, false))
                    return;
                try
                {
                    EnsureDirs();
                    PatchDifficultyIntoPrefab($"{SrcDir}/{PrefabName}.prefab");
                    PatchDifficultyIntoPrefab($"{DstDir}/{PrefabName}.prefab");
                    FixGenButtonGraphics($"{DstDir}/{PrefabName}.prefab");
                    EditorPrefs.SetBool(PatchDifficultyKey, true);
                    Debug.Log("[Suku] UITitle 已写入 Fade + 难度选择面板");
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"[Suku] Patch difficulty UI failed: {e.Message}");
                }
            };
        }

        /// <summary>给已有 UITitle Prefab 补上变黑遮罩与难度面板（不改 UIBind）。</summary>
        private static void PatchDifficultyIntoPrefab(string prefabPath)
        {
            if (!File.Exists(prefabPath)) return;

            var root = PrefabUtility.LoadPrefabContents(prefabPath);
            if (root == null) return;

            try
            {
                bool needPatch = root.transform.Find("Fade") == null
                                 || root.transform.Find("DifficultyPanel") == null
                                 || root.transform.Find("DifficultyPanel/BtnBack") == null;
                if (needPatch)
                {
                    var fade = root.transform.Find("Fade");
                    if (fade != null) Object.DestroyImmediate(fade.gameObject);
                    var panel = root.transform.Find("DifficultyPanel");
                    if (panel != null) Object.DestroyImmediate(panel.gameObject);
                    BuildFadeAndDifficulty(root.transform);
                    PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                }
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        [MenuItem("Suku/Rebuild UITitle Prefab")]
        [MenuItem("Suku/Create Title UI (UITitle)")]
        public static void Create()
        {
            EditorPrefs.DeleteKey(AutoFlagKey);
            EditorPrefs.DeleteKey(PatchDifficultyKey);
            EnsureDirs();
            ImportTitleSprite();
            EnsureABPacks();

            string srcPath = $"{SrcDir}/{PrefabName}.prefab";
            string logicPath = $"{LogicDir}/{PrefabName}.cs";

            var root = BuildHierarchy();
            PrefabUtility.SaveAsPrefabAsset(root, srcPath);
            Object.DestroyImmediate(root);

            // UIGen：源 Prefab → Gen Prefab（Image→UIImage，sprite 改为按文件名运行时加载）
            var srcPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(srcPath);
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(srcPrefab);
            instance.name = PrefabName;

            var config = new UIGen.Config
            {
                BaseUI = "BaseUI",
                BaseUISub = "BaseUISub",
                BasePath = CodeDir,
                SrcPrefabPath = SrcDir,
                DstPrefabPath = DstDir
            };
            UIGen.GenSingle(instance, config, srcPath);
            Object.DestroyImmediate(instance);
            FixGenButtonGraphics($"{DstDir}/{PrefabName}.prefab");
            // UIGen 后确保难度面板仍在 Gen 上（若被冲掉则补回）
            PatchDifficultyIntoPrefab($"{DstDir}/{PrefabName}.prefab");
            FixGenButtonGraphics($"{DstDir}/{PrefabName}.prefab");

            if (!File.Exists(logicPath))
                File.WriteAllText(logicPath, BuildLogicScript());

            UIDefGen.Gen(new UIGen.Config
            {
                BasePath = LogicDir,
                SrcPrefabPath = SrcDir,
                DstPrefabPath = DstDir
            });
            UICanvasToggleGen.Gen(new UIGen.Config
            {
                BasePath = LogicDir,
                SrcPrefabPath = SrcDir,
                DstPrefabPath = DstDir
            });

            SetupPlayScene();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<GameObject>(srcPath));
            Debug.Log(
                $"[Suku] Title UI 就绪:\n" +
                $"  源 Prefab（直引用图）: {srcPath}\n" +
                $"  Gen Prefab（按名加载）: {DstDir}/{PrefabName}.prefab\n" +
                $"  业务代码: {logicPath}\n" +
                $"  Play 入口场景: {ScenePath}\n" +
                $"之后改源 Prefab 请再跑菜单 Umbreey/UIGen。");
        }

        [MenuItem("Suku/Patch UITitle Difficulty Panel")]
        public static void PatchDifficultyMenu()
        {
            EditorPrefs.DeleteKey(PatchDifficultyKey);
            EnsureDirs();
            PatchDifficultyIntoPrefab($"{SrcDir}/{PrefabName}.prefab");
            PatchDifficultyIntoPrefab($"{DstDir}/{PrefabName}.prefab");
            FixGenButtonGraphics($"{DstDir}/{PrefabName}.prefab");
            EditorPrefs.SetBool(PatchDifficultyKey, true);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[Suku] UITitle Fade + DifficultyPanel patched");
        }

        /// <summary>供 -executeMethod 批处理调用。</summary>
        public static void CreateBatch()
        {
            Create();
            EditorApplication.Exit(0);
        }

        private static void EnsureDirs()
        {
            Directory.CreateDirectory(SrcDir);
            Directory.CreateDirectory(DstDir);
            Directory.CreateDirectory(TextureDir);
            Directory.CreateDirectory(ConfigDir);
            Directory.CreateDirectory(CodeDir);
            Directory.CreateDirectory(LogicDir);

            if (!File.Exists(BtnWhitePath))
            {
                var fallback = "Assets/Plugins/Umbreey/Resources/EmptySprite.png";
                if (File.Exists(fallback))
                    File.Copy(fallback, BtnWhitePath, true);
            }
        }

        private static void ImportTitleSprite()
        {
            if (!File.Exists(TitleSpritePath))
                throw new FileNotFoundException("缺少 Title.png", TitleSpritePath);

            AssetDatabase.ImportAsset(TitleSpritePath, ImportAssetOptions.ForceUpdate);
            var importer = AssetImporter.GetAtPath(TitleSpritePath) as TextureImporter;
            if (importer == null) return;

            bool dirty = false;
            if (importer.textureType != TextureImporterType.Sprite)
            {
                importer.textureType = TextureImporterType.Sprite;
                dirty = true;
            }
            if (importer.spriteImportMode != SpriteImportMode.Single)
            {
                importer.spriteImportMode = SpriteImportMode.Single;
                dirty = true;
            }
            if (importer.mipmapEnabled)
            {
                importer.mipmapEnabled = false;
                dirty = true;
            }
            if (dirty)
            {
                importer.SaveAndReimport();
            }
        }

        private static void EnsureABPacks()
        {
            var rule = LoadOrCreateAsset<ABRuleEachFiles>($"{ConfigDir}/ABRule_EachFiles.asset");

            var texPack = LoadOrCreateAsset<ABPack>($"{ConfigDir}/ABPack_UI_Texture.asset");
            texPack.PackName = "ui_tex";
            texPack.Extension = "*.png,*.jpg,*.jpeg";
            texPack.Rule = rule;
            texPack.Builtin = true;
            texPack.DirAssets = new List<Object>
            {
                AssetDatabase.LoadAssetAtPath<Object>(TextureDir)
            };
            EditorUtility.SetDirty(texPack);

            var prefabPack = LoadOrCreateAsset<ABPack>($"{ConfigDir}/ABPack_UI_Prefab.asset");
            prefabPack.PackName = "ui_prefab";
            prefabPack.Extension = "*.prefab";
            prefabPack.Rule = rule;
            prefabPack.Builtin = true;
            prefabPack.DirAssets = new List<Object>
            {
                AssetDatabase.LoadAssetAtPath<Object>(DstDir)
            };
            EditorUtility.SetDirty(prefabPack);
        }

        private static T LoadOrCreateAsset<T>(string path) where T : ScriptableObject
        {
            var existing = AssetDatabase.LoadAssetAtPath<T>(path);
            if (existing != null) return existing;

            var asset = ScriptableObject.CreateInstance<T>();
            AssetDatabase.CreateAsset(asset, path);
            return asset;
        }

        private static GameObject BuildHierarchy()
        {
            var root = new GameObject(PrefabName, typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = root.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 0;

            var scaler = root.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            // Title.png 为横屏构图
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;

            var rootRt = root.GetComponent<RectTransform>();
            rootRt.anchorMin = Vector2.zero;
            rootRt.anchorMax = Vector2.one;
            rootRt.offsetMin = Vector2.zero;
            rootRt.offsetMax = Vector2.zero;

            // e_Bg：全屏背景，源 Prefab 直接引用 Title.png（UIGen 后改为按名加载）
            var bg = CreateUIObject("e_Bg", root.transform);
            var bgImage = bg.AddComponent<Image>();
            bgImage.sprite = AssetDatabase.LoadAssetAtPath<Sprite>(TitleSpritePath);
            bgImage.type = Image.Type.Simple;
            bgImage.preserveAspect = false;
            bgImage.color = Color.white;
            bgImage.raycastTarget = true;
            StretchFull(bg.GetComponent<RectTransform>());

            if (bgImage.sprite == null)
                Debug.LogError($"[Suku] 未能加载 Sprite: {TitleSpritePath}（请确认 Texture Type = Sprite）");

            // 屏幕正中竖排菜单（一直显示）
            BuildMenuButtons(root.transform);
            // 新游戏：全屏变黑遮罩 + 难度选择（默认隐藏）
            BuildFadeAndDifficulty(root.transform);

            return root;
        }

        private static void BuildFadeAndDifficulty(Transform parent)
        {
            var white = LoadUiSprite();

            // Fade：全屏黑遮罩，初始透明；盖在主菜单之上
            var fade = CreateUIObject("Fade", parent);
            var fadeImg = fade.AddComponent<Image>();
            fadeImg.sprite = white;
            fadeImg.type = Image.Type.Simple;
            fadeImg.color = new Color(0f, 0f, 0f, 0f);
            fadeImg.raycastTarget = true;
            fade.AddComponent<UIPersist>();
            StretchFull(fade.GetComponent<RectTransform>());
            fade.SetActive(false);

            // 难度层：全屏容器（返回 + 中央难度选项一起显示/隐藏）
            var panel = CreateUIObject("DifficultyPanel", parent);
            StretchFull(panel.GetComponent<RectTransform>());

            // 左上角「返回」
            var back = CreateUIObject("BtnBack", panel.transform);
            var backRt = back.GetComponent<RectTransform>();
            backRt.anchorMin = backRt.anchorMax = new Vector2(0f, 1f);
            backRt.pivot = new Vector2(0f, 1f);
            backRt.anchoredPosition = new Vector2(36f, -36f);
            backRt.sizeDelta = new Vector2(160f, 64f);
            var backImg = back.AddComponent<Image>();
            backImg.sprite = white;
            backImg.type = Image.Type.Simple;
            backImg.color = Color.white;
            back.AddComponent<UIPersist>();
            var backBtn = back.AddComponent<Button>();
            backBtn.targetGraphic = backImg;
            var backLabel = CreateUIObject("Label", back.transform);
            StretchFull(backLabel.GetComponent<RectTransform>());
            var backText = backLabel.AddComponent<Text>();
            backText.text = "返回";
            backText.alignment = TextAnchor.MiddleCenter;
            backText.color = Color.black;
            backText.fontSize = 32;
            backText.raycastTarget = false;
            backText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (backText.font == null)
                backText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            backLabel.AddComponent<UIPersist>();

            // 中央难度选项
            var center = CreateUIObject("DifficultyCenter", panel.transform);
            var centerRt = center.GetComponent<RectTransform>();
            centerRt.anchorMin = centerRt.anchorMax = new Vector2(0.5f, 0.5f);
            centerRt.pivot = new Vector2(0.5f, 0.5f);
            centerRt.anchoredPosition = Vector2.zero;
            centerRt.sizeDelta = new Vector2(480f, 420f);

            var layout = center.AddComponent<VerticalLayoutGroup>();
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            layout.spacing = 20f;

            var fitter = center.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var titleGo = CreateUIObject("DifficultyTitle", center.transform);
            var titleLe = titleGo.AddComponent<LayoutElement>();
            titleLe.preferredWidth = 480f;
            titleLe.preferredHeight = 64f;
            titleLe.minHeight = 64f;
            var titleText = titleGo.AddComponent<Text>();
            titleText.text = "请选择游戏难度";
            titleText.alignment = TextAnchor.MiddleCenter;
            titleText.color = Color.white;
            titleText.fontSize = 40;
            titleText.raycastTarget = false;
            titleText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (titleText.font == null)
                titleText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            titleGo.AddComponent<UIPersist>();

            CreateMenuButton(center.transform, "BtnDiffEasy", "简单");
            CreateMenuButton(center.transform, "BtnDiffNormal", "普通");
            CreateMenuButton(center.transform, "BtnDiffHard", "困难");

            panel.SetActive(false);
        }

        private static void BuildMenuButtons(Transform parent)
        {
            var menu = CreateUIObject("Menu", parent);
            var menuRt = menu.GetComponent<RectTransform>();
            menuRt.anchorMin = menuRt.anchorMax = new Vector2(0.5f, 0.5f);
            menuRt.pivot = new Vector2(0.5f, 0.5f);
            menuRt.anchoredPosition = Vector2.zero;
            menuRt.sizeDelta = new Vector2(420, 480);

            var layout = menu.AddComponent<VerticalLayoutGroup>();
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            layout.spacing = 18f;
            layout.padding = new RectOffset(0, 0, 0, 0);

            var fitter = menu.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            CreateMenuButton(menu.transform, "e_BtnNewGame", "新游戏");
            CreateMenuButton(menu.transform, "e_BtnLoad", "读取存档");
            CreateMenuButton(menu.transform, "e_BtnChapter", "章节进度");
            CreateMenuButton(menu.transform, "e_BtnSettings", "设置");
            CreateMenuButton(menu.transform, "e_BtnQuit", "退出");
        }

        private static void CreateMenuButton(Transform parent, string name, string label)
        {
            var btnGo = CreateUIObject(name, parent);
            var le = btnGo.AddComponent<LayoutElement>();
            le.preferredWidth = 400f;
            le.preferredHeight = 72f;
            le.minHeight = 72f;

            var img = btnGo.AddComponent<Image>();
            img.sprite = LoadUiSprite();
            img.type = Image.Type.Simple;
            img.color = Color.white;
            // 保留直引用白底，UIGen 不要改成按名加载的 UIImage（否则背板易透明）
            btnGo.AddComponent<UIPersist>();

            var button = btnGo.AddComponent<Button>();
            button.targetGraphic = img;
            var colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(0.92f, 0.92f, 0.92f, 1f);
            colors.pressedColor = new Color(0.85f, 0.85f, 0.85f, 1f);
            colors.selectedColor = Color.white;
            button.colors = colors;

            var labelGo = CreateUIObject("Label", btnGo.transform);
            StretchFull(labelGo.GetComponent<RectTransform>());
            var text = labelGo.AddComponent<Text>();
            text.text = label;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = Color.black;
            text.fontSize = 36;
            text.raycastTarget = false;
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (text.font == null)
                text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        }

        private const string BtnWhitePath = TextureDir + "/White.png";

        private static Sprite LoadUiSprite()
        {
            var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(BtnWhitePath);
            if (sprite != null) return sprite;
            return AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");
        }

        /// <summary>
        /// 按钮背板用直引用 White.png，并清空 UIImage.SpriteName，避免运行时按名加载失败变透明。
        /// </summary>
        private static void FixGenButtonGraphics(string genPath)
        {
            var root = PrefabUtility.LoadPrefabContents(genPath);
            if (root == null) return;

            var white = AssetDatabase.LoadAssetAtPath<Sprite>(BtnWhitePath);
            foreach (var btn in root.GetComponentsInChildren<Button>(true))
            {
                var graphic = btn.GetComponent<Graphic>();
                if (graphic == null) continue;

                if (graphic is UIImage uiImage)
                {
                    uiImage.type = Image.Type.Simple;
                    uiImage.color = Color.white;
                    if (white != null) uiImage.sprite = white;
                    // 与 Title.png 相同：按名从 ui_tex 加载；Prefab 上保留直引用作编辑器预览
                    uiImage.SpriteName = "White.png";
                }
                else if (graphic is Image image)
                {
                    image.type = Image.Type.Simple;
                    image.color = Color.white;
                    if (white != null) image.sprite = white;
                }

                if (btn.GetComponent<UIPersist>() == null)
                    btn.gameObject.AddComponent<UIPersist>();

                btn.targetGraphic = graphic;
            }

            PrefabUtility.SaveAsPrefabAsset(root, genPath);
            PrefabUtility.UnloadPrefabContents(root);
        }

        private static GameObject CreateUIObject(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go;
        }

        private static void StretchFull(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            rt.pivot = new Vector2(0.5f, 0.5f);
        }

        private static string BuildLogicScript()
        {
            return
@"using UnityEngine;
using UnityEngine.UI;
using Umbreey;

[UIHandler(""UITitle"")]
public class UITitle : BaseUITitle
{
    // 逻辑以 Assets/HotUpdate/UI/UITitle.cs 为准
}
";
        }

        private static void SetupPlayScene()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            EnsureComponentInScene<Umbreey.Umbreey>("Main");
            EnsureEventSystem();
            EnsureMainEntry();

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);

            // 保证 Build Settings 含启动场景
            var scenes = EditorBuildSettings.scenes;
            bool found = false;
            foreach (var s in scenes)
            {
                if (s.path == ScenePath)
                {
                    found = true;
                    break;
                }
            }
            if (!found)
            {
                var list = new List<EditorBuildSettingsScene>(scenes)
                {
                    new EditorBuildSettingsScene(ScenePath, true)
                };
                EditorBuildSettings.scenes = list.ToArray();
            }
        }

        private static T EnsureComponentInScene<T>(string goName) where T : Component
        {
            var existing = Object.FindObjectOfType<T>();
            if (existing != null) return existing;

            var go = GameObject.Find(goName) ?? new GameObject(goName);
            return go.GetComponent<T>() ?? go.AddComponent<T>();
        }

        private static void EnsureEventSystem()
        {
            if (Object.FindObjectOfType<EventSystem>() != null) return;
            var es = new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
            Undo.RegisterCreatedObjectUndo(es, "Create EventSystem");
        }

        private static void EnsureMainEntry()
        {
            var main = Object.FindObjectOfType<Scripts.Main>();
            if (main != null) return;

            var umbreey = Object.FindObjectOfType<Umbreey.Umbreey>();
            var go = umbreey != null ? umbreey.gameObject : (GameObject.Find("Main") ?? new GameObject("Main"));
            go.name = "Main";
            if (go.GetComponent<Umbreey.Umbreey>() == null)
                go.AddComponent<Umbreey.Umbreey>();
            go.AddComponent<Scripts.Main>();
        }
    }
}
