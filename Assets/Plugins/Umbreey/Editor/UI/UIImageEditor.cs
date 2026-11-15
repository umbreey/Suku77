using UnityEditor;
using UnityEditor.UI;
using UnityEngine;
using UnityEngine.UI;

namespace Umbreey.Editor
{
    [CustomEditor(typeof(UIImage)), CanEditMultipleObjects]
    public class UIImageEditor : ImageEditor
    {
        private SerializedProperty m_HDRIntensity;

        protected override void OnEnable()
        {
            base.OnEnable();
            m_HDRIntensity = serializedObject.FindProperty("m_HDRIntensity");
        }

        public override void OnInspectorGUI()
        {
            base.OnInspectorGUI();

            serializedObject.Update();
            EditorGUILayout.Space();
            var uiImage = target as UIImage;
            if (uiImage != null)
            {
                Material material = uiImage.material;
                if (material != null && material.shader != null && material.shader.name == "Artist/UI/HDR Intensity")
                {
                    EditorGUILayout.PropertyField(m_HDRIntensity, new GUIContent(
                        "HDR Brightness",
                        "1 is the original brightness. Values above 1 compensate for Tonemapping compression."));
                }
            }

            serializedObject.ApplyModifiedProperties();

            if (m_HDRIntensity.hasMultipleDifferentValues || m_HDRIntensity.floatValue <= 1f)
                return;

            foreach (Object selectedTarget in targets)
            {
                var image = selectedTarget as UIImage;
                if (image == null) continue;

                Material material = image.material;
                if (material == null || material.shader == null || material.shader.name != "Artist/UI/HDR Intensity")
                {
                    EditorGUILayout.HelpBox(
                        "HDR Brightness needs the M_UI_HDRIntensity material. Assign it to this Image's Material field.",
                        MessageType.Warning);
                    break;
                }

                Canvas canvas = image.canvas;
                if (canvas != null && (canvas.additionalShaderChannels & AdditionalCanvasShaderChannels.TexCoord1) == 0)
                {
                    EditorGUILayout.HelpBox(
                        "The parent Canvas must enable Additional Shader Channels > TexCoord1.",
                        MessageType.Warning);
                    break;
                }
            }
        }
    }
}
