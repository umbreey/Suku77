using System;
using TMPro;
using TMPro.EditorUtilities;
using UnityEditor;
using UnityEngine;

// https://gist.github.com/yasirkula/edf4ba1bb7c5c18800e791e01bc9bcd7?continueFlag=3e21b3ba5417cd3e23d4eee90f83650f
[CustomEditor(typeof(TMP_FontAsset))]
public class TMP_FontAssetSaveDisabler : TMP_FontAssetEditor
{
    private static bool FontAssetsLocked
    {
        get => EditorPrefs.GetBool("TMPFontsLocked", true);
        set => EditorPrefs.SetBool("TMPFontsLocked", value);
    }

    public override void OnInspectorGUI()
    {
        if (GUILayout.Button(FontAssetsLocked ? "Unlock Font Assets" : "Lock Font Assets"))
        {
            FontAssetsLocked = !FontAssetsLocked;
            GUIUtility.ExitGUI();
        }

        if (!FontAssetsLocked)
        {
            EditorGUILayout.Space();
            base.OnInspectorGUI();
        }
    }

    // Credit: https://discussions.unity.com/t/tmpro-dynamic-font-asset-constantly-changes-in-source-control/868941/29
    private class SaveHandler : AssetModificationProcessor
    {
        private static string[] OnWillSaveAssets(string[] paths)
        {
            if (FontAssetsLocked)
            {
                int index = 0;
                foreach (string path in paths)
                {
                    if (!typeof(TMP_FontAsset).IsAssignableFrom(AssetDatabase.GetMainAssetTypeAtPath(path)))
                        paths[index++] = path;
                }

                Array.Resize(ref paths, index);
            }

            return paths;
        }
    }
}