using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Umbreey
{
    /// <summary>
    /// UI容器组件，支持泛型数据绑定，避免装箱拆箱
    /// </summary>
    public class UIContainer : UIBehaviour
    {
        #region Serialized Fields
        
        [Tooltip("Item预制体列表")]
        public List<GameObject> itemPrefabList = new List<GameObject>();
        
        #endregion
        
        #region Private Fields
        
        // 对象池管理
        private GameObject _poolRoot;
        private readonly Dictionary<string, Queue<GameObject>> _dictPool = new Dictionary<string, Queue<GameObject>>();
        
        // 预制体管理
        private string _defaultName;
        private Dictionary<string, GameObject> _dictionary;
        
        // 泛型核心对象（类型擦除存储）
        private object _coreObject;
        private Type _dataType;
        
        #endregion
        
        #region Public Properties
        
        /// <summary>
        /// 容器是否已初始化
        /// </summary>
        public bool IsInitialized => _coreObject != null;
        
        /// <summary>
        /// 容器中的元素数量
        /// </summary>
        public int Count => _coreObject != null ? GetCountInternal() : 0;
        
        /// <summary>
        /// 默认预制体名称
        /// </summary>
        public string DefaultName
        {
            get
            {
                if (string.IsNullOrEmpty(_defaultName))
                {
                    if (itemPrefabList.Count > 0)
                        _defaultName = itemPrefabList[0].name;
                }
                return _defaultName;
            }
            set => _defaultName = value;
        }
        
        #endregion
        
        #region Private Properties
        
        /// <summary>
        /// 预制体字典
        /// </summary>
        private Dictionary<string, GameObject> Dictionary
        {
            get
            {
                if (_dictionary == null)
                {
                    _dictionary = new Dictionary<string, GameObject>();
                    foreach (var prefab in itemPrefabList)
                    {
                        _dictionary[prefab.name] = prefab;
                        prefab.SetActive(false);
                    }
                }
                return _dictionary;
            }
        }

        /// <summary>
        /// 对象池根节点
        /// </summary>
        private GameObject PoolRoot
        {
            get
            {
                if (!_poolRoot)
                {
                    _poolRoot = new GameObject("pool_root");
                    _poolRoot.SetParent(gameObject);
                }
                return _poolRoot;
            }
        }
        
        #endregion
        
        #region Unity Lifecycle
        
        protected override void Awake()
        {
            base.Awake();
            // 触发延迟初始化
            var tmpDic = Dictionary;
            var tmpDefName = DefaultName;
            var tmpPoolRoot = PoolRoot;
        }
        
        #endregion
        
        #region Nested Types
        
        /// <summary>
        /// 容器核心接口
        /// </summary>
        private interface IContainerCore
        {
            int Count { get; }
            void Clear();
            void RemoveAt(int index);
            void RemoveIf(Func<GameObject, bool> checkCondition);
            GameObject GetAt(int index);
            void RefreshAt(int index);
            void RefreshAll();
        }
        
        /// <summary>
        /// Item数据结构
        /// </summary>
        private struct ItemData<T>
        {
            public GameObject ItemGo;
            public string ItemName;
            public T Data;
        }
        
        /// <summary>
        /// 泛型核心类
        /// </summary>
        private class ContainerCore<T> : IContainerCore
        {
            private readonly UIContainer _host;
            private readonly List<ItemData<T>> _itemList = new List<ItemData<T>>();
            private Action<GameObject, T> _updateItem;
            private Action<GameObject, T> _returnItem;
            
            public int Count => _itemList.Count;
            
            public ContainerCore(UIContainer host)
            {
                _host = host;
            }
            
            public void InitContainer(Action<GameObject, T> updateItem)
            {
                Clear();
                _updateItem = updateItem;
            }
            
            public void SetReturnPoolCallback(Action<GameObject, T> returnPool)
            {
                _returnItem = returnPool;
            }
            
            public GameObject AddData(T data)
            {
                return AddData(_host.DefaultName, data);
            }
            
            public GameObject AddData(string itemName, T data)
            {
                var go = _host.GetPool(itemName);
                _itemList.Add(new ItemData<T> { ItemGo = go, ItemName = itemName, Data = data });
                _updateItem?.Invoke(go, data);
                return go;
            }
            
            public void SetData(int index, T data)
            {
                if (index < _itemList.Count)
                {
                    var item = _itemList[index];
                    item.Data = data;
                    _itemList[index] = item;
                    RefreshAt(index);
                }
            }
            
            public GameObject Insert(int index, T data)
            {
                return Insert(_host.DefaultName, index, data);
            }
            
            public GameObject Insert(string itemName, int index, T data)
            {
                var go = _host.GetPool(itemName);
                var item = new ItemData<T> { ItemGo = go, ItemName = itemName, Data = data };
                
                if (index < _itemList.Count)
                {
                    var cur = _itemList[index];
                    go.transform.SetSiblingIndex(cur.ItemGo.transform.GetSiblingIndex());
                    _itemList.Insert(index, item);
                }
                else
                {
                    _itemList.Add(item);
                }
                
                _updateItem?.Invoke(go, data);
                return go;
            }
            
            public int IndexOf(T data)
            {
                for (int i = 0; i < _itemList.Count; i++)
                {
                    if (EqualityComparer<T>.Default.Equals(_itemList[i].Data, data))
                    {
                        return i;
                    }
                }
                return -1;
            }
            
            public void RemoveAt(int index)
            {
                if (index < _itemList.Count)
                {
                    ReturnPool(_itemList[index]);
                    _itemList.RemoveAt(index);
                }
            }
            
            public void Remove(T data)
            {
                for (int i = 0; i < _itemList.Count; i++)
                {
                    if (EqualityComparer<T>.Default.Equals(_itemList[i].Data, data))
                    {
                        RemoveAt(i);
                        return;
                    }
                }
            }
            
            public bool Contains(T data)
            {
                for (int i = 0; i < _itemList.Count; i++)
                {
                    if (EqualityComparer<T>.Default.Equals(_itemList[i].Data, data))
                    {
                        return true;
                    }
                }
                return false;
            }
            
            public void RemoveIf(Func<GameObject, bool> checkCondition)
            {
                for (int i = _itemList.Count - 1; i >= 0; i--)
                {
                    if (checkCondition(_itemList[i].ItemGo))
                    {
                        RemoveAt(i);
                    }
                }
            }
            
            public GameObject GetAt(int index)
            {
                if (index < _itemList.Count)
                {
                    return _itemList[index].ItemGo;
                }
                return null;
            }
            
            public T GetDataAt(int index)
            {
                if (index < _itemList.Count)
                {
                    return _itemList[index].Data;
                }
                return default;
            }
            
            public void RefreshAt(int index)
            {
                if (index < _itemList.Count && _updateItem != null)
                {
                    var item = _itemList[index];
                    _updateItem(item.ItemGo, item.Data);
                }
            }
            
            public void Refresh(T data)
            {
                for (int i = 0; i < _itemList.Count; i++)
                {
                    if (EqualityComparer<T>.Default.Equals(_itemList[i].Data, data))
                    {
                        RefreshAt(i);
                        return;
                    }
                }
            }
            
            public void RefreshAll()
            {
                for (int i = 0; i < _itemList.Count; i++)
                {
                    RefreshAt(i);
                }
            }
            
            public void Clear()
            {
                if (_itemList == null || _itemList.Count < 1)
                {
                    return;
                }
                
                int loopTime = _itemList.Count;
                for (int i = 0; i < loopTime; ++i)
                {
                    ReturnPool(_itemList[i]);
                }
                _itemList.Clear();
            }
            
            private void ReturnPool(ItemData<T> item)
            {
                var go = item.ItemGo;
                if (go)
                {
                    _returnItem?.Invoke(go, item.Data);
                    _host.ReturnPool(go, item.ItemName);
                }
            }
        }
        
        #endregion
        
        #region Initialization
        
        /// <summary>
        /// 初始化容器
        /// </summary>
        /// <typeparam name="T">数据类型</typeparam>
        /// <param name="updateItem">更新Item的回调</param>
        public void InitContainer<T>(Action<GameObject, T> updateItem)
        {
            Clear();
            // 如果已有核心对象，先清理
            if (_coreObject != null)
            {
                ClearInternal();
            }
            GetCore<T>(autoInit: true)?.InitContainer(updateItem);
        }
        
        /// <summary>
        /// 初始化容器（object类型）
        /// </summary>
        /// <param name="updateItem">更新Item的回调</param>
        public void InitContainer(Action<GameObject, object> updateItem)
        {
            Clear();
            if (updateItem == null) return;
            InitContainer<object>(updateItem);
        }
        
        /// <summary>
        /// 设置返回对象池的回调
        /// </summary>
        /// <typeparam name="T">数据类型</typeparam>
        /// <param name="returnPool">返回池时的回调</param>
        public void SetReturnPoolCallback<T>(Action<GameObject, T> returnPool)
        {
            GetCore<T>(autoInit: true)?.SetReturnPoolCallback(returnPool);
        }
        
        #endregion
        
        #region Add Methods
        
        /// <summary>
        /// 添加数据
        /// </summary>
        /// <typeparam name="T">数据类型</typeparam>
        /// <param name="data">数据</param>
        /// <returns>创建的GameObject</returns>
        public GameObject AddData<T>(T data)
        {
            return GetCore<T>(autoInit: true)?.AddData(data);
        }
        
        /// <summary>
        /// 添加数据（object类型）
        /// </summary>
        /// <param name="data">数据</param>
        /// <returns>创建的GameObject</returns>
        public GameObject AddData(object data)
        {
            return AddData<object>(data);
        }
        
        /// <summary>
        /// 添加数据（指定预制体）
        /// </summary>
        /// <typeparam name="T">数据类型</typeparam>
        /// <param name="itemName">预制体名称</param>
        /// <param name="data">数据</param>
        /// <returns>创建的GameObject</returns>
        public GameObject AddData<T>(string itemName, T data)
        {
            return GetCore<T>(autoInit: true)?.AddData(itemName, data);
        }
        
        #endregion
        
        #region Insert Methods
        
        /// <summary>
        /// 插入数据
        /// </summary>
        /// <typeparam name="T">数据类型</typeparam>
        /// <param name="index">插入位置</param>
        /// <param name="data">数据</param>
        /// <returns>创建的GameObject</returns>
        public GameObject Insert<T>(int index, T data)
        {
            return GetCore<T>(autoInit: true)?.Insert(index, data);
        }
        
        /// <summary>
        /// 插入数据（指定预制体）
        /// </summary>
        /// <typeparam name="T">数据类型</typeparam>
        /// <param name="itemName">预制体名称</param>
        /// <param name="index">插入位置</param>
        /// <param name="data">数据</param>
        /// <returns>创建的GameObject</returns>
        public GameObject Insert<T>(string itemName, int index, T data)
        {
            return GetCore<T>(autoInit: true)?.Insert(itemName, index, data);
        }
        
        #endregion

        #region IndexOf

        public int IndexOf<T>(T data)
        {
            var core = GetCore<T>(autoInit: false);
            if (core == null) return -1;

            return core.IndexOf(data);
        }

        #endregion
        
        #region Set/Update Methods
        
        /// <summary>
        /// 设置数据
        /// </summary>
        /// <typeparam name="T">数据类型</typeparam>
        /// <param name="index">索引</param>
        /// <param name="data">新数据</param>
        public void SetData<T>(int index, T data)
        {
            GetCore<T>(autoInit: true)?.SetData(index, data);
        }
        
        #endregion
        
        #region Remove Methods
        
        /// <summary>
        /// 移除元素
        /// </summary>
        /// <param name="index">索引</param>
        public void RemoveAt(int index)
        {
            if (_coreObject == null) return;
            ((IContainerCore)_coreObject).RemoveAt(index);
        }
        
        /// <summary>
        /// 移除元素
        /// </summary>
        /// <typeparam name="T">数据类型</typeparam>
        /// <param name="data">数据</param>
        public void Remove<T>(T data)
        {
            GetCore<T>(autoInit: false)?.Remove(data);
        }
        
        /// <summary>
        /// 根据条件移除元素
        /// </summary>
        /// <param name="checkCondition">检查条件</param>
        public void RemoveIf(Func<GameObject, bool> checkCondition)
        {
            if (_coreObject == null) return;
            ((IContainerCore)_coreObject).RemoveIf(checkCondition);
        }
        
        /// <summary>
        /// 清空容器
        /// </summary>
        public void Clear()
        {
            ClearInternal();
        }
        
        #endregion
        
        #region Query Methods
        
        /// <summary>
        /// 检查是否包含
        /// </summary>
        /// <typeparam name="T">数据类型</typeparam>
        /// <param name="data">数据</param>
        /// <returns>是否包含</returns>
        public bool Contains<T>(T data)
        {
            return GetCore<T>(autoInit: false)?.Contains(data) ?? false;
        }
        
        /// <summary>
        /// 获取GameObject
        /// </summary>
        /// <param name="index">索引</param>
        /// <returns>GameObject</returns>
        public GameObject GetAt(int index)
        {
            if (_coreObject == null) return null;
            return ((IContainerCore)_coreObject).GetAt(index);
        }
        
        /// <summary>
        /// 获取数据
        /// </summary>
        /// <typeparam name="T">数据类型</typeparam>
        /// <param name="index">索引</param>
        /// <returns>数据</returns>
        public T GetDataAt<T>(int index)
        {
            var core = GetCore<T>(autoInit: true);
            return core != null ? core.GetDataAt(index) : default;
        }
        
        #endregion
        
        #region Refresh Methods
        
        /// <summary>
        /// 刷新元素
        /// </summary>
        /// <param name="index">索引</param>
        public void RefreshAt(int index)
        {
            if (_coreObject == null) return;
            ((IContainerCore)_coreObject).RefreshAt(index);
        }
        
        /// <summary>
        /// 刷新元素
        /// </summary>
        /// <typeparam name="T">数据类型</typeparam>
        /// <param name="data">数据</param>
        public void Refresh<T>(T data)
        {
            GetCore<T>(autoInit: false)?.Refresh(data);
        }
        
        /// <summary>
        /// 刷新所有元素
        /// </summary>
        public void RefreshAll()
        {
            if (_coreObject == null) return;
            ((IContainerCore)_coreObject).RefreshAll();
        }
        
        #endregion
        
        #region Private Methods
        
        /// <summary>
        /// 获取泛型核心
        /// </summary>
        private ContainerCore<T> GetCore<T>(bool autoInit = true)
        {
            // 未初始化，自动初始化
            if (_coreObject == null)
            {
                if (autoInit)
                {
                    _dataType = typeof(T);
                    _coreObject = new ContainerCore<T>(this);
                    return (ContainerCore<T>)_coreObject;
                }
                else
                {
                    Log.E($"_coreObject 未初始化，无法获取类型 {typeof(T).Name} 的核心对象");
                    return null;
                }
            }
            
            // 类型不匹配，需要重新初始化
            if (_dataType != typeof(T))
            {
                if (autoInit)
                {
                    // 清理旧数据
                    ClearInternal();
                    // 重新初始化为新类型
                    _dataType = typeof(T);
                    _coreObject = new ContainerCore<T>(this);
                    return (ContainerCore<T>)_coreObject;
                }
                else
                {
                    throw new InvalidOperationException(
                        $"类型不匹配: 容器已初始化为 {_dataType.Name}，但尝试使用 {typeof(T).Name}");
                }
            }
            
            return (ContainerCore<T>)_coreObject;
        }
        
        /// <summary>
        /// 获取元素数量
        /// </summary>
        private int GetCountInternal()
        {
            if (_coreObject == null) return 0;
            return ((IContainerCore)_coreObject).Count;
        }
        
        /// <summary>
        /// 内部清理方法
        /// </summary>
        private void ClearInternal()
        {
            if (_coreObject == null) return;
            ((IContainerCore)_coreObject).Clear();
        }
        
        #endregion
        
        #region Internal Methods (Pool Management)
        
        /// <summary>
        /// 从对象池获取
        /// </summary>
        internal GameObject GetPool(string itemName)
        {
            GameObject go = null;
            if (_dictPool.TryGetValue(itemName, out var list) && list.Count > 0)
                go = list.Dequeue();
            if (go == null)
                go = GameObject.Instantiate(Dictionary[itemName], transform, false);
            go.SetParent(gameObject, false);
            go.SetActive(true);
            return go;
        }

        /// <summary>
        /// 返回对象池
        /// </summary>
        internal void ReturnPool(GameObject go, string itemName)
        {
            if (go)
            {
                go.SetParent(PoolRoot, false);
                go.SetActive(false);
                if (!_dictPool.TryGetValue(itemName, out var list))
                {
                    _dictPool[itemName] = new Queue<GameObject>();
                    list = _dictPool[itemName];
                }
                list.Enqueue(go);
            }
        }
        
        #endregion
    }
}
