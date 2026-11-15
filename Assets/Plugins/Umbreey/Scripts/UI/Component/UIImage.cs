using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.UI;

namespace Umbreey
{
    public class UIImage : Image
    {
        [SerializeField, Range(1f, 8f)]
        [Tooltip("HDR brightness multiplier. Requires the Artist/UI/HDR Intensity material and Canvas TexCoord1 channel.")]
        float m_HDRIntensity = 1f;

        [SerializeField]
        string m_SpriteName;
        [SerializeField]
        string m_MaterialName;
  
        private Sprite m_CacheSprite;
        private Material m_CacheMaterial;
        [NonSerialized] public bool isNativeSize;
        
        public System.Action OnLoaded;
        public System.Action OnMatLoaded;
        
        public bool IsLoaded { get; private set; }
        public bool IsMatLoaded { get; private set; }

        /// <summary>
        /// Brightness multiplier passed to the HDR UI shader without using the clamped Color32 vertex color.
        /// </summary>
        public float HDRIntensity
        {
            get => m_HDRIntensity;
            set
            {
                float newValue = Mathf.Clamp(value, 1f, 8f);
                if (Mathf.Approximately(m_HDRIntensity, newValue)) return;
                m_HDRIntensity = newValue;
                SetVerticesDirty();
            }
        }

        private bool _isLoading = false;
        private bool _isMatLoading = false;
        
        public string SpriteName
        {
            get { return m_SpriteName; }
            set
            {
                if (m_SpriteName != value)
                {
                    Assets.ReleaseAsset(m_SpriteName);
                    m_SpriteName = value;
                    SetSprite();
                }
                else
                {
                    if (sprite != null && sprite.name == GetNameWithoutExtension(m_SpriteName))
                    {
                        OnLoaded?.Invoke();
                    }
                    else if(!_isLoading)
                    {
                        m_SpriteName = value;
                        SetSprite();
                    }
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
                else
                {
                    if (material != null && material.name == GetNameWithoutExtension(m_MaterialName))
                    {
                        OnMatLoaded?.Invoke();
                    }
                    else if(!_isMatLoading)
                    {
                        m_MaterialName = value;
                        SetMaterial();
                    }
                }
            }
        }
        
        protected override void OnEnable()
        {
            base.OnEnable();
            TryLoadSprite();
        }

        protected override void OnDisable()
        {
            TryReleaseSprite();
            base.OnDisable();
        }

        protected override void OnPopulateMesh(VertexHelper toFill)
        {
            base.OnPopulateMesh(toFill);

            UIVertex vertex = default;
            for (int i = 0; i < toFill.currentVertCount; i++)
            {
                toFill.PopulateUIVertex(ref vertex, i);
                vertex.uv1.x = m_HDRIntensity;
                toFill.SetUIVertex(vertex, i);
            }
        }
        
        void TryLoadSprite()
        {
            if(Application.isPlaying)
            {
                if (!string.IsNullOrEmpty(m_SpriteName) && ((this.sprite != null && GetNameWithoutExtension(m_SpriteName) != this.sprite.name ) || ( this.sprite == null)))
                {
                    SetSprite();
                }
                if (!string.IsNullOrEmpty(m_MaterialName) && ((GetNameWithoutExtension(m_MaterialName) != this.material.name )))
                {
                    SetMaterial();
                }
            }
        }
        
        void TryReleaseSprite()
        {
            if (!string.IsNullOrEmpty(m_SpriteName) && (this.sprite != null && GetNameWithoutExtension(m_SpriteName) == this.sprite.name))
            {
                Assets.ReleaseAsset(m_SpriteName);
                this.sprite = UGUIHelper.EmptySprite;
            }
            
            if (!string.IsNullOrEmpty(m_MaterialName) && ((GetNameWithoutExtension(m_MaterialName) == this.material.name )))
            {
                Assets.ReleaseAsset(m_MaterialName);
                this.material = null;
            }
        }
        
        void SetSprite()
        {
            if (!this.IsActive()) return;
            if (!string.IsNullOrEmpty(m_SpriteName))
            {
                var loadSpriteName = m_SpriteName;
                // 未 Init 资源库 / 图不在 AB 表时：保留 Prefab 当前 Sprite（不要被 EmptySprite 卡住）
                if (!Assets.HasAsset(loadSpriteName))
                {
#if UNITY_EDITOR
                    // 编辑器兜底：按文件名直接从工程加载
                    var fileName = System.IO.Path.GetFileName(loadSpriteName);
                    var guids = UnityEditor.AssetDatabase.FindAssets($"{System.IO.Path.GetFileNameWithoutExtension(fileName)} t:Sprite");
                    foreach (var guid in guids)
                    {
                        var path = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
                        if (System.IO.Path.GetFileName(path) != fileName)
                            continue;
                        var sp = UnityEditor.AssetDatabase.LoadAssetAtPath<Sprite>(path);
                        if (sp != null)
                        {
                            this.sprite = sp;
                            this.IsLoaded = true;
                            this._isLoading = false;
                            return;
                        }
                    }
#endif
                    if (this.sprite != null && this.sprite != UGUIHelper.EmptySprite)
                    {
                        this.IsLoaded = true;
                        this._isLoading = false;
                        return;
                    }
                    Debug.LogError($"Sprite {loadSpriteName} is not found in {this.gameObject.GetScenePath()}", this.gameObject);
                    return;
                }
                this.IsLoaded = false;
                this._isLoading = true;
                Assets.LoadAssetAsync<Sprite>(loadSpriteName, (s) =>
                {
                    if (this && loadSpriteName == m_SpriteName)
                    {
                        m_CacheSprite = s;
                        TryRefresh();
                    }
                    else
                    {
                        Assets.ReleaseAsset(loadSpriteName);
                    }
                });
            }
            else
            {
                TryRefresh();
            }
        }

        void SetMaterial()
        {
            if (!this.IsActive()) return;
            if (!string.IsNullOrEmpty(m_MaterialName))
            {
                IsMatLoaded = false;
                this._isMatLoading = true;
                var loadMaterialName = m_MaterialName;
                Assets.LoadAssetAsync<Material>(loadMaterialName, (m) =>
                {
                    if (this && loadMaterialName == m_MaterialName)
                    {
                        m_CacheMaterial = m;
                        TryRefresh();
                    }
                    else
                    {
                        Assets.ReleaseAsset(loadMaterialName);
                    }
                });
            }
            else
            {
                TryRefresh(); 
            }
        }

        void TryRefresh()
        {
            if (m_CacheSprite != null && !string.IsNullOrEmpty(m_SpriteName))
            {
                this.sprite = m_CacheSprite;
                if(isNativeSize) this.SetNativeSize();
                this.IsLoaded = true;
                OnLoaded?.Invoke();
            }

            if (m_CacheMaterial != null && !string.IsNullOrEmpty(m_MaterialName))
            {
                this.material = m_CacheMaterial;
                this.IsMatLoaded = true;
                OnMatLoaded?.Invoke();
            }

            if (string.IsNullOrEmpty(m_SpriteName))
            {
                // 无资源名时保留 Prefab 上直接引用的 Sprite（未走 AB 的编辑器预览场景）
                this.IsLoaded = true;
                OnLoaded?.Invoke();
            }
            
            if (string.IsNullOrEmpty(m_MaterialName))
            {
                this.IsMatLoaded = true;
                OnMatLoaded?.Invoke();
            }
            
            _isLoading = false;
            _isMatLoading = false;
        }
        
        string GetNameWithoutExtension(string name)
        {
            return Path.GetFileNameWithoutExtension(name);
        }
    }
}
