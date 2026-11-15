using UnityEditor;

namespace Umbreey.Editor
{
    [CustomEditor(typeof(UILang), true)]
    public class UILangEditor : UnityEditor.Editor
    {
        private UILang _lang;

        private void OnEnable()
        {
            _lang = target as UILang;
            if (_lang.library == null)
            {
                _lang.library = _lang.transform.root.GetComponentInChildren<UILangLibrary>();
            }
            if (_lang.library == null)
            {
                Log.E($"{_lang.name} can't find UILangLibrary in parent {_lang.transform.root.name}");
            }
        }
    }
}