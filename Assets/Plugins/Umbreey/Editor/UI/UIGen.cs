using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using SuperScrollView;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Umbreey.Editor
{
    public partial class UIGen
    {
        private const string kPrefixE = "e_";
        private const string kPrefixP = "p_";
        private const string kPrefixT = "t_";
        
        public class Config
        {
            public string BaseUI = "UIView";   //自定义生成UI类名（默认UIView）
            public string BaseUISub = "UIViewSub"; //自定义生成UISub类名（默认UIViewSub）
            public string BasePath;            //生成代码路径
            public string SrcPrefabPath;       //制作Prefab路径
            public string DstPrefabPath;       //生成Prefab路径
        }

        /// <summary>
        /// 批量生成UI（处理单个GameObject实例）
        /// </summary>
        /// <param name="go">GameObject实例（必须是通过Instantiate创建的副本）</param>
        /// <param name="config">配置</param>
        /// <param name="srcPrefabPath">源Prefab路径</param>
        public static void GenSingle(GameObject go, Config config, string srcPrefabPath)
        {
            const string kCopyright = "// GEN by Umbreey! Do NOT edit it.";
            
            List<GameObject> rootExport = new List<GameObject>();
            Dictionary<GameObject, List<GameObject>> pageExport = new Dictionary<GameObject, List<GameObject>>();
            Dictionary<GameObject, List<GameObject>> listItemExport = new Dictionary<GameObject, List<GameObject>>();
            
            ParseRoot(go, rootExport, pageExport, listItemExport);
            ReplaceRoot(go);
            
            string baseName = $"Base{go.name}";
            string baseFile = $"{config.BasePath}/Base{go.name}.cs";
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(kCopyright);
            sb.AppendLine($"using System.Collections;");
            sb.AppendLine($"using System.Collections.Generic;");
            sb.AppendLine($"using UnityEngine;");
            sb.AppendLine($"using Umbreey;");
            sb.AppendLine($"public class {baseName} : {config.BaseUI}");
            sb.AppendLine("{");
            sb.AppendLine($"{GenCode(go, rootExport, 1)}");
            
            if (pageExport.Count > 0)
                sb.AppendLine($"{GenPageBindCode(go, pageExport, 1)}");
                
            List<string> names = new List<string>();
            foreach (var page in pageExport)
            {
                string uniqueName = ObjectNames.GetUniqueName(names.ToArray(), page.Key.name);
                names.Add(uniqueName);
                string pageName = NormalizeName(uniqueName, kPrefixP);
                
                sb.AppendLine($"\t//Path:{AnimationUtility.CalculateTransformPath(page.Key.transform, go.transform)}");
                sb.AppendLine($"\tpublic class Base{pageName} : {config.BaseUISub}");
                sb.AppendLine("\t{");
                sb.AppendLine($"{GenCode(page.Key, page.Value, 2)}");
                sb.AppendLine("\t}");
            }
            
            names.Clear();
            foreach (var listItem in listItemExport)
            {
                string uniqueName = ObjectNames.GetUniqueName(names.ToArray(), listItem.Key.name);
                names.Add(uniqueName);
                string pageName = NormalizeName(uniqueName, kPrefixT);
                
                sb.AppendLine($"\t//Path:{AnimationUtility.CalculateTransformPath(listItem.Key.transform, go.transform)}");
                sb.AppendLine($"\tpublic class {pageName} : UIHandler");
                sb.AppendLine("\t{");
                sb.AppendLine($"{GenListItemCode(listItem.Key, listItem.Value, 2)}");
                sb.AppendLine("\t}");
                sb.AppendLine("");
            }
            sb.AppendLine("}");
            sb.AppendLine(kCopyright);
            File.WriteAllText(baseFile, sb.ToString());
            
            // 保存Prefab - 使用SaveAsPrefabAssetAndConnect保留Override属性
            var destPrefabPath = $"{config.DstPrefabPath}/{go.name}.prefab";
            
            // 确保目录存在
            string directory = Path.GetDirectoryName(destPrefabPath);
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
            
            // 使用SaveAsPrefabAssetAndConnect保存，保留Canvas等组件的属性
            PrefabUtility.SaveAsPrefabAssetAndConnect(go, destPrefabPath, InteractionMode.AutomatedAction);
            
            Debug.Log($"Done : {destPrefabPath}");
        }
        
        private static void Gen(Config config)
        {
            const string kCopyright = "// GEN by Umbreey! Do NOT edit it.";

            if (!string.IsNullOrEmpty(config.BasePath) && !Directory.Exists(config.BasePath))
                Directory.CreateDirectory(config.BasePath);
            if (!string.IsNullOrEmpty(config.DstPrefabPath) && !Directory.Exists(config.DstPrefabPath))
                Directory.CreateDirectory(config.DstPrefabPath);
            
            AssetDatabase.StartAssetEditing();
            
            List<GameObject> rootGo = new List<GameObject>();
            var prefabStage = UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage();
            if (prefabStage)
            {
                PrefabUtility.SaveAsPrefabAssetAndConnect(prefabStage.prefabContentsRoot, prefabStage.assetPath,InteractionMode.AutomatedAction);
                rootGo.Add(prefabStage.prefabContentsRoot);
            }
            else
            {
                rootGo.AddRange(SceneManager.GetActiveScene().GetRootGameObjects()); 
            }
            
            foreach (var raw in rootGo)
            {
                if (!raw.name.StartsWith("UI"))
                {
                    Debug.LogError($"{raw.name} is not start with UI,ignore it!");
                    continue;
                }
                if (raw.activeSelf)
                {
                    var prefab = PrefabUtility.GetCorrespondingObjectFromSource(raw);
                    var prefabpath = AssetDatabase.GetAssetPath(prefab);
                    if (prefabpath.Contains($"{config.SrcPrefabPath}/"))
                    {
                        var go = GameObject.Instantiate(raw, null, false);
                        go.name = raw.name;
                        List<GameObject> rootExport = new List<GameObject>();
                        Dictionary<GameObject, List<GameObject>> pageExport = new Dictionary<GameObject, List<GameObject>>();
                        Dictionary<GameObject, List<GameObject>> listItemExport = new Dictionary<GameObject, List<GameObject>>();
                        
                        ParseRoot(go, rootExport, pageExport,listItemExport);
                        ReplaceRoot(go);
                        string baseName = $"Base{go.name}";
                        string baseFile = $"{config.BasePath}/Base{go.name}.cs";
                        StringBuilder sb = new StringBuilder();
                        sb.AppendLine(kCopyright);
                        sb.AppendLine($"using System.Collections;");
                        sb.AppendLine($"using System.Collections.Generic;");
                        sb.AppendLine($"using UnityEngine;");
                        sb.AppendLine($"using Umbreey;");
                        sb.AppendLine($"public class {baseName} : {config.BaseUI}");
                        sb.AppendLine("{");
                        sb.AppendLine($"{GenCode(go, rootExport, 1)}");
                        if (pageExport.Count > 0)
                            sb.AppendLine($"{GenPageBindCode(go, pageExport, 1)}");
                        List<string> names = new List<string>();
                        foreach (var page in pageExport)
                        {
                            string uniqueName = ObjectNames.GetUniqueName(names.ToArray(),page.Key.name);
                            names.Add(uniqueName);
                            string pageName = NormalizeName(uniqueName, kPrefixP);
                                
                            sb.AppendLine(
                                $"\t//Path:{AnimationUtility.CalculateTransformPath(page.Key.transform, go.transform)}");
                            sb.AppendLine($"\tpublic class Base{pageName} : {config.BaseUISub}");
                            sb.AppendLine("\t{");
                            sb.AppendLine($"{GenCode(page.Key, page.Value, 2)}");
                            sb.AppendLine("\t}");
                        }
                        names.Clear();
                        foreach (var listItem in listItemExport)
                        {
                            string uniqueName = ObjectNames.GetUniqueName(names.ToArray(),listItem.Key.name);
                            names.Add(uniqueName);
                            string pageName = NormalizeName(uniqueName, kPrefixT);
                            
                            sb.AppendLine(
                                $"\t//Path:{AnimationUtility.CalculateTransformPath(listItem.Key.transform, go.transform)}");
                            sb.AppendLine($"\tpublic class {pageName} : UIHandler");
                            sb.AppendLine("\t{");
                            sb.AppendLine($"{GenListItemCode(listItem.Key, listItem.Value ,2)}");
                            sb.AppendLine("\t}");
                            sb.AppendLine("");
                        }
                        //sb.AppendLine(GenLangCode(go,1));
                        sb.AppendLine("}");
                        sb.AppendLine(kCopyright);
                        File.WriteAllText(baseFile, sb.ToString());

                        var destPrefabpath = $"{config.DstPrefabPath}/{prefab.name}.prefab";
                        PrefabUtility.SaveAsPrefabAssetAndConnect(raw, prefabpath,InteractionMode.AutomatedAction);
                        PrefabUtility.SaveAsPrefabAssetAndConnect(go, destPrefabpath,InteractionMode.AutomatedAction);
                        GameObject.DestroyImmediate(go);
                        Debug.Log($"Done : {destPrefabpath}");
                    }
                }
            }
            AssetDatabase.StopAssetEditing();
            AssetDatabase.Refresh();
            Debug.Log($"AllDone");
            
            GenDef();
        }

        static void GenDef()
        {
            var config = new Config()
            {
                BasePath = "Assets/HotUpdate/UI",
                SrcPrefabPath = "Assets/AssetBundles/UI/Prefab",
                DstPrefabPath = "Assets/AssetBundles/UI/Prefab/Gen",
            };
            UIDefGen.Gen(config);
            UICanvasToggleGen.Gen(config);
        }

        static void ParseRoot(GameObject go,
            List<GameObject> rootList,
            Dictionary<GameObject,List<GameObject>>page,
            Dictionary<GameObject,List<GameObject>>list)
        {
            foreach (Transform trans in go.transform)
            {
                string name = trans.name;
                if (name.StartsWith(kPrefixP))
                {
                    ParsePage(trans.gameObject,trans.gameObject,page,list);
                    continue;
                }else if (name.StartsWith(kPrefixT))
                {
                    ParseTemplate(trans.gameObject,trans.gameObject,list);
                    continue;
                }             
                else if (name.StartsWith(kPrefixE))
                {
                    rootList.Add(trans.gameObject);
                }
                ParseRoot(trans.gameObject, rootList, page,list);
            }
        }

        static void ParsePage(GameObject root, 
            GameObject child,
            Dictionary<GameObject,List<GameObject>> page,
            Dictionary<GameObject,List<GameObject>> list)
        {
            if (!page.ContainsKey(root)) page[root] = new List<GameObject>();
            foreach (Transform trans in child.transform)
            {
                string name = trans.name;
                var go = trans.gameObject;
                if (name.StartsWith(kPrefixP))
                {
                    ParsePage(go,go, page,list);
                    continue;
                }else if (name.StartsWith(kPrefixT))
                {
                    ParseTemplate(go,go,list);
                    continue;
                } 
                else if (name.StartsWith(kPrefixE))
                {
                    page[root].Add(go);
                }
                
                ParsePage(root,go, page,list);
            }
        }

        static void ParseTemplate(GameObject root, 
            GameObject child,
            Dictionary<GameObject,List<GameObject>> list)
        {
            if (!list.ContainsKey(root)) list[root] = new List<GameObject>();

            foreach (Transform trans in child.transform)
            {
                string name = trans.name;
                var go = trans.gameObject;
                if (name.StartsWith(kPrefixT))
                {
                    ParseTemplate(go,go,list);
                    continue;
                } 
                else if (name.StartsWith(kPrefixE))
                {
                    list[root].Add(go);
                }
                
                ParseTemplate(root,go,list);
            }
        }
        static List<Component> CollectBind(List<GameObject> export)
        {
            List<Component> bind = new List<Component>();
      
            foreach (var gameObject in export)
            {
                bool iExport = false;

                foreach (var behaviour in  gameObject.GetComponents<MonoBehaviour>())
                {
                    if(behaviour is IExport)
                    {
                        bind.Add(behaviour);
                        iExport = true;
                        break;
                    }
                }

                if (!iExport)
                {
                    if (IsIgnoreOtherComp(gameObject) && gameObject.TryGetComponent(typeof(RectTransform), out var rectTransform))
                    {
                        bind.Add(rectTransform);
                        continue;
                    }
                    
                    List<Type> exportOrder = new List<Type>()
                    {
                        typeof(LoopListView2),
                        typeof(LoopGridView),
                        typeof(LoopTreeView),
                        typeof(LoopStaggeredGridView),
                        typeof(UIContainer),
                        typeof(ScrollRect),
                        typeof(Button),
                        typeof(Toggle),
                        typeof(Animation),
                        typeof(Animator),
                        typeof(UITweener),
                        typeof(Slider),
                        typeof(Scrollbar),
                        typeof(TMP_InputField),
                        typeof(InputField),
                        typeof(TMP_Dropdown),
                        typeof(Dropdown),
                        typeof(TextMeshProUGUI),
                        typeof(Text),
                        typeof(Image),
                        typeof(RawImage),
                        typeof(ToggleGroup),
                        typeof(CanvasGroup),
                        typeof(Camera),
                        typeof(RectTransform),
                        typeof(Transform),
                    };
                    
                    foreach (Type order in exportOrder)
                    {
                        if (gameObject.TryGetComponent(order, out var comp))
                        {
                            bind.Add(comp);
                            break;
                        }
                    }
                }
            }
            return bind;
        }
        
        static string GenCode(GameObject go,List<GameObject> export,int t)
        {
            string space="";
            for (int i = 0; i < t; i++)
                space += "\t";

            UIBind bind = go.GetComponent<UIBind>() ?? go.AddComponent<UIBind>();
            bind.Bind=CollectBind(export);
            StringBuilder sb = new StringBuilder();
            StringBuilder onCreate = new StringBuilder();
            List<string> names = new List<string>();
            for (int i = 0; i < bind.Bind.Count; i++)
            {
                var comp = bind.Bind[i];
                string uniqueName = ObjectNames.GetUniqueName(names.ToArray(), comp.name);
                names.Add(uniqueName);
                string propName = "m_"+NormalizeName(uniqueName, kPrefixE);
                sb.AppendLine($"{space}protected {comp.GetType()} {propName};");
                onCreate.AppendLine($"{space}\t{propName} = m_Bind.Bind[{i}] as {comp.GetType()};");
            }
            onCreate.AppendLine($"{space}\tOnBindEvents(m_Bind.Bind);");
            sb.AppendLine($"{space}private UIBind m_Bind;\n");
            sb.AppendLine($"{space}protected override void OnCreated()");
            sb.AppendLine($"{space}{{");
            sb.AppendLine($"{space}\tbase.OnCreated();");
            sb.AppendLine($"{space}\tm_Bind = transform.GetComponent<UIBind>();");
            sb.Append(onCreate.ToString());
            sb.AppendLine($"{space}}}");
            return sb.ToString();
        }
        
        static string GenPageBindCode(GameObject go,Dictionary<GameObject, List<GameObject>> pageExport,int t)
        {
            string space="";
            for (int i = 0; i < t; i++)
                space += "\t";
            
            UIBind bind = go.GetComponent<UIBind>();
            StringBuilder sb = new StringBuilder();
            List<string> names = new List<string>();
            foreach (var page in pageExport)
            {
                string uniqueName = ObjectNames.GetUniqueName(names.ToArray(), page.Key.name);
                names.Add(uniqueName);
                string pageName = NormalizeName(uniqueName, kPrefixP);
                bind.Bind.Add(page.Key.transform);
                sb.AppendLine(
                    $"\t//Path:{AnimationUtility.CalculateTransformPath(page.Key.transform, go.transform)}");
                sb.AppendLine($"{space}protected T Bind{pageName}<T>()where T : UIViewSub");
                sb.AppendLine($"{space}{{");
                sb.AppendLine($"{space}\tT view = (T)System.Activator.CreateInstance(typeof(T));");
                sb.AppendLine($"{space}\tOnBindSubPage(view,m_Bind.Bind[{bind.Bind.Count-1}].gameObject);");
                sb.AppendLine($"{space}\treturn view;");
                sb.AppendLine($"{space}}}");
            }

            return sb.ToString();
        }
        
        static string GenListItemCode(GameObject go, List<GameObject> export, int t)
        {
            string space="";
            for (int i = 0; i < t; i++)
                space += "\t";
            
            UIBindItem bind = go.GetComponent<UIBindItem>() ?? go.AddComponent<UIBindItem>();
            bind.Bind = CollectBind(export);
            StringBuilder sb = new StringBuilder();
            StringBuilder onCreate = new StringBuilder();
            List<string> names = new List<string>();
            for (int i = 0; i < bind.Bind.Count; i++)
            {
                var comp = bind.Bind[i];
                string uniqueName = ObjectNames.GetUniqueName(names.ToArray(), comp.name);
                names.Add(comp.name);
                string propName = NormalizeName(uniqueName, kPrefixE);
                
                sb.AppendLine($"{space}public {comp.GetType()} {propName};");
                onCreate.AppendLine($"{space}\t{propName} = m_Bind.Bind[{i}] as {comp.GetType()};");
            }
            onCreate.AppendLine($"{space}\tOnBindEvents(m_Bind.Bind);");
            sb.AppendLine($"{space}private UIBindItem m_Bind;\n");
            sb.AppendLine($"{space}protected override void OnCreated()");
            sb.AppendLine($"{space}{{");
            sb.AppendLine($"{space}\tbase.OnCreated();");
            sb.AppendLine($"{space}\tm_Bind = transform.GetComponent<UIBindItem>();");
            sb.Append(onCreate.ToString());
            sb.AppendLine($"{space}}}");
            return sb.ToString().TrimEnd();
        }

        static string GenLangCode(GameObject root, int t)
        {
            StringBuilder sb = new StringBuilder();
            if (root.TryGetComponent<UILangLibrary>(out var library))
            {
                string space = "";
                for (int i = 0; i < t; i++)
                    space += "\t";
                sb.Append($"{space}public class LangKey").AppendLine();
                sb.Append($"{space}{{").AppendLine();
                foreach (var lang in  library.langs)
                {
                    if(lang != null)
                        sb.Append($"{space}\tpublic static string {lang.key} => Umbreey.Lang.GetText(\"{lang.key}\", \"{lang.preview}\");").AppendLine();
                }
                sb.Append($"{space}}}").AppendLine();
            }
            return sb.ToString();
        }

        static string NormalizeName(string input, string prefix)
        {
            if (string.IsNullOrEmpty(input) || prefix == null)
            {
                return input;
            }

            string normalized = Regex.Replace(input, "[^_0-9a-zA-Z]", "");
            normalized = normalized.Replace(prefix, "");
            
            return char.ToUpper(normalized[0]) + normalized.Substring(1);
        }
    }

}
