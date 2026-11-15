using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Umbreey.Editor
{
    /// <summary>
    /// 荳髞ｮ蛻帛ｻｺ遉ｺ萓・UI Prefab・悟ｹｶ遶句綾霍・UIGen 逕滓・ Base 莉｣遐√・
    /// </summary>
    public static class CreateDemoUIPrefab
    {
        private const string PrefabName = "UIDemo";
        private const string SrcDir = "Assets/AssetBundles/UI/Prefab";
        private const string DstDir = "Assets/AssetBundles/UI/Prefab/Gen";
        private const string CodeDir = "Assets/HotUpdate/UI/Base";
        private const string LogicDir = "Assets/HotUpdate/UI";

        [MenuItem("Umbreey/Create Demo UI (UIDemo)")]
        public static void Create()
        {
            EnsureDirs();

            string srcPath = $"{SrcDir}/{PrefabName}.prefab";
            string logicPath = $"{LogicDir}/{PrefabName}.cs";

            var root = BuildHierarchy();
            PrefabUtility.SaveAsPrefabAsset(root, srcPath);
            Object.DestroyImmediate(root);

            // 逕ｨ貅・Prefab 蜀榊ｮ樔ｾ句喧荳莉ｽ霍・Gen・井ｼ・Replace 扈・ｻｶ蟷ｶ蜀吝・ Base 莉｣遐・+ Gen Prefab・・
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

            if (!File.Exists(logicPath))
            {
                File.WriteAllText(logicPath, BuildLogicScript());
            }

            // 蛻ｷ譁ｰ UIDef / canvas-toggle 逋ｽ蜷榊黒
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

            EnsureEventSystem();
            AssetDatabase.Refresh();

            EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<GameObject>(srcPath));
            Debug.Log(
                $"[Umbreey] Demo UI 创建完成:\n" +
                $"  Prefab(源): {srcPath}\n" +
                $"  Prefab(Gen): {DstDir}/{PrefabName}.prefab\n" +
                $"  Base 代码: {CodeDir}/Base{PrefabName}.cs\n" +
                $"  业务代码: {logicPath}\n" +
                $"请手动打开并检查 Prefab 后走 Umbreey/UIGen。");
        }

        private static void EnsureDirs()
        {
            Directory.CreateDirectory(SrcDir);
            Directory.CreateDirectory(DstDir);
            Directory.CreateDirectory(CodeDir);
            Directory.CreateDirectory(LogicDir);
        }

        private static GameObject BuildHierarchy()
        {
            // 譬ｹ・壼・螻・Canvas
            var root = new GameObject(PrefabName, typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = root.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;

            var scaler = root.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080, 1920);
            scaler.matchWidthOrHeight = 0.5f;

            var rootRt = root.GetComponent<RectTransform>();
            rootRt.anchorMin = Vector2.zero;
            rootRt.anchorMax = Vector2.one;
            rootRt.offsetMin = Vector2.zero;
            rootRt.offsetMax = Vector2.zero;

            // e_Bg・夐銅貊｡蜈ｨ螻剰レ譚ｿ・亥錐蟄怜ｸｦ e_ 謇堺ｼ夊｢ｫ UIGen 蟇ｼ蜃ｺ・・
            var bg = CreateUIObject("e_Bg", root.transform);
            var bgImage = bg.AddComponent<Image>();
            bgImage.sprite = LoadUiSprite();
            bgImage.type = Image.Type.Sliced;
            bgImage.color = new Color(0.12f, 0.14f, 0.18f, 1f);
            StretchFull(bg.GetComponent<RectTransform>());

            // e_Btn・壼ｱ・ｸｭ謖蛾聴
            var btnGo = CreateUIObject("e_Btn", root.transform);
            var btnRt = btnGo.GetComponent<RectTransform>();
            btnRt.anchorMin = btnRt.anchorMax = new Vector2(0.5f, 0.5f);
            btnRt.pivot = new Vector2(0.5f, 0.5f);
            btnRt.sizeDelta = new Vector2(280, 80);
            btnRt.anchoredPosition = Vector2.zero;

            var btnImage = btnGo.AddComponent<Image>();
            btnImage.sprite = LoadUiSprite();
            btnImage.type = Image.Type.Sliced;
            btnImage.color = new Color(0.25f, 0.55f, 0.95f, 1f);
            var button = btnGo.AddComponent<Button>();
            button.targetGraphic = btnImage;

            var label = CreateUIObject("Label", btnGo.transform);
            StretchFull(label.GetComponent<RectTransform>(), 8f);
            var text = label.AddComponent<Text>();
            text.text = "Click Me";
            text.alignment = TextAnchor.MiddleCenter;
            text.color = Color.white;
            text.fontSize = 36;
            text.raycastTarget = false;
            // 蜀・ｽｮ蟄嶺ｽ難ｼ磯∩蜈堺ｾ晁ｵ・TMP 襍・ｺ撰ｼ・
            text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");

            return root;
        }

        private static Sprite LoadUiSprite()
        {
            var sprite = AssetDatabase.LoadAssetAtPath<Sprite>("Assets/Plugins/Umbreey/Resources/EmptySprite.png");
            if (sprite != null) return sprite;
            return AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");
        }

        private static GameObject CreateUIObject(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go;
        }

        private static void StretchFull(RectTransform rt, float padding = 0f)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(padding, padding);
            rt.offsetMax = new Vector2(-padding, -padding);
            rt.pivot = new Vector2(0.5f, 0.5f);
        }

        private static string BuildLogicScript()
        {
            return
@"using UnityEngine;
using Umbreey;

/// <summary>
/// UIDemo 荳壼苅騾ｻ霎托ｼ夂ｻｧ謇ｿ UIGen 逕滓・逧・BaseUIDemo縲・
/// Prefab 蜷・/ 邀ｻ蜷・/ [UIHandler] 荳芽・ｿ晄戟荳閾ｴ・御ｾｿ莠・UIManager.Open(""UIDemo"")縲・
/// </summary>
[UIHandler(""UIDemo"")]
public class UIDemo : BaseUIDemo
{
    protected override void OnOpened()
    {
        base.OnOpened();
        Log.I(""UIDemo opened"");
    }

    protected override void OnButtonClick(Component comp)
    {
        base.OnButtonClick(comp);
        if (comp == m_Btn)
        {
            Log.I(""UIDemo button clicked"");
        }
    }
}
";
        }

        private static void EnsureEventSystem()
        {
            if (Object.FindObjectOfType<EventSystem>() != null) return;
            var es = new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
            Undo.RegisterCreatedObjectUndo(es, "Create EventSystem");
        }
    }
}
