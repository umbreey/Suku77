using System;
using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Umbreey;

namespace Umbreey.Editor
{
    [CustomEditor(typeof(UILangLibrary), true)]
    public class UILangLibraryEditor : UnityEditor.Editor
    {
        UILangLibrary _langLibrary;
        private void OnEnable()
        {
            _langLibrary = target as UILangLibrary;
            _langLibrary.RootName=_langLibrary.name;
        }
    }    
}

