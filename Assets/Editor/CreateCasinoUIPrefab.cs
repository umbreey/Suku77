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
    /// 创建 UICasino：按 JSON 左二右二 Robot + 玩家。
    /// Robot1/2 左侧，Robot4/5 右侧；圆形 tip / ing_light / player_money。
    /// 菜单：Suku/Create UICasino Prefab
    /// </summary>
    public static class CreateCasinoUIPrefab
    {
        private const string PrefabName = "UICasino";
        private const string PrefabDir = "Assets/AssetBundles/UI/Prefab";
        private const string GenDir = PrefabDir + "/Gen";
        private const string SrcPath = PrefabDir + "/" + PrefabName + ".prefab";
        private const string GenPath = GenDir + "/" + PrefabName + ".prefab";
        private const string WhitePath = "Assets/AssetBundles/UI/Texture/White.png";
        private const string CirclePath = "Assets/AssetBundles/UI/Texture/Circle.png";
        private const string BtnWhitePath = "Assets/AssetBundles/UI/Texture/BtnWhite.png";
        private const string CodeDir = "Assets/HotUpdate/UI/Base";
        private const string LogicDir = "Assets/HotUpdate/UI";
        private const string ConfigDir = "Assets/AssetBundles/UI/Config";
        private const string FlagKey = "Suku.UICasino.v5";

        private static readonly Color CardPlaceholder = new Color(0.847f, 0.847f, 0.847f, 1f);
        private static readonly Color DeskColor = new Color(0.686f, 0.686f, 0.729f, 1f);
        private static readonly Color NpcBackColor = new Color(0.622f, 0.941f, 0.490f, 1f);
        private static readonly Color TipPreview = new Color(1f, 1f, 0.008f, 0.85f);
        private static readonly Color IngLightPreview = new Color(1f, 0.15f, 0.15f, 0.55f);

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
                    Debug.LogWarning($"[Suku] UICasino setup: {e.Message}");
                }
            };
        }

        [MenuItem("Suku/Create UICasino Prefab")]
        public static void Create()
        {
            Directory.CreateDirectory(PrefabDir);
            Directory.CreateDirectory(GenDir);
            Directory.CreateDirectory(CodeDir);
            Directory.CreateDirectory(LogicDir);
            EnsureCircleSprite();
            EnsureABPacks();

            var root = BuildHierarchy();
            PrefabUtility.SaveAsPrefabAsset(root, SrcPath);
            Object.DestroyImmediate(root);

            var srcPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(SrcPath);
            var instance = Object.Instantiate(srcPrefab);
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

            EnsureGenStandalone();
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

        private static void EnsureGenStandalone()
        {
            if (!File.Exists(GenPath)) return;
            var contents = PrefabUtility.LoadPrefabContents(GenPath);
            try
            {
                if (PrefabUtility.IsAnyPrefabInstanceRoot(contents))
                {
                    PrefabUtility.UnpackPrefabInstance(contents, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                    PrefabUtility.SaveAsPrefabAsset(contents, GenPath);
                    Debug.Log($"[Suku] {PrefabName} Gen unpacked → standalone");
                }
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        private static void EnsureLogicStub()
        {
            var logic = $"{LogicDir}/UICasino.cs";
            if (File.Exists(logic))
            {
                var existing = File.ReadAllText(logic);
                if (existing.Contains("OnButtonClick") || existing.Contains("SetBlindTip"))
                    return;
            }

            File.WriteAllText(logic,
@"using UnityEngine;
using Umbreey;

/// <summary>
/// 赌场德州桌 UI（JSON：Seat0 玩家 + Seat1=Robot1 / Seat2=Robot2 / Seat3=Robot4 / Seat4=Robot5）。
/// 布局：左二（Robot1 下、Robot2 上）+ 右二（Robot4 上、Robot5 下）。
/// 行为约定：
/// - ing_light：NPC 用小圆点；玩家用底栏扁椭圆 e_Seat0_IngLight（无单独小圆点）
/// - 当前行动座位 Image.color = 红并显示，其余隐藏或恢复默认色
/// - tip：大盲红、小盲黄并显示；非盲注隐藏
/// </summary>
[UIHandler(""UICasino"")]
public class UICasino : BaseUICasino
{
    public static readonly Color IngLightRed = new Color(1f, 0.15f, 0.15f, 1f);
    public static readonly Color TipBigBlindRed = new Color(1f, 0f, 0f, 1f);
    public static readonly Color TipSmallBlindYellow = new Color(1f, 1f, 0.008f, 1f);

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

        private static void EnsureCircleSprite()
        {
            if (File.Exists(CirclePath))
            {
                var existing = AssetDatabase.LoadAssetAtPath<Sprite>(CirclePath);
                if (existing != null) return;
            }

            const int size = 128;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var center = (size - 1) * 0.5f;
            var r = center - 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x - center;
                    float dy = y - center;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    float a = Mathf.Clamp01(r - d + 0.5f);
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                }
            }
            tex.Apply();
            var png = tex.EncodeToPNG();
            Object.DestroyImmediate(tex);
            Directory.CreateDirectory(Path.GetDirectoryName(CirclePath));
            File.WriteAllBytes(CirclePath, png);
            AssetDatabase.ImportAsset(CirclePath, ImportAssetOptions.ForceUpdate);
            var importer = AssetImporter.GetAtPath(CirclePath) as TextureImporter;
            if (importer != null)
            {
                importer.textureType = TextureImporterType.Sprite;
                importer.spriteImportMode = SpriteImportMode.Single;
                importer.alphaIsTransparency = true;
                importer.mipmapEnabled = false;
                importer.npotScale = TextureImporterNPOTScale.None;
                importer.SaveAndReimport();
            }
        }

        private static GameObject BuildHierarchy()
        {
            var root = new GameObject(PrefabName, typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = root.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 8;
            var scaler = root.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;
            StretchFull(root.GetComponent<RectTransform>());

            var bg = CreateUIObject("e_Bg", root.transform);
            var bgImg = bg.AddComponent<Image>();
            bgImg.sprite = LoadWhiteSprite();
            bgImg.color = Color.white;
            bgImg.raycastTarget = true;
            StretchFull(bg.GetComponent<RectTransform>());
            bg.AddComponent<UIPersist>();

            var desk = CreateUIObject("Desk", root.transform);
            var deskImg = desk.AddComponent<Image>();
            deskImg.sprite = LoadWhiteSprite();
            deskImg.color = DeskColor;
            deskImg.raycastTarget = false;
            StretchFull(desk.GetComponent<RectTransform>());

            BuildBoard(root.transform);
            BuildPot(root.transform);
            BuildPlayerMoneyDecor(root.transform);

            // Seat0 玩家
            BuildPlayerSeat(root.transform, 0);

            // JSON 左二右二（无对面座）
            // Seat1 = Robot1 左下 (0,648) tip 右
            BuildNpcSeat(root.transform, 1, new Vector2(0f, -648f), tipOnRight: true, stackOnLeft: true, "Robot1");
            // Seat2 = Robot2 左上 (0,254) tip 右
            BuildNpcSeat(root.transform, 2, new Vector2(0f, -254f), tipOnRight: true, stackOnLeft: true, "Robot2");
            // Seat3 = Robot4 右上 (1770,254) tip 左
            BuildNpcSeat(root.transform, 3, new Vector2(1770f, -254f), tipOnRight: false, stackOnLeft: false, "Robot4");
            // Seat4 = Robot5 右下 (1770,648) tip 左
            BuildNpcSeat(root.transform, 4, new Vector2(1770f, -648f), tipOnRight: false, stackOnLeft: false, "Robot5");

            BuildActionBar(root.transform);
            return root;
        }

        private static void BuildBoard(Transform parent)
        {
            var board = CreateUIObject("Board", parent);
            var boardRt = board.GetComponent<RectTransform>();
            SetTopLeft(boardRt, new Vector2(587f, -371f), new Vector2(747f, 149f));

            float[] xs = { 0f, 160f, 320f, 480f, 640f };
            for (int i = 0; i < 5; i++)
            {
                var card = CreateSquareImage($"e_BoardCard{i}", board.transform, CardPlaceholder, new Vector2(107f, 149f));
                SetTopLeft(card.GetComponent<RectTransform>(), new Vector2(xs[i], 0f), new Vector2(107f, 149f));
                card.AddComponent<UIPersist>();
            }
        }

        private static void BuildPot(Transform parent)
        {
            var pot = CreateUIObject("e_TxtPot", parent);
            var rt = pot.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(0f, -320f);
            rt.sizeDelta = new Vector2(360f, 40f);
            var text = pot.AddComponent<Text>();
            SetupText(text, "底池 0", 32, TextAnchor.MiddleCenter, new Color(0.24f, 0.24f, 0.24f, 1f));
            pot.AddComponent<UIPersist>();
        }

        private static void BuildPlayerMoneyDecor(Transform parent)
        {
            // JSON 圆形 2/3/4/5/7 — 必须用 Circle 贴图
            var money = CreateUIObject("PlayerMoneyDecor", parent);
            SetTopLeft(money.GetComponent<RectTransform>(), new Vector2(887f, -52f), new Vector2(173f, 173f));

            CreateRing(money.transform, "Ring0", 0f, 173f, new Color(0.03f, 1f, 0f, 0.55f));
            CreateRing(money.transform, "Ring1", 13f, 147f, new Color(1f, 1f, 0f, 0.55f));
            CreateRing(money.transform, "Ring2", 26f, 122f, new Color(0f, 0.53f, 1f, 0.55f));
            CreateRing(money.transform, "Ring3", 38f, 99f, new Color(1f, 0.47f, 0f, 0.55f));
            CreateRing(money.transform, "Ring4", 51f, 73f, new Color(0.52f, 0.004f, 1f, 0.55f));
        }

        private static void CreateRing(Transform parent, string name, float inset, float size, Color color)
        {
            var go = CreateCircleImage(name, parent, color, new Vector2(size, size));
            SetTopLeft(go.GetComponent<RectTransform>(), new Vector2(inset, -inset), new Vector2(size, size));
        }

        private static void BuildNpcSeat(Transform parent, int seat, Vector2 topLeftPos, bool tipOnRight, bool stackOnLeft, string defaultName)
        {
            var seatGo = CreateUIObject($"Seat{seat}", parent);
            SetTopLeft(seatGo.GetComponent<RectTransform>(), topLeftPos, new Vector2(150f, 126f));

            var back = CreateSquareImage("Back", seatGo.transform, NpcBackColor, new Vector2(150f, 126f));
            SetTopLeft(back.GetComponent<RectTransform>(), Vector2.zero, new Vector2(150f, 126f));

            // ing_light 圆形，预制体中可见（运行时再按行动显隐）
            var ing = CreateCircleImage($"e_Seat{seat}_IngLight", seatGo.transform, IngLightPreview, new Vector2(21f, 21f));
            SetTopLeft(ing.GetComponent<RectTransform>(), new Vector2(65f, -6f), new Vector2(21f, 21f));
            ing.AddComponent<UIPersist>();

            var card0 = CreateSquareImage($"e_Seat{seat}_Card0", seatGo.transform, CardPlaceholder, new Vector2(59f, 82f));
            SetTopLeft(card0.GetComponent<RectTransform>(), new Vector2(10f, -34f), new Vector2(59f, 82f));
            card0.AddComponent<UIPersist>();

            var card1 = CreateSquareImage($"e_Seat{seat}_Card1", seatGo.transform, CardPlaceholder, new Vector2(59f, 82f));
            SetTopLeft(card1.GetComponent<RectTransform>(), new Vector2(81f, -34f), new Vector2(59f, 82f));
            card1.AddComponent<UIPersist>();

            // tip 圆形，预制体中可见
            float tipX = tipOnRight ? 174f : -54f;
            var tip = CreateCircleImage($"e_Seat{seat}_Tip", seatGo.transform, TipPreview, new Vector2(39f, 39f));
            SetTopLeft(tip.GetComponent<RectTransform>(), new Vector2(tipX, 0f), new Vector2(39f, 39f));
            tip.AddComponent<UIPersist>();

            var nameGo = CreateUIObject($"e_Seat{seat}_TxtName", seatGo.transform);
            SetTopLeft(nameGo.GetComponent<RectTransform>(), new Vector2(0f, 61f), new Vector2(150f, 23f));
            var nameText = nameGo.AddComponent<Text>();
            SetupText(nameText, defaultName, 16, TextAnchor.MiddleCenter, new Color(0.24f, 0.24f, 0.24f, 1f));
            nameGo.AddComponent<UIPersist>();

            float lineX = stackOnLeft ? 0f : -180f;
            var line = CreateSquareImage($"MoneyLine{seat}", seatGo.transform, new Color(0.847f, 0.847f, 0.847f, 0.85f), new Vector2(330f, 19f));
            SetTopLeft(line.GetComponent<RectTransform>(), new Vector2(lineX, 29f), new Vector2(330f, 19f));

            var stack = CreateUIObject($"e_Seat{seat}_TxtStack", seatGo.transform);
            SetTopLeft(stack.GetComponent<RectTransform>(), new Vector2(lineX, 29f), new Vector2(150f, 19f));
            var stackText = stack.AddComponent<Text>();
            SetupText(stackText, "0", 16, TextAnchor.MiddleLeft, new Color(0.15f, 0.15f, 0.15f, 1f));
            stack.AddComponent<UIPersist>();
        }

        private static void BuildPlayerSeat(Transform parent, int seat)
        {
            var seatGo = CreateUIObject($"Seat{seat}", parent);
            StretchFull(seatGo.GetComponent<RectTransform>());

            // JSON player 底栏：圆贴图横向拉伸；同时充当玩家行动指示 e_Seat0_IngLight（不再另做小圆点）
            var ing = CreateCircleImage($"e_Seat{seat}_IngLight", seatGo.transform, new Color(1f, 1f, 1f, 0.35f), new Vector2(1920f, 77f));
            var ingImg = ing.GetComponent<Image>();
            ingImg.preserveAspect = false;
            SetTopLeft(ing.GetComponent<RectTransform>(), new Vector2(0f, -1042f), new Vector2(1920f, 77f));
            ing.AddComponent<UIPersist>();

            var card0 = CreateSquareImage($"e_Seat{seat}_Card0", seatGo.transform, CardPlaceholder, new Vector2(150f, 208f));
            SetTopLeft(card0.GetComponent<RectTransform>(), new Vector2(785f, -812f), new Vector2(150f, 208f));
            card0.AddComponent<UIPersist>();

            var card1 = CreateSquareImage($"e_Seat{seat}_Card1", seatGo.transform, CardPlaceholder, new Vector2(150f, 208f));
            SetTopLeft(card1.GetComponent<RectTransform>(), new Vector2(985f, -812f), new Vector2(150f, 208f));
            card1.AddComponent<UIPersist>();

            var tip = CreateCircleImage($"e_Seat{seat}_Tip", seatGo.transform, TipPreview, new Vector2(39f, 39f));
            SetTopLeft(tip.GetComponent<RectTransform>(), new Vector2(705f, -61f), new Vector2(39f, 39f));
            tip.AddComponent<UIPersist>();

            var nameGo = CreateUIObject($"e_Seat{seat}_TxtName", seatGo.transform);
            SetTopLeft(nameGo.GetComponent<RectTransform>(), new Vector2(1062f, -52f), new Vector2(272f, 52f));
            var nameText = nameGo.AddComponent<Text>();
            SetupText(nameText, "Player", 28, TextAnchor.MiddleLeft, new Color(0.24f, 0.24f, 0.24f, 1f));
            nameGo.AddComponent<UIPersist>();

            var stack = CreateUIObject($"e_Seat{seat}_TxtStack", seatGo.transform);
            SetTopLeft(stack.GetComponent<RectTransform>(), new Vector2(1062f, -100f), new Vector2(272f, 36f));
            var stackText = stack.AddComponent<Text>();
            SetupText(stackText, "0", 24, TextAnchor.MiddleLeft, new Color(0.15f, 0.15f, 0.15f, 1f));
            stack.AddComponent<UIPersist>();
        }

        private static void BuildActionBar(Transform parent)
        {
            var bar = CreateUIObject("ActionBar", parent);
            SetTopLeft(bar.GetComponent<RectTransform>(), new Vector2(600f, -640f), new Vector2(720f, 64f));

            CreateActionButton(bar.transform, "e_BtnFold", "弃牌", 0, new Color(0.55f, 0.25f, 0.22f, 0.95f));
            CreateActionButton(bar.transform, "e_BtnCheckCall", "跟注", 1, new Color(0.28f, 0.42f, 0.55f, 0.95f));
            CreateActionButton(bar.transform, "e_BtnRaise", "加注", 2, new Color(0.35f, 0.5f, 0.28f, 0.95f));
            CreateActionButton(bar.transform, "e_BtnAllIn", "全下", 3, new Color(0.55f, 0.4f, 0.18f, 0.95f));
        }

        private static void CreateActionButton(Transform parent, string name, string label, int index, Color color)
        {
            var go = CreateUIObject(name, parent);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 0.5f);
            rt.pivot = new Vector2(0f, 0.5f);
            rt.anchoredPosition = new Vector2(12f + index * 178f, 0f);
            rt.sizeDelta = new Vector2(164f, 56f);

            var img = go.AddComponent<Image>();
            img.sprite = LoadBtnSprite();
            img.type = Image.Type.Sliced;
            img.color = color;
            var btn = go.AddComponent<Button>();
            btn.targetGraphic = img;

            var textGo = CreateUIObject("Label", go.transform);
            StretchFull(textGo.GetComponent<RectTransform>(), 6f, 4f);
            var text = textGo.AddComponent<Text>();
            SetupText(text, label, 28, TextAnchor.MiddleCenter, Color.white);
            text.raycastTarget = false;
            go.AddComponent<UIPersist>();
        }

        private static GameObject CreateSquareImage(string name, Transform parent, Color color, Vector2 size)
        {
            var go = CreateUIObject(name, parent);
            var img = go.AddComponent<Image>();
            img.sprite = LoadWhiteSprite();
            img.color = color;
            img.raycastTarget = false;
            go.GetComponent<RectTransform>().sizeDelta = size;
            return go;
        }

        private static GameObject CreateCircleImage(string name, Transform parent, Color color, Vector2 size)
        {
            var go = CreateUIObject(name, parent);
            var img = go.AddComponent<Image>();
            img.sprite = LoadCircleSprite();
            img.color = color;
            img.raycastTarget = false;
            img.preserveAspect = true;
            go.GetComponent<RectTransform>().sizeDelta = size;
            return go;
        }

        private static void SetTopLeft(RectTransform rt, Vector2 anchoredPos, Vector2 size)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = anchoredPos;
            rt.sizeDelta = size;
        }

        private static void SetupText(Text t, string content, int size, TextAnchor align, Color color)
        {
            t.text = content;
            t.fontSize = size;
            t.alignment = align;
            t.color = color;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
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

        private static Sprite LoadCircleSprite()
        {
            var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(CirclePath);
            return sprite != null ? sprite : LoadWhiteSprite();
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
