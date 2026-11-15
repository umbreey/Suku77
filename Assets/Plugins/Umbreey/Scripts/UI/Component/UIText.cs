using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Umbreey
{
    public class UIText : Text
    {
        [SerializeField]
        string m_FontName;
        [SerializeField]
        string m_MaterialName;
        
        [SerializeField] private LangLibraryData m_lang = new LangLibraryData();

        private bool m_IsFontLoading = false;
        private bool m_IsMaterialLoading = false;
        private Font m_CacheFont;
        private Material m_CacheMaterial;
        public System.Action OnLoaded;
        
        public string FontName
        {
            get
            {
                return m_FontName;
            }
            set
            {
                if (m_FontName != value)
                {
                    Assets.ReleaseAsset(m_FontName);
                    m_FontName = value;
                    SetFontName();
                }
            }
        }
        public string MaterialName
        {
            get { return m_MaterialName; }
            set
            {
                if (m_MaterialName != value)
                {
                    Assets.ReleaseAsset(m_MaterialName);
                    m_MaterialName = value;
                    SetMaterial();
                }
            }
        }
        
        public LangLibraryData LangLibrary => m_lang;

        protected override void Awake()
        {
            base.Awake();
            if (!string.IsNullOrEmpty(m_lang.key))
            {
                this.text = m_lang.library.GetText(m_lang.key);
            }
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            TryLoadText();
        }
        
        protected override void OnDestroy()
        {
            if (!m_IsFontLoading)
            {
                Assets.ReleaseAsset(m_FontName);
            }
            if (!string.IsNullOrEmpty(m_MaterialName) && !m_IsMaterialLoading)
            {
                Assets.ReleaseAsset(m_MaterialName);
            }
            base.OnDestroy();
        } 

        void TryLoadText()
        {
            if(Application.isPlaying)
            {
                if (!string.IsNullOrEmpty(m_FontName) && (!m_IsFontLoading || this.font == null))
                {
                    SetFontName();
                }
                if ((!m_IsMaterialLoading && !string.IsNullOrEmpty(m_MaterialName)))
                {
                    SetMaterial();
                }
            }
        }
               
        void SetFontName()
        {
            m_IsFontLoading = true;
            Assets.LoadAssetAsync<Font>(m_FontName, (f) =>
            {
                if (this)
                {
                    m_IsFontLoading = false;
                    m_CacheFont = f;
                    TryRefresh();
                }
                else
                {
                    Assets.ReleaseAsset(m_FontName);
                }
            });
        }

        void SetMaterial()
        {
            m_IsMaterialLoading= true;
            Assets.LoadAssetAsync<Material>(m_MaterialName, (m) =>
            {
                if (this)
                {
                    m_IsMaterialLoading = false;
                    m_CacheMaterial = m;
                    TryRefresh();
                }
                else
                {
                    Assets.ReleaseAsset(m_MaterialName);
                }
            });
        }

        void TryRefresh()
        {
            if (m_CacheFont != null)
            {
                this.font = m_CacheFont;
                if(m_CacheMaterial != null)
                    this.material = m_CacheMaterial;
                OnLoaded?.Invoke();
            }
        }
        protected override void OnPopulateMesh(VertexHelper toFill)
        {
            base.OnPopulateMesh(toFill);
            return;
            Umbreey.Inst.StartCoroutine(MClearUpExplainMode(this,text));
        }
        
        private StringBuilder MExplainText = null;
        
        private IList<UILineInfo> MExpalinTextLine;
        
        private IEnumerator MClearUpExplainMode(Text _component, string _text)
        {
            // 设置文本并等待缓存更新
            _component.text = _text;
            yield return new WaitForSeconds(0.001f);

            // 获取当前文本生成器的行信息
            MExpalinTextLine = _component.cachedTextGenerator.lines;
            MExplainText = new StringBuilder(text);
            for (int i = 1; i < MExpalinTextLine.Count; i++)
            {
                int startCharIdx = MExpalinTextLine[i].startCharIdx;
                if (startCharIdx >= MExplainText.Length) continue;

                // 检查行首字符是否为标点符号
                bool isPunctuation = IsPunctuation(MExplainText[startCharIdx]);
                if (isPunctuation)
                {
                    // 找到前一个非标点符号的位置作为插入点
                    int insertIndex = startCharIdx - 1;
                    while (insertIndex > 0 && IsPunctuation(MExplainText[insertIndex]))
                    {
                        insertIndex--;
                        if(insertIndex >= 0 && IsWhiteSpace(MExplainText[insertIndex]))
                            break;
                    }
                    
                    if(insertIndex >= 0 && IsWhiteSpace(MExplainText[insertIndex]))
                        break;

                    // 在插入点前添加换行符
                    MExplainText.Insert(insertIndex, "\n");
                    yield return new WaitForSeconds(0.001f); // 等待缓存更新
                }
            }
            _component.text = MExplainText.ToString();
        }

        private bool IsPunctuation(char character)
        {
            return Char.IsPunctuation(character);
        }

        private bool IsWhiteSpace(char character)
        {
            return character == '\n';
        }
    }
}