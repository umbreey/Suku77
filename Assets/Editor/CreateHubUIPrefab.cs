using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using Umbreey;
using Umbreey.Editor;

namespace Suku.Editor
{
    /// <summary>
    /// 创建 UIHub：兰斯据点式「家」一屏热点图（源 Prefab + UIGen）。
    /// 菜单：Suku/Create UIHub Prefab
    /// </summary>
    public static class CreateHubUIPrefab
    {
        private const string PrefabName = "UIHub";
        private const string PrefabDir = "Assets/AssetBundles/UI/Prefab";
        private const string GenDir = PrefabDir + "/Gen";
        private const string SrcPath = PrefabDir + "/" + PrefabName + ".prefab";
        private const string GenPath = GenDir + "/" + PrefabName + ".prefab";
        private const string WhitePath = "Assets/AssetBundles/UI/Texture/White.png";
        private const string BtnWhitePath = "Assets/AssetBundles/UI/Texture/BtnWhite.png";
        private const string CodeDir = "Assets/HotUpdate/UI/Base";
        private const string LogicDir = "Assets/HotUpdate/UI";
        private const string ConfigDir = "Assets/AssetBundles/UI/Config";
        private const string FlagKey = "Suku.UIHub.v1";

        [InitializeOnLoadMethod]
        private static void AutoEnsure()
        {
            EditorApplication.delayCall += () =>
            {
                try
                {
                    if (!File.Exists(SrcPath) || !File.Exists(GenPath) || !EditorPrefs.GetBool(FlagKey, false))
                        Create();
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"[Suku] UIHub setup: {e.Message}");
                }
            };
        }

        [MenuItem("Suku/Create UIHub Prefab")]
        public static void Create()
        {
            Directory.CreateDirectory(PrefabDir);
            Directory.CreateDirectory(GenDir);
            EnsureABPacks();

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

            EnsureLogicStub();
            var cfg = new UIGen.Config
            {
                BasePath = LogicDir,
                SrcPrefabPath = PrefabDir,
                DstPrefabPath = GenDir
            };
            UIDefGen.Gen(cfg);
            UICanvasToggleGen.Gen(cfg);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            EditorPrefs.SetBool(FlagKey, true);
            Debug.Log($"[Suku] {PrefabName} ready → {GenPath}");
        }

        private static void EnsureLogicStub()
        {
            var logic = $"{LogicDir}/UIHub.cs";
            if (File.Exists(logic)) return;
            File.WriteAllText(logic,
@"using Umbreey;

[UIHandler(""UIHub"")]
public class UIHub : BaseUIHub
{
    protected override void OnOpened()
    {
        base.OnOpened();
    }
}
");
        }

        private static void EnsureABPacks()
        {
            var rule = LoadOrCreateAsset<ABRuleEachFiles>($"{ConfigDir}/ABRule_EachFiles.asset");
            var prefabPack = LoadOrCreateAsset<ABPack>($"{ConfigDir}/ABPack_UI_Prefab.asset");
            prefabPack.PackName = "ui_prefab";
            prefabPack.Extension = "*.prefab";
            prefabPack.Rule = rule;
            prefabPack.Builtin = true;
            prefabPack.DirAssets = new List<Object>
            {
                AssetDatabase.LoadAssetAtPath<Object>(GenDir)
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
            canvas.sortingOrder = 5;
            var scaler = root.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;
            StretchFull(root.GetComponent<RectTransform>());

            // 1. 全屏据点背景（暖色室内占位，后续可换 CG）
            var bg = CreateUIObject("e_Bg", root.transform);
            var bgImg = bg.AddComponent<Image>();
            bgImg.sprite = LoadWhiteSprite();
            bgImg.color = new Color(0.42f, 0.34f, 0.28f, 1f);
            bgImg.raycastTarget = true;
            StretchFull(bg.GetComponent<RectTransform>());

            // 地面色带（兰斯据点构图里常见的前景层）
            var floor = CreateUIObject("FloorBand", root.transform);
            var floorImg = floor.AddComponent<Image>();
            floorImg.sprite = LoadWhiteSprite();
            floorImg.color = new Color(0.28f, 0.22f, 0.18f, 1f);
            floorImg.raycastTarget = false;
            var floorRt = floor.GetComponent<RectTransform>();
            floorRt.anchorMin = new Vector2(0f, 0f);
            floorRt.anchorMax = new Vector2(1f, 0.28f);
            floorRt.offsetMin = Vector2.zero;
            floorRt.offsetMax = Vector2.zero;

            // 左侧地点铭牌
            var plate = CreateUIObject("LocationPlate", root.transform);
            var plateImg = plate.AddComponent<Image>();
            plateImg.sprite = LoadWhiteSprite();
            plateImg.color = new Color(0.12f, 0.1f, 0.08f, 0.75f);
            var plateRt = plate.GetComponent<RectTransform>();
            plateRt.anchorMin = new Vector2(0f, 1f);
            plateRt.anchorMax = new Vector2(0f, 1f);
            plateRt.pivot = new Vector2(0f, 1f);
            plateRt.anchoredPosition = new Vector2(36f, -96f);
            plateRt.sizeDelta = new Vector2(220f, 56f);
            var plateLabel = CreateUIObject("PlateLabel", plate.transform);
            StretchFull(plateLabel.GetComponent<RectTransform>(), 12f, 8f);
            var plateText = plateLabel.AddComponent<Text>();
            SetupText(plateText, "自宅", 28, TextAnchor.MiddleLeft, Color.white);

            // 顶栏状态（仅家中可见行动力）
            BuildStatusBar(root.transform);

            // 底栏提示先建，保证 e_TxtHint 的 UIBind 顺序在按钮之前
            BuildHintBar(root.transform);

            // 热点设施（点选，非跑图）
            BuildHotspots(root.transform);

            // 结束阶段（兰斯据点右下「次へ」位）
            BuildEndPhaseButton(root.transform);

            // 出门地点层（默认隐藏）
            BuildOutPanel(root.transform);

            return root;
        }

        private static void BuildStatusBar(Transform parent)
        {
            var bar = CreateUIObject("StatusBar", parent);
            var barImg = bar.AddComponent<Image>();
            barImg.sprite = LoadWhiteSprite();
            barImg.color = new Color(0.08f, 0.07f, 0.06f, 0.82f);
            var barRt = bar.GetComponent<RectTransform>();
            barRt.anchorMin = new Vector2(0f, 1f);
            barRt.anchorMax = new Vector2(1f, 1f);
            barRt.pivot = new Vector2(0.5f, 1f);
            barRt.anchoredPosition = Vector2.zero;
            barRt.sizeDelta = new Vector2(0f, 72f);

            // 绑定顺序：Day → Phase → Gold → Ap（与 BaseUIHub 一致）
            CreateStatusText(bar.transform, "e_TxtDay", "第 1 天", 0.02f, 0.18f);
            CreateStatusText(bar.transform, "e_TxtPhase", "白天", 0.20f, 0.36f);
            CreateStatusText(bar.transform, "e_TxtGold", "金钱 0", 0.38f, 0.62f);
            CreateStatusText(bar.transform, "e_TxtAp", "行动力 5/5", 0.64f, 0.98f);
        }

        private static void CreateStatusText(Transform parent, string name, string text, float aMin, float aMax)
        {
            var go = CreateUIObject(name, parent);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(aMin, 0f);
            rt.anchorMax = new Vector2(aMax, 1f);
            rt.offsetMin = new Vector2(8f, 8f);
            rt.offsetMax = new Vector2(-8f, -8f);
            var t = go.AddComponent<Text>();
            SetupText(t, text, 26, TextAnchor.MiddleLeft, new Color(0.95f, 0.92f, 0.85f, 1f));
            go.AddComponent<UIPersist>();
        }

        private static void BuildHotspots(Transform parent)
        {
            var layer = CreateUIObject("HotspotLayer", parent);
            StretchFull(layer.GetComponent<RectTransform>());

            // 位置模仿兰斯据点：人物/设施散布在画面中下部
            CreateHotspot(layer.transform, "e_BtnSleep", "睡觉", new Vector2(0.18f, 0.42f), new Color(0.55f, 0.35f, 0.45f, 0.88f));
            CreateHotspot(layer.transform, "e_BtnWarehouse", "仓库", new Vector2(0.38f, 0.52f), new Color(0.35f, 0.45f, 0.55f, 0.88f));
            CreateHotspot(layer.transform, "e_BtnRepair", "修理", new Vector2(0.58f, 0.48f), new Color(0.45f, 0.4f, 0.3f, 0.88f));
            CreateHotspot(layer.transform, "e_BtnCraft", "整理", new Vector2(0.72f, 0.38f), new Color(0.4f, 0.5f, 0.35f, 0.88f));
            CreateHotspot(layer.transform, "e_BtnStatus", "状态", new Vector2(0.28f, 0.28f), new Color(0.5f, 0.4f, 0.55f, 0.88f));
            CreateHotspot(layer.transform, "e_BtnGoOut", "出门", new Vector2(0.82f, 0.58f), new Color(0.55f, 0.45f, 0.25f, 0.92f));
        }

        private static void CreateHotspot(Transform parent, string name, string label, Vector2 anchor, Color color)
        {
            var go = CreateUIObject(name, parent);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = anchor;
            rt.anchorMax = anchor;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(168f, 72f);

            var img = go.AddComponent<Image>();
            img.sprite = LoadBtnSprite();
            img.type = Image.Type.Sliced;
            img.color = color;

            var btn = go.AddComponent<Button>();
            btn.targetGraphic = img;
            var colors = btn.colors;
            colors.highlightedColor = new Color(1.1f, 1.1f, 1.1f, 1f);
            colors.pressedColor = new Color(0.85f, 0.85f, 0.85f, 1f);
            btn.colors = colors;

            var textGo = CreateUIObject("Label", go.transform);
            StretchFull(textGo.GetComponent<RectTransform>(), 6f, 4f);
            var text = textGo.AddComponent<Text>();
            SetupText(text, label, 28, TextAnchor.MiddleCenter, Color.white);
            text.raycastTarget = false;

            go.AddComponent<UIPersist>();
        }

        private static void BuildHintBar(Transform parent)
        {
            var hintBar = CreateUIObject("HintBar", parent);
            var hintBarImg = hintBar.AddComponent<Image>();
            hintBarImg.sprite = LoadWhiteSprite();
            hintBarImg.color = new Color(0.05f, 0.05f, 0.05f, 0.55f);
            hintBarImg.raycastTarget = false;
            var hintBarRt = hintBar.GetComponent<RectTransform>();
            hintBarRt.anchorMin = new Vector2(0.03f, 0.015f);
            hintBarRt.anchorMax = new Vector2(0.74f, 0.11f);
            hintBarRt.offsetMin = Vector2.zero;
            hintBarRt.offsetMax = Vector2.zero;

            var hint = CreateUIObject("e_TxtHint", hintBar.transform);
            StretchFull(hint.GetComponent<RectTransform>(), 16f, 8f);
            var hintText = hint.AddComponent<Text>();
            SetupText(hintText, "", 22, TextAnchor.MiddleLeft, new Color(0.92f, 0.9f, 0.82f, 1f));
            hint.AddComponent<UIPersist>();
        }

        private static void BuildEndPhaseButton(Transform parent)
        {
            var end = CreateUIObject("e_BtnEndPhase", parent);
            var endRt = end.GetComponent<RectTransform>();
            endRt.anchorMin = new Vector2(1f, 0f);
            endRt.anchorMax = new Vector2(1f, 0f);
            endRt.pivot = new Vector2(1f, 0f);
            endRt.anchoredPosition = new Vector2(-36f, 28f);
            endRt.sizeDelta = new Vector2(220f, 64f);
            var endImg = end.AddComponent<Image>();
            endImg.sprite = LoadBtnSprite();
            endImg.type = Image.Type.Sliced;
            endImg.color = new Color(0.65f, 0.22f, 0.18f, 0.95f);
            var endBtn = end.AddComponent<Button>();
            endBtn.targetGraphic = endImg;
            var endLabel = CreateUIObject("Label", end.transform);
            StretchFull(endLabel.GetComponent<RectTransform>(), 8f, 4f);
            var endText = endLabel.AddComponent<Text>();
            SetupText(endText, "结束阶段", 28, TextAnchor.MiddleCenter, Color.white);
            endText.raycastTarget = false;
            end.AddComponent<UIPersist>();
        }

        private static void BuildOutPanel(Transform parent)
        {
            var panel = CreateUIObject("OutPanel", parent);
            StretchFull(panel.GetComponent<RectTransform>());
            var dim = panel.AddComponent<Image>();
            dim.sprite = LoadWhiteSprite();
            dim.color = new Color(0f, 0f, 0f, 0.55f);
            panel.SetActive(false);

            var card = CreateUIObject("OutCard", panel.transform);
            var cardImg = card.AddComponent<Image>();
            cardImg.sprite = LoadWhiteSprite();
            cardImg.color = new Color(0.16f, 0.14f, 0.12f, 0.95f);
            var cardRt = card.GetComponent<RectTransform>();
            cardRt.anchorMin = new Vector2(0.5f, 0.5f);
            cardRt.anchorMax = new Vector2(0.5f, 0.5f);
            cardRt.sizeDelta = new Vector2(520f, 360f);

            var title = CreateUIObject("OutTitle", card.transform);
            var titleRt = title.GetComponent<RectTransform>();
            titleRt.anchorMin = new Vector2(0f, 1f);
            titleRt.anchorMax = new Vector2(1f, 1f);
            titleRt.pivot = new Vector2(0.5f, 1f);
            titleRt.anchoredPosition = new Vector2(0f, -16f);
            titleRt.sizeDelta = new Vector2(-32f, 48f);
            var titleText = title.AddComponent<Text>();
            SetupText(titleText, "出门 · 选择地点", 30, TextAnchor.MiddleCenter, new Color(1f, 0.92f, 0.75f));

            for (int i = 0; i < 3; i++)
            {
                var btn = CreateUIObject($"BtnLoc{i}", card.transform);
                var rt = btn.GetComponent<RectTransform>();
                rt.anchorMin = new Vector2(0.5f, 1f);
                rt.anchorMax = new Vector2(0.5f, 1f);
                rt.pivot = new Vector2(0.5f, 1f);
                rt.anchoredPosition = new Vector2(0f, -80f - i * 72f);
                rt.sizeDelta = new Vector2(360f, 56f);
                var img = btn.AddComponent<Image>();
                img.sprite = LoadBtnSprite();
                img.type = Image.Type.Sliced;
                img.color = new Color(0.35f, 0.32f, 0.28f, 1f);
                btn.AddComponent<Button>().targetGraphic = img;
                var label = CreateUIObject("Label", btn.transform);
                StretchFull(label.GetComponent<RectTransform>(), 8f, 4f);
                var text = label.AddComponent<Text>();
                SetupText(text, $"地点{i + 1}", 26, TextAnchor.MiddleCenter, Color.white);
                text.raycastTarget = false;
            }

            var close = CreateUIObject("BtnCloseOut", card.transform);
            var closeRt = close.GetComponent<RectTransform>();
            closeRt.anchorMin = new Vector2(0.5f, 0f);
            closeRt.anchorMax = new Vector2(0.5f, 0f);
            closeRt.pivot = new Vector2(0.5f, 0f);
            closeRt.anchoredPosition = new Vector2(0f, 24f);
            closeRt.sizeDelta = new Vector2(200f, 48f);
            var closeImg = close.AddComponent<Image>();
            closeImg.sprite = LoadBtnSprite();
            closeImg.type = Image.Type.Sliced;
            closeImg.color = new Color(0.4f, 0.25f, 0.22f, 1f);
            var closeBtn = close.AddComponent<Button>();
            closeBtn.targetGraphic = closeImg;
            var closeLabel = CreateUIObject("Label", close.transform);
            StretchFull(closeLabel.GetComponent<RectTransform>(), 6f, 4f);
            var closeText = closeLabel.AddComponent<Text>();
            SetupText(closeText, "返回家里", 24, TextAnchor.MiddleCenter, Color.white);
        }

        private static void SetupText(Text t, string content, int size, TextAnchor align, Color color)
        {
            t.text = content;
            t.fontSize = size;
            t.alignment = align;
            t.color = color;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Truncate;
            t.raycastTarget = false;
            t.font = LoadUiFont();
        }

        private static GameObject CreateUIObject(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go;
        }

        private static void StretchFull(RectTransform rt, float padX = 0f, float padY = 0f)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(padX, padY);
            rt.offsetMax = new Vector2(-padX, -padY);
        }

        private static Sprite LoadWhiteSprite()
        {
            var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(WhitePath);
            if (sprite != null) return sprite;
            return AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");
        }

        private static Sprite LoadBtnSprite()
        {
            var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(BtnWhitePath);
            return sprite != null ? sprite : LoadWhiteSprite();
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
