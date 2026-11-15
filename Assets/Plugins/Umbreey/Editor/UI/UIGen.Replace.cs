using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Coffee.UIExtensions;
using SuperScrollView;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace Umbreey.Editor
{
    public partial class UIGen
    {
        private static Sprite s_Empty;
                
        private static Dictionary<Toggle, (GameObject, GameObject)> s_CacheToggle =
            new Dictionary<Toggle, (GameObject, GameObject)>();
        private static Dictionary<InputField, (GameObject, GameObject)> s_CacheInputField =
            new Dictionary<InputField, (GameObject, GameObject)>();
        private static Dictionary<TMP_InputField, (GameObject, GameObject)> s_CacheTMPInputField =
            new Dictionary<TMP_InputField, (GameObject, GameObject)>();
        private static Dictionary<Dropdown, (GameObject, GameObject,GameObject, GameObject)> s_CacheDropdown =
            new Dictionary<Dropdown, (GameObject, GameObject,GameObject, GameObject)>();
        private static Dictionary<TMP_Dropdown, (GameObject, GameObject,GameObject, GameObject)> s_CacheTMP_Dropdown =
            new Dictionary<TMP_Dropdown, (GameObject, GameObject,GameObject, GameObject)>();
        
        static void ReplaceRoot(GameObject go)
        {
            s_Empty= Resources.Load<Sprite>("EmptySprite");
            Process(go);
            ProcessList(go);
            ProcessParticle(go);
            CacheComps(go);
            ReplaceImage(go);
            ReplaceRawImage(go);
            ReplaceTextMeshPro(go);
            ReplaceText(go);
            RestoreComps(go);

            ProcessUILang(go);
        }
        
        static void Process(GameObject go)
        {
            foreach (var behaviour in  go.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if(behaviour is IExportProcess)
                {
                    ((IExportProcess)behaviour).Process();
                }
            }
        }

        static void ProcessList(GameObject go)
        {
            //loop
            foreach (var view in  go.GetComponentsInChildren<LoopListView2>(true))
            {
                if (view.ItemPrefabDataList != null)
                {
                    foreach (var data in view.ItemPrefabDataList)
                    {
                        data.mItemPrefab?.SetActive(false);
                    }
                }
            }
            foreach (var view in  go.GetComponentsInChildren<LoopGridView>(true))
            {
                if (view.ItemPrefabDataList != null)
                {
                    foreach (var data in view.ItemPrefabDataList)
                    {
                        data.mItemPrefab?.SetActive(false);
                    }
                }
            }
            foreach (var view in  go.GetComponentsInChildren<LoopTreeView>(true))
            {
                if (view.ItemPrefabDataList != null)
                {
                    foreach (var data in view.ItemPrefabDataList)
                    {
                        data.mItemPrefab?.SetActive(false);
                    }
                }
            }
            
            foreach (var view in go.GetComponentsInChildren<UIHeadView>(true))
            {
                view.Prefab?.SetActive(false);
            }
            
            foreach (var view in  go.GetComponentsInChildren<UIContainer>(true))
            {
                foreach (var item in view.itemPrefabList)
                {
                    if(item!=null)  
                        item.SetActive(false);
                }
            }
        }

        static void ProcessParticle(GameObject go)
        {
            foreach (var particle in go.GetComponentsInChildren<UIParticle>(true))
                GameObject.DestroyImmediate(particle);

            foreach (var particle in go.GetComponentsInChildren<ParticleSystem>(true))
                GameObject.DestroyImmediate(particle);
        }
        static void CacheComps(GameObject go)
        {
            //Toggle
            s_CacheToggle.Clear();
            foreach (var toggle in go.GetComponentsInChildren<Toggle>(true))
            {
                if (toggle.targetGraphic != null && toggle.graphic != null)
                {
                    s_CacheToggle[toggle] = (toggle.targetGraphic.gameObject,toggle.graphic.gameObject);
                }
                else
                    Debug.LogError($"{toggle.gameObject.GetScenePath()} targetGraphic or graphic is null!");
            }
            //InputField
            s_CacheInputField.Clear();
            foreach (var text in go.GetComponentsInChildren<InputField>(true))
            {
                if (text.textComponent != null)
                {
                    s_CacheInputField[text] = (text.textComponent.gameObject,GetGO(text.placeholder));
                }
                else
                    Debug.LogError($"{text.gameObject.GetScenePath()} textComponent is null!",text);
            }
            
            //TMP_InputField 
            s_CacheTMPInputField.Clear();
            foreach (var textMeshPro in go.GetComponentsInChildren<TMP_InputField>(true))
            {
                if (textMeshPro.textComponent != null)
                {
                    s_CacheTMPInputField[textMeshPro] = (textMeshPro.textComponent.gameObject,GetGO(textMeshPro.placeholder));
                }
                else
                    Debug.LogError($"{textMeshPro.gameObject.GetScenePath()} textComponent is null!",textMeshPro);
            }
            
            //DropDown
            s_CacheDropdown.Clear();
            foreach (var tmpDropDown in go.GetComponentsInChildren<Dropdown>(true))
            {
                s_CacheDropdown[tmpDropDown] = (GetGO(tmpDropDown.captionText),GetGO(tmpDropDown.captionImage),GetGO(tmpDropDown.itemText),GetGO(tmpDropDown.itemImage));
            }
            
            //TMP_DropDown
            s_CacheTMP_Dropdown.Clear();
            foreach (var tmpDropDown in go.GetComponentsInChildren<TMP_Dropdown>(true))
            {
                s_CacheTMP_Dropdown[tmpDropDown] = (GetGO(tmpDropDown.captionText),GetGO(tmpDropDown.captionImage),GetGO(tmpDropDown.itemText),GetGO(tmpDropDown.itemImage));
            }
        }
        static void RestoreComps(GameObject go)
        {
            foreach (var c in s_CacheToggle)
            {
                c.Key.targetGraphic = c.Value.Item1.GetComponent<UIImage>();
                c.Key.graphic = c.Value.Item2.GetComponent<UIImage>();
            }
            
            foreach (var c in s_CacheInputField)
            {
                c.Key.textComponent = c.Value.Item1.GetComponent<Text>();
                if(c.Value.Item2 != null)
                    c.Key.placeholder = c.Value.Item2.GetComponent<Text>();
            }
            
            foreach (var c in s_CacheTMPInputField)
            {
                c.Key.textComponent = c.Value.Item1.GetComponent<UITextMeshPro>();
                if(c.Value.Item2 != null)
                    c.Key.placeholder = c.Value.Item2.GetComponent<UITextMeshPro>();
            }
            
            foreach (var c in s_CacheDropdown)
            {
                c.Key.captionText = SetGO<UIText>(c.Value.Item1);
                c.Key.captionImage = SetGO<UIImage>(c.Value.Item2);
                c.Key.itemText = SetGO<UIText>(c.Value.Item3);
                c.Key.itemImage = SetGO<UIImage>(c.Value.Item4);
            }
            
            foreach (var c in s_CacheTMP_Dropdown)
            {
                c.Key.captionText = SetGO<UITextMeshPro>(c.Value.Item1);
                c.Key.captionImage = SetGO<UIImage>(c.Value.Item2);
                c.Key.itemText = SetGO<UITextMeshPro>(c.Value.Item3);
                c.Key.itemImage = SetGO<UIImage>(c.Value.Item4);
            }
        }
        static void ReplaceImage(GameObject go)
        {
            foreach (var image in go.GetComponentsInChildren<Image>(true))
            {
                if(IsUIPersist(image)) continue;
                var obj = image.gameObject;
                var spritePath = AssetDatabase.GetAssetPath(image.sprite);
                Object.DestroyImmediate(image);
                var uiImage= obj.AddComponent<UIImage>();
                SerializedObject o = new SerializedObject(uiImage);

                if (image.sprite == null)
                {
                    Debug.LogError($"{obj.GetScenePath()} sprite is null!");
                }else if(IsBuiltin(spritePath))
                {
                    uiImage.sprite = image.sprite;
                }
                else if(IsProject(spritePath))
                {
                    o.FindProperty("m_SpriteName").stringValue = Path.GetFileName(spritePath);
                    o.FindProperty("m_MaterialName").stringValue = Path.GetFileName(AssetDatabase.GetAssetPath(image.material));
                    o.ApplyModifiedProperties();
                    uiImage.sprite = s_Empty;
                }
                
                uiImage.color = image.color;
                if(image.material != Graphic.defaultGraphicMaterial)
                    uiImage.material = image.material;
                uiImage.raycastTarget = image.raycastTarget;
                uiImage.raycastPadding = image.raycastPadding;
                uiImage.type = image.type;
                if (image.type == Image.Type.Simple)
                {
                    uiImage.useSpriteMesh = image.useSpriteMesh;
                    uiImage.preserveAspect = image.preserveAspect;
                } else if (image.type == Image.Type.Sliced ||image.type == Image.Type.Tiled)
                {
                    uiImage.pixelsPerUnitMultiplier = image.pixelsPerUnitMultiplier;
                }else if (image.type == Image.Type.Filled)
                {
                    uiImage.fillMethod = image.fillMethod;
                    uiImage.fillOrigin = image.fillOrigin;
                    uiImage.fillAmount= image.fillAmount;
                    uiImage.fillClockwise= image.fillClockwise;
                    uiImage.preserveAspect= image.preserveAspect;
                }
            }
        }
        
        static void ReplaceRawImage(GameObject go)
        {
            foreach (var rawImage in go.GetComponentsInChildren<RawImage>(true))
            {
                if(IsUIPersist(rawImage)) continue;
                var obj = rawImage.gameObject;
                var spritePath = AssetDatabase.GetAssetPath(rawImage.texture);
                Object.DestroyImmediate(rawImage);
                var uiRawImage= obj.AddComponent<UIRawImage>();
                SerializedObject o = new SerializedObject(uiRawImage);
                if (rawImage.texture == null)
                {
                    Debug.LogWarning($"{obj} texture is null!",obj);
                }else if (rawImage.material != Graphic.defaultGraphicMaterial)
                {
                    o.FindProperty("m_MaterialName").stringValue = $"{rawImage.material.name}.mat";
                    o.ApplyModifiedProperties(); 
                }
                if(IsBuiltin(spritePath))
                {
                    if(rawImage.texture != null)
                        uiRawImage.texture= rawImage.texture;
                    else
                        Debug.LogError($"{rawImage.gameObject.GetScenePath()} texture is null!",rawImage);
                }
                else if(IsProject(spritePath))
                {
                    o.FindProperty("m_SpriteName").stringValue = Path.GetFileName(spritePath);
                    o.ApplyModifiedProperties();
                    uiRawImage.texture = s_Empty.texture;
                }
                uiRawImage.color = rawImage.color;
                uiRawImage.raycastTarget = rawImage.raycastTarget;
                uiRawImage.raycastPadding = rawImage.raycastPadding;
                uiRawImage.maskable = rawImage.maskable;
                uiRawImage.uvRect = rawImage.uvRect;
            }
        }
        
        public static void ReplaceTextMeshPro(GameObject go)
        {
            foreach (var textMeshPro in go.GetComponentsInChildren<TextMeshProUGUI>(true))
            {
                var obj = textMeshPro.gameObject;
                string content = textMeshPro.text;
                Object.DestroyImmediate(textMeshPro);
                var uiTextMeshPro= obj.AddComponent<UITextMeshPro>();
                SerializedObject o = new SerializedObject(uiTextMeshPro);
                if (textMeshPro.font != null)
                {
                    o.FindProperty("m_FontName").stringValue = $"{textMeshPro.font.name}.asset";
                }
                else
                    Debug.LogError($"{obj.GetScenePath()} font is null!",obj);
                var materialPath = AssetDatabase.GetAssetPath(textMeshPro.fontSharedMaterial);
                if(IsProject(materialPath))
                {
                    if (materialPath != AssetDatabase.GetAssetPath(textMeshPro.font))
                    {
                        o.FindProperty("m_MaterialName").stringValue = Path.GetFileName(materialPath);
                    }
                }
                o.ApplyModifiedProperties();

                //others
                uiTextMeshPro.text = content;
                uiTextMeshPro.fontStyle = textMeshPro.fontStyle;
                uiTextMeshPro.fontSize = textMeshPro.fontSize;
                uiTextMeshPro.color = textMeshPro.color;
                uiTextMeshPro.alignment = textMeshPro.alignment;
                uiTextMeshPro.enableWordWrapping = textMeshPro.enableWordWrapping;
                uiTextMeshPro.horizontalMapping = textMeshPro.horizontalMapping;
                uiTextMeshPro.verticalMapping = textMeshPro.verticalMapping;
                uiTextMeshPro.overflowMode = textMeshPro.overflowMode;
                uiTextMeshPro.raycastTarget = textMeshPro.raycastTarget;
                uiTextMeshPro.raycastPadding = textMeshPro.raycastPadding;
                uiTextMeshPro.maskable = textMeshPro.maskable;
                uiTextMeshPro.margin = textMeshPro.margin;
                uiTextMeshPro.enableVertexGradient = textMeshPro.enableVertexGradient;
                uiTextMeshPro.colorGradient = textMeshPro.colorGradient;
                uiTextMeshPro.lineSpacing = textMeshPro.lineSpacing;
                uiTextMeshPro.characterSpacing = textMeshPro.characterSpacing;
                uiTextMeshPro.enableAutoSizing = textMeshPro.enableAutoSizing;
                uiTextMeshPro.fontSizeMax = textMeshPro.fontSizeMax;
                uiTextMeshPro.fontSizeMin = textMeshPro.fontSizeMin;
                uiTextMeshPro.enableAutoSizing = textMeshPro.enableAutoSizing;
                uiTextMeshPro.fontSizeMax = textMeshPro.fontSizeMax;
                uiTextMeshPro.fontSizeMin = textMeshPro.fontSizeMin;
                uiTextMeshPro.enableKerning = textMeshPro.enableKerning;
            }
        }
        
        public static void ReplaceText(GameObject go)
        {
            foreach (var text in go.GetComponentsInChildren<Text>(true))
            {
                var obj = text.gameObject;
                var content = text.text;
                Object.DestroyImmediate(text);
                var uiText = obj.AddComponent<UIText>();
                SerializedObject o = new SerializedObject(uiText);
                bool freefont = false;
                if (text.font != null)
                {
                    if(IsProject(AssetDatabase.GetAssetPath(text.font)))
                    {
                        o.FindProperty("m_FontName").stringValue = $"{text.font.name}.ttf";
                        freefont = true; 
                    }
                }
                else
                    Debug.LogError($"{obj.GetScenePath()} font is null!",obj);
                var materialPath = AssetDatabase.GetAssetPath(text.material);
                if(IsProject(materialPath))
                {
                    o.FindProperty("m_MaterialName").stringValue = Path.GetFileName(materialPath);
                }
                o.ApplyModifiedProperties();

                if(freefont) //need before check
                    uiText.font = null;
                uiText.text = content;
                uiText.fontStyle = text.fontStyle;
                uiText.fontSize = text.fontSize;
                uiText.color = text.color;
                uiText.alignment = text.alignment;
                uiText.raycastTarget = text.raycastTarget;
                uiText.raycastPadding = text.raycastPadding;
                uiText.maskable = text.maskable;
                uiText.resizeTextForBestFit = text.resizeTextForBestFit;
                uiText.horizontalOverflow = text.horizontalOverflow;
                uiText.verticalOverflow = text.verticalOverflow;
                uiText.resizeTextForBestFit = text.resizeTextForBestFit;
                uiText.resizeTextMinSize = text.resizeTextMinSize;
                uiText.resizeTextMaxSize = text.resizeTextMaxSize;
                uiText.lineSpacing = text.lineSpacing;
            }
        }
        
        public static void ProcessUILang(GameObject go)
        {
            bool hasUILang = false;
            foreach (var uiTmp in go.GetComponentsInChildren<UITextMeshPro>(true))
            {
                if(uiTmp.gameObject.name.StartsWith(kPrefixE) && !uiTmp.TryGetComponent(typeof(UIIgnore), out var component)) continue;
                var obj = uiTmp.gameObject;
                var content = uiTmp.text;
                if (string.IsNullOrEmpty(content) || !ContainsChinese(content))
                {
                    if (obj.TryGetComponent<UILang>(out var lang))
                    {
                        UnityEngine.Object.DestroyImmediate(lang, true);
                    }
                }
                else
                {
                    hasUILang = true;
                    var library = obj.transform.root.GetComponentInChildren<UILangLibrary>();
                    if (!library)
                    {
                        library = obj.transform.root.GetAddComponent<UILangLibrary>();
                    }
                    if (library)
                    {
                        var uiLang = obj.GetAddComponent<UILang>();
                        if (uiLang)
                        {
                            uiLang.key = ComputeStableHash(content);
                            
                            var field = uiTmp.GetType().GetField("m_lang", BindingFlags.NonPublic | BindingFlags.Instance);
                            
                            field.SetValue(uiTmp, new LangLibraryData() { library = library, key = uiLang.key });

                            var langData = library.langs.Find(x => x.key == uiLang.key);
                            if (langData == null)
                            {
                                library.langs.Add(new UILangLibrary.LangData()
                                {
                                    key = uiLang.key,
                                    preview = content
                                });
                            }
                            uiTmp.text = "";
                        }
                    }
                }
            }
            
            foreach (var uiText in go.GetComponentsInChildren<UIText>(true))
            {
                if(uiText.gameObject.name.StartsWith(kPrefixE) && !uiText.TryGetComponent(typeof(UIIgnore), out var component)) continue;
                var obj = uiText.gameObject;
                var content = uiText.text;

                if (string.IsNullOrEmpty(content) || !ContainsChinese(content))
                {
                    if (obj.TryGetComponent<UILang>(out var lang))
                    {
                        UnityEngine.Object.DestroyImmediate(lang, true);
                    }
                }
                else
                {
                    hasUILang = true;
                    var library = obj.transform.root.GetComponentInChildren<UILangLibrary>();
                    if (!library)
                    {
                        library = obj.transform.root.GetAddComponent<UILangLibrary>();
                    }
                    if (library)
                    {
                        var uiLang = obj.GetAddComponent<UILang>();

                        if (uiLang)
                        {
                            uiLang.key = ComputeStableHash(content);
                            
                            var field = uiText.GetType().GetField("m_lang", BindingFlags.NonPublic | BindingFlags.Instance);

                            field.SetValue(uiText, new LangLibraryData() { library = library, key = uiLang.key });

                            var langData = library.langs.Find(x => x.key == uiLang.key);
                            if (langData == null)
                            {
                                library.langs.Add(new UILangLibrary.LangData()
                                {
                                    key = uiLang.key,
                                    preview = content
                                });
                            }
                            uiText.text = "";
                        }
                    }
                }
            }

            if (!hasUILang)
            {
                UnityEngine.Object.DestroyImmediate(go.GetComponentInChildren<UILangLibrary>(), false);
            }
        }

        static bool IsUIPersist(Component comp)
        {
            return comp.GetComponent<UIPersist>() != null;
        }

        static bool IsIgnoreOtherComp(GameObject comp)
        {
            return comp.GetComponent<UIIgnore>()?.ignoreOtherComp ?? false;
        }
        
        static bool IsBuiltin(string path)
        {
            return path == "Resources/unity_builtin_extra";
        }
        public static bool IsProject(string path)
        {
            return path.StartsWith("Assets");
        }
        
        static bool IsUIAtlas(string path)
        {
            var ext = Path.GetExtension(path).ToLower();
            return ext == ".spriteatlasv2";
        }

        static GameObject GetGO(Component comp)
        {
            if (comp != null)
                 return comp.gameObject;
            return null;
        }
        static T SetGO<T>(GameObject go) where T : Component
        {
            if (go != null)
                return go.GetComponent<T>();
            return null;
        }
    }
}