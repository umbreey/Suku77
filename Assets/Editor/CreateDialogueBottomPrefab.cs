using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using Umbreey;
using Umbreey.Editor;

namespace Suku.Editor
{
    /// <summary>
    /// 创建 UIDialogueBottom（源 + Gen）。
    /// DialogueFrame：底部半透明白框 + 正文/说话人文本。
    /// </summary>
    public static class CreateDialogueBottomPrefab
    {
        private const string PrefabName = "UIDialogueBottom";
        private const string PrefabDir = "Assets/AssetBundles/UI/Prefab";
        private const string GenDir = PrefabDir + "/Gen";
        private const string SrcPath = PrefabDir + "/" + PrefabName + ".prefab";
        private const string GenPath = GenDir + "/" + PrefabName + ".prefab";
        private const string WhitePath = "Assets/AssetBundles/UI/Texture/White.png";
        private const string CodeDir = "Assets/HotUpdate/UI/Base";
        private const string LogicDir = "Assets/HotUpdate/UI";
        private const string FlagKey = "Suku.UIDialogueBottom.v2";

        private const float TextPad = 20f;
        private const int BodyFontSize = 40;
        private const int SpeakerFontSize = 28;

        [InitializeOnLoadMethod]
        private static void AutoEnsure()
        {
            EditorApplication.delayCall += () =>
            {
                try
                {
                    RenameLegacy();
                    if (!File.Exists(SrcPath) || !File.Exists(GenPath) || !EditorPrefs.GetBool(FlagKey, false))
                        Create();
                    else
                        EnsureDialogueContent(SrcPath);
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"[Suku] UIDialogueBottom setup: {e.Message}");
                }
            };
        }

        [MenuItem("Suku/Create UIDialogueBottom Prefab")]
        public static void Create()
        {
            Directory.CreateDirectory(PrefabDir);
            Directory.CreateDirectory(GenDir);
            RenameLegacy();

            var root = BuildHierarchy();
            PrefabUtility.SaveAsPrefabAsset(root, SrcPath);
            Object.DestroyImmediate(root);

            var srcPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(SrcPath);
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(srcPrefab);
            instance.name = PrefabName;
            UIGen.GenSingle(instance, new UIGen.Config
            {
                BaseUI = "BaseUI",
                BaseUISub = "BaseUISub",
                BasePath = CodeDir,
                SrcPrefabPath = PrefabDir,
                DstPrefabPath = GenDir
            }, SrcPath);
            Object.DestroyImmediate(instance);

            EnsureLogic();
            var cfg = new UIGen.Config { BasePath = LogicDir, SrcPrefabPath = PrefabDir, DstPrefabPath = GenDir };
            UIDefGen.Gen(cfg);
            UICanvasToggleGen.Gen(cfg);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            EditorPrefs.SetBool(FlagKey, true);
            Debug.Log($"[Suku] {PrefabName} ready");
        }

        private static void RenameLegacy()
        {
            TryMove($"{PrefabDir}/DialogueBottom.prefab", SrcPath);
            TryMove($"{GenDir}/DialogueBottom.prefab", GenPath);
            TryMove($"{PrefabDir}/UIBottomFrame.prefab", SrcPath);
            TryMove($"{GenDir}/UIBottomFrame.prefab", GenPath);

            if (File.Exists(SrcPath) || File.Exists(GenPath))
            {
                PatchPrefabNames(SrcPath);
                PatchPrefabNames(GenPath);
            }
        }

        private static void TryMove(string from, string to)
        {
            if (!File.Exists(from) || File.Exists(to)) return;
            AssetDatabase.MoveAsset(from, to);
        }

        private static void PatchPrefabNames(string path)
        {
            if (!File.Exists(path)) return;
            var root = PrefabUtility.LoadPrefabContents(path);
            root.name = PrefabName;
            var frame = root.transform.Find("DialogueFrame") ?? root.transform.Find("Frame");
            if (frame != null) frame.name = "DialogueFrame";
            PrefabUtility.SaveAsPrefabAsset(root, path);
            PrefabUtility.UnloadPrefabContents(root);
        }

        private static void EnsureDialogueContent(string path)
        {
            if (!File.Exists(path)) return;
            var root = PrefabUtility.LoadPrefabContents(path);
            var frame = root.transform.Find("DialogueFrame");
            if (frame != null && frame.Find("e_DialogueText") == null)
            {
                AddDialogueChildren(frame.gameObject);
                PrefabUtility.SaveAsPrefabAsset(root, path);
                Debug.Log($"[Suku] patched dialogue content: {path}");
            }
            PrefabUtility.UnloadPrefabContents(root);
        }

        private static void EnsureLogic()
        {
            var logic = $"{LogicDir}/UIDialogueBottom.cs";
            if (!File.Exists(logic))
            {
                File.WriteAllText(logic,
@"using UnityEngine;
using Umbreey;

[UIHandler(""UIDialogueBottom"")]
public class UIDialogueBottom : BaseUIDialogueBottom
{
    protected override void OnOpened()
    {
        base.OnOpened();
    }
}
");
            }
            var old = $"{LogicDir}/DialogueBottom.cs";
            if (File.Exists(old)) AssetDatabase.DeleteAsset(old);
        }

        private static GameObject BuildHierarchy()
        {
            var root = new GameObject(PrefabName, typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = root.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 10;
            var scaler = root.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;
            var rootRt = root.GetComponent<RectTransform>();
            rootRt.anchorMin = Vector2.zero;
            rootRt.anchorMax = Vector2.one;
            rootRt.offsetMin = Vector2.zero;
            rootRt.offsetMax = Vector2.zero;

            var frame = new GameObject("DialogueFrame", typeof(RectTransform));
            frame.transform.SetParent(root.transform, false);
            var frameRt = frame.GetComponent<RectTransform>();
            frameRt.anchorMin = new Vector2(0f, 0f);
            frameRt.anchorMax = new Vector2(1f, 0.25f);
            frameRt.pivot = new Vector2(0.5f, 0.5f);
            frameRt.offsetMin = new Vector2(0f, 50f);
            frameRt.offsetMax = new Vector2(0f, -50f);
            var img = frame.AddComponent<Image>();
            img.sprite = LoadWhiteSprite();
            img.type = Image.Type.Simple;
            img.color = new Color(1f, 1f, 1f, 0.5f);
            frame.AddComponent<UIPersist>();

            AddDialogueChildren(frame);
            return root;
        }

        private static void AddDialogueChildren(GameObject frame)
        {
            // 正文：相对 DialogueFrame 四边各缩进 20
            var body = CreateUIObject("e_DialogueText", frame.transform);
            var bodyRt = body.GetComponent<RectTransform>();
            bodyRt.anchorMin = Vector2.zero;
            bodyRt.anchorMax = Vector2.one;
            bodyRt.offsetMin = new Vector2(TextPad, TextPad);
            bodyRt.offsetMax = new Vector2(-TextPad, -TextPad);
            var bodyText = body.AddComponent<Text>();
            bodyText.text = "";
            bodyText.fontSize = BodyFontSize;
            bodyText.alignment = TextAnchor.UpperLeft;
            bodyText.color = Color.black;
            bodyText.horizontalOverflow = HorizontalWrapMode.Wrap;
            bodyText.verticalOverflow = VerticalWrapMode.Overflow;
            bodyText.raycastTarget = false;
            bodyText.font = LoadUiFont();
            body.AddComponent<UIPersist>();

            // 说话人：DialogueFrame 左上角半透明蓝底小框
            var speakerBox = CreateUIObject("SpeakerBox", frame.transform);
            var boxRt = speakerBox.GetComponent<RectTransform>();
            boxRt.anchorMin = new Vector2(0f, 1f);
            boxRt.anchorMax = new Vector2(0f, 1f);
            boxRt.pivot = new Vector2(0f, 1f);
            boxRt.anchoredPosition = new Vector2(TextPad, -TextPad);
            boxRt.sizeDelta = new Vector2(220f, 48f);
            var boxImg = speakerBox.AddComponent<Image>();
            boxImg.sprite = LoadWhiteSprite();
            boxImg.type = Image.Type.Simple;
            boxImg.color = new Color(0.15f, 0.35f, 0.85f, 0.55f);
            speakerBox.AddComponent<UIPersist>();

            var speakerLabel = CreateUIObject("e_SpeakerText", speakerBox.transform);
            var speakerRt = speakerLabel.GetComponent<RectTransform>();
            speakerRt.anchorMin = Vector2.zero;
            speakerRt.anchorMax = Vector2.one;
            speakerRt.offsetMin = new Vector2(10f, 4f);
            speakerRt.offsetMax = new Vector2(-10f, -4f);
            var speakerText = speakerLabel.AddComponent<Text>();
            speakerText.text = "";
            speakerText.fontSize = SpeakerFontSize;
            speakerText.alignment = TextAnchor.MiddleCenter;
            speakerText.color = Color.white;
            speakerText.horizontalOverflow = HorizontalWrapMode.Overflow;
            speakerText.verticalOverflow = VerticalWrapMode.Truncate;
            speakerText.raycastTarget = false;
            speakerText.font = LoadUiFont();
            speakerLabel.AddComponent<UIPersist>();
        }

        private static GameObject CreateUIObject(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go;
        }

        private static Sprite LoadWhiteSprite()
        {
            var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(WhitePath);
            if (sprite != null) return sprite;
            return AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");
        }

        private static Font LoadUiFont()
        {
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (font == null)
                font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            return font;
        }
    }
}
