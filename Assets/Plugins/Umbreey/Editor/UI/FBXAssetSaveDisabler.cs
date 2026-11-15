using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;

[CustomEditor(typeof(ModelImporter))]
[CanEditMultipleObjects]
public class FBXAssetSaveDisabler : Editor
{
    private const string FBXAssetsLockedKey = "FBXAssetsLocked";

    private static bool? s_FBXAssetsLocked;

    private static readonly Type ModelImporterEditorType =
        typeof(Editor).Assembly.GetType("UnityEditor.ModelImporterEditor");

    private static readonly MethodInfo SetAssetImporterTargetEditorMethod =
        ModelImporterEditorType?.GetMethod(
            "InternalSetAssetImporterTargetEditor",
            BindingFlags.Instance | BindingFlags.NonPublic);

    private AssetImporterEditor m_ModelImporterEditor;
    private Editor m_ModelAssetEditor;
    private bool m_IsSavingChanges;
    private bool m_IsDrawingPreview;
    private bool m_HasFBXTarget;

    internal static bool FBXAssetsLocked
    {
        get
        {
            if (!s_FBXAssetsLocked.HasValue)
                s_FBXAssetsLocked = EditorPrefs.GetBool(FBXAssetsLockedKey, true);

            return s_FBXAssetsLocked.Value;
        }
        set
        {
            if (s_FBXAssetsLocked.HasValue && s_FBXAssetsLocked.Value == value)
                return;

            s_FBXAssetsLocked = value;
            EditorPrefs.SetBool(FBXAssetsLockedKey, value);
        }
    }

    private void OnEnable()
    {
        m_HasFBXTarget = HasFBXTarget();

        if (!AreSelectedAssetsLocked)
            CreateModelImporterEditor();
    }

    private void OnDisable()
    {
        DestroyModelImporterEditor();
    }

    public override void OnInspectorGUI()
    {
        bool assetsLocked = AreSelectedAssetsLocked;
        if (m_HasFBXTarget
            && GUILayout.Button(assetsLocked ? "Unlock FBX Assets" : "Lock FBX Assets"))
        {
            assetsLocked = !assetsLocked;
            FBXAssetsLocked = assetsLocked;

            if (assetsLocked)
                DestroyModelImporterEditor();
            else
                CreateModelImporterEditor();

            GUIUtility.ExitGUI();
        }
        
        if (assetsLocked)
        {
            // Another Inspector may have changed the shared lock state.
            DestroyModelImporterEditor();
            return;
        }

        if (m_ModelImporterEditor == null)
            CreateModelImporterEditor();
        
        EditorGUILayout.Space();
        
        if (m_ModelImporterEditor == null)
        {
            EditorGUILayout.HelpBox("Unity's built-in Model Importer editor could not be loaded.", MessageType.Error);
            return;
        }
        
        m_ModelImporterEditor.OnInspectorGUI();
        hasUnsavedChanges = m_ModelImporterEditor.hasUnsavedChanges;
        saveChangesMessage = m_ModelImporterEditor.saveChangesMessage;
    }

    public override void SaveChanges()
    {
        base.SaveChanges();
    
        if (m_IsSavingChanges || m_ModelImporterEditor == null)
            return;
    
        try
        {
            m_IsSavingChanges = true;
            m_ModelImporterEditor.SaveChanges();
        }
        finally
        {
            m_IsSavingChanges = false;
        }
    }
    
    public override void DiscardChanges()
    {
        base.DiscardChanges();
        m_ModelImporterEditor?.DiscardChanges();
    }

    public override bool HasPreviewGUI()
    {
        return !AreSelectedAssetsLocked
               && m_ModelImporterEditor != null
               && m_ModelImporterEditor.HasPreviewGUI();
    }

    public override GUIContent GetPreviewTitle()
    {
        return m_ModelImporterEditor != null
            ? m_ModelImporterEditor.GetPreviewTitle()
            : new GUIContent(target != null ? target.name : "FBX Preview");
    }

    public override string GetInfoString()
    {
        return m_ModelImporterEditor != null
            ? m_ModelImporterEditor.GetInfoString()
            : string.Empty;
    }

    public override void OnPreviewSettings()
    {
        if (!AreSelectedAssetsLocked)
            m_ModelImporterEditor?.OnPreviewSettings();
    }

    public override void OnPreviewGUI(Rect previewArea, GUIStyle background)
    {
        DrawModelPreview(previewArea, background, false);
    }

    public override void OnInteractivePreviewGUI(Rect previewArea, GUIStyle background)
    {
        DrawModelPreview(previewArea, background, true);
    }

    public override bool RequiresConstantRepaint()
    {
        return !AreSelectedAssetsLocked
               && m_ModelImporterEditor != null
               && m_ModelImporterEditor.RequiresConstantRepaint();
    }

    private void DrawModelPreview(Rect previewArea, GUIStyle background, bool interactive)
    {
        if (AreSelectedAssetsLocked || m_ModelImporterEditor == null || m_IsDrawingPreview)
            return;

        try
        {
            m_IsDrawingPreview = true;

            if (interactive)
                m_ModelImporterEditor.OnInteractivePreviewGUI(previewArea, background);
            else
                m_ModelImporterEditor.OnPreviewGUI(previewArea, background);
        }
        finally
        {
            m_IsDrawingPreview = false;
        }
    }

    private bool AreSelectedAssetsLocked => m_HasFBXTarget && FBXAssetsLocked;

    private bool HasFBXTarget()
    {
        foreach (UnityEngine.Object inspectedTarget in targets)
        {
            if (IsFBXAsset(AssetDatabase.GetAssetPath(inspectedTarget)))
                return true;
        }

        return false;
    }
    
    private void CreateModelImporterEditor()
    {
        DestroyModelImporterEditor();
    
        if (ModelImporterEditorType == null)
            return;
    
        m_ModelImporterEditor = CreateEditor(targets, ModelImporterEditorType) as AssetImporterEditor;
        if (m_ModelImporterEditor == null)
            return;

        m_ModelAssetEditor = CreateModelAssetEditor();
        if (m_ModelAssetEditor != null && SetAssetImporterTargetEditorMethod != null)
        {
            SetAssetImporterTargetEditorMethod.Invoke(
                m_ModelImporterEditor,
                new object[] { m_ModelAssetEditor });
        }
    }

    private Editor CreateModelAssetEditor()
    {
        UnityEngine.Object[] modelAssets = new UnityEngine.Object[targets.Length];
        for (int i = 0; i < targets.Length; i++)
        {
            string assetPath = AssetDatabase.GetAssetPath(targets[i]);
            modelAssets[i] = AssetDatabase.LoadMainAssetAtPath(assetPath);
            if (modelAssets[i] == null)
                return null;
        }

        return CreateEditor(modelAssets);
    }
    
    private void DestroyModelImporterEditor()
    {
        if (m_ModelImporterEditor != null)
        {
            DestroyImmediate(m_ModelImporterEditor);
            m_ModelImporterEditor = null;
        }

        if (m_ModelAssetEditor != null)
        {
            DestroyImmediate(m_ModelAssetEditor);
            m_ModelAssetEditor = null;
        }
    }

    private static bool IsFBXAsset(string path)
    {
        return string.Equals(Path.GetExtension(path), ".fbx", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".fbx.meta", StringComparison.OrdinalIgnoreCase);
    }

    private class SaveHandler : AssetModificationProcessor
    {
        private static string[] OnWillSaveAssets(string[] paths)
        {
            if (!FBXAssetsLocked)
                return paths;
        
            int index = 0;
            foreach (string path in paths)
            {
                if (!IsFBXAsset(path))
                    paths[index++] = path;
            }
        
            Array.Resize(ref paths, index);
            return paths;
        }
    }
}
