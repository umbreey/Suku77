using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

namespace Umbreey
{
    public partial class UIManager
    {
        private class Entity
        {
            public enum Phase
            {
                OPENING,
                OPENED,
                CLOSING,
                CLOSED
            }
            public string name;
            public UIView view;
            public bool created;
            public bool focused;
            public float closeTime = 0f;
            public Phase phase = Phase.CLOSED;
        }

        private class RegTask
        {
            public Action<string,Action> onBack;
            public string openName;
        }
        
        public class EntityTask
        {
            public string name;
            public enum Type
            {
                NORMAL,
                BACK_OPEN,
                BOTH_OPEN_AND_CLOSE,
            }
            public Type type = Type.NORMAL;
        }
        private static Dictionary<string, Type> s_Types = new Dictionary<string, Type>();
        private static List<Entity> s_Entities = new List<Entity>();
        private static List<EntityTask> s_EntitieTasks = new List<EntityTask>();
        private static Dictionary<string, RegTask> s_RegTasks = new Dictionary<string, RegTask>();
        private static Action<string,bool> OnVisibleChange;

        // ---- canvas-toggle 白名单（UIGen 期静态生成，运行期仅一次 HashSet 查询）----
        private static HashSet<string> s_CanvasToggleNames = new HashSet<string>();
        // 当前以 canvas 方式隐藏的 UI：value 记录被我们关闭的 Canvas / GraphicRaycaster（含嵌套子 Canvas），
        // show 时只恢复这些，避免把游戏逻辑原本就关闭的嵌套 Canvas 误复活
        private static Dictionary<string, CanvasToggleState> s_CanvasHidden = new Dictionary<string, CanvasToggleState>();

        private struct CanvasToggleState
        {
            public List<Canvas> canvases;
            public List<GraphicRaycaster> raycasters;
            public List<IUIVisibleNotify> comps; // canvas 隐藏期被禁用的补偿组件，show 时只恢复这些
        }
        
        static UIManager()
        {
            MakeUIHandler();
            Umbreey.OnReflectionAssemblySet += MakeUIHandler;
            
            TaskManager.Update(Update);
        }

        static void Update()
        {
            for (int i = 0; i < s_Entities.Count; i++)
            {
                var entity = s_Entities[i];
                if (entity != null)
                {
                    bool needDestroy = (entity.phase == Entity.Phase.CLOSED) &&
                        (entity.closeTime != 0f && Time.unscaledTime - entity.closeTime >= Umbreey.Inst.UIDestroyTime);
                    if (needDestroy)
                    {
                        DestroyEntity(entity);
                        i--;
                    }
                    else if (entity.phase != Entity.Phase.CLOSED)
                    {
                        UIView.InternalUpdate(entity.view);
                    }
                }
            }
        }

        private static Entity CreateEntity(Type type)
        {
            UIView view;
            Entity entity;
            if (!TryGet(type, out entity))
            {
                view = (UIView)Activator.CreateInstance(type);
                entity = new Entity() { name = type.Name,view = view, closeTime=0f, phase = Entity.Phase.CLOSED };
                s_Entities.Add(entity);
            }
           
            return entity;
        }
        
        private static void DestroyEntity(Entity entity)
        {
            var view = entity.view;
            s_Entities.Remove(entity);
            // 必须同步清掉 canvas-toggle 隐藏状态：字典里存的是具体 Canvas/Raycaster 引用，
            s_CanvasHidden.Remove(entity.name);
            if (view.gameObject != null) {
                GameObject.Destroy(entity.view.gameObject);
                UIView.InternalDestroyed(view);
            }
        }

        public static void Open(string name, Action callback = null)
        {
            InternalOpen(name, EntityTask.Type.NORMAL, callback);
        }

        public static void OpenOnlyBack(string name, Action callback = null)
        {
            InternalOpen(name, EntityTask.Type.BACK_OPEN, callback);
        }

        public static void OpenAndPush(string name, Action callback = null)
        {
            InternalOpen(name, EntityTask.Type.BOTH_OPEN_AND_CLOSE, callback);
        }

        static void InternalOpen(string name, EntityTask.Type taskType, Action callback)
        {
            Type type = s_Types[name];
            Entity entity = CreateEntity(type);
            if (entity.phase == Entity.Phase.CLOSED || entity.phase == Entity.Phase.CLOSING)
            {
                entity.phase = Entity.Phase.OPENING;
                entity.closeTime = 0f;
                UIView view = entity.view;
                LoadPrefab($"{type.Name}.prefab", view.gameObject,(prefab) =>
                {
                    if (entity.phase == Entity.Phase.OPENING)
                    {
                        entity.phase = Entity.Phase.OPENED;
                        s_EntitieTasks.Add(new EntityTask() { name = name, type = taskType });
                        if (taskType == EntityTask.Type.BOTH_OPEN_AND_CLOSE)
                        {
                            for (int i = s_EntitieTasks.Count-2; i >=0; i--)
                            {
                                var entityTask = s_EntitieTasks[i];
                                bool triggerClose = (entityTask.type == EntityTask.Type.BACK_OPEN || entityTask.type == EntityTask.Type.BOTH_OPEN_AND_CLOSE);
                                if (triggerClose)
                                {
                                    Entity closeEntity;
                                    if (TryGet(s_Types[entityTask.name], out closeEntity))
                                    {
                                        InternalOnlyClose(closeEntity, false);
                                    }
                                    break;
                                }
                            }
                        }
                        InternalOnlyOpen(entity, prefab);
                        callback?.Invoke();
                    }else
                    {
                        GameObject.Destroy(prefab);
                    }
                });
            }
        }

        static void InternalOnlyOpen(Entity entity,GameObject prefab)
        {
            //Log.D($"[UIManager] => InternalOnlyOpen {entity.name}");
            var view = entity.view;
            if (prefab != null)
            {
                if (!entity.created)
                {
                    entity.created = true;
                    UIView.InternalCreated(view, prefab);
                }
                view.transform.SetParent(Umbreey.Inst.UIRoot.transform, false);
                if (view.transform.TryGetComponent<Canvas>(out var canvas))
                { 
                    canvas.renderMode = Umbreey.Inst.UIRenderMode;
                    if (Umbreey.Inst.UIRenderMode == RenderMode.ScreenSpaceCamera)
                    {
                        canvas.worldCamera = Umbreey.Inst.UICamera;
                    }
                    UIView.InternalOverrideCanvas(view, canvas);
                }
            }
            SetViewVisible(entity, true);
            UIView.InternalOpened(view);
            OnVisibleChange?.Invoke(entity.name,true);
            
            if (entity.phase != Entity.Phase.CLOSED && entity.phase != Entity.Phase.CLOSING)
            {
                SetViewAnimOpen(view);
            }
            
            if (!entity.focused)
            {
                entity.focused = true;
                UIView.InternalFocusChanged(view, true);
            }
        }

        public static void Close(string name)
        {
            InternalClose(name, false);
        }

        private static void InternalClose(string name, bool destroy)
        {
            Entity entity;
            if (TryGet(s_Types[name], out entity))
            {
                if (entity.phase == Entity.Phase.OPENED || entity.phase == Entity.Phase.OPENING)
                {
                    entity.phase = Entity.Phase.CLOSING;
                    SetViewAnimClose(entity.view, () =>
                    {
                        if (entity.phase != Entity.Phase.CLOSING) return;
                        CloseTriggerOpen(name, (openEntity,openPrefab) =>
                        {
                            bool hasOpen = (openEntity != null && openPrefab != null);
                            InternalOnlyClose(entity, destroy);
                            if (hasOpen)
                            {
                                RegTask regTask = null;
                                bool hasReg = (s_RegTasks.TryGetValue(openEntity.name, out regTask));
                                if (hasReg && regTask.onBack != null)
                                {
                                    UILock.Lock();
                                    regTask.openName = openEntity.name;
                                    regTask.onBack(name,() =>
                                    {
                                        UILock.UnLock();
                                        openEntity.phase = Entity.Phase.OPENED;
                                        hasOpen = (openEntity != null && openPrefab != null);
                                        if(hasOpen)
                                            InternalOnlyOpen(openEntity, openPrefab);
                                        else
                                        {
                                            Type type = s_Types[regTask.openName];
                                            Entity entity = CreateEntity(type);
                                            LoadPrefab($"{type.Name}.prefab", entity.view.gameObject, (go) =>
                                            {
                                                InternalOnlyOpen(entity, go);
                                            }); 
                                        }
                                    });
                                }
                                else
                                {
                                    openEntity.phase = Entity.Phase.OPENED;
                                    InternalOnlyOpen(openEntity, openPrefab);
                                }
                            }
                        });
                    });
                }
            }
        }

        private static void InternalOnlyClose(Entity entity, bool destroy)
        {
            var view = entity.view;

            if (view.gameObject != null)
            {
                if (entity.focused)
                {
                    entity.focused = false;
                    UIView.InternalFocusChanged(view, false);
                }
                UIView.InternalClosed(view);
            }

            entity.phase = Entity.Phase.CLOSED;
            if (destroy)
            {
                DestroyEntity(entity);
            }
            else if (view.gameObject != null)
            {
                entity.closeTime = Time.unscaledTime;
                SetViewVisible(entity, false);
            }
            OnVisibleChange?.Invoke(entity.name,false);
        }

        static void CloseTriggerOpen(string closeName,Action<Entity,GameObject> trigger)
        {
            bool closeTriggerOpen = false;
            for (int i = s_EntitieTasks.Count-1; i >= 0; i--)
            {
                var entityTask = s_EntitieTasks[i];
                if (closeTriggerOpen)
                {
                    bool triggerOpen = (entityTask.type == EntityTask.Type.BACK_OPEN || entityTask.type == EntityTask.Type.BOTH_OPEN_AND_CLOSE);
                    if (triggerOpen)
                    {
                        Type type = s_Types[entityTask.name];
                        Entity entity = CreateEntity(type);
                        LoadPrefab($"{type.Name}.prefab", entity.view.gameObject, (go) =>
                        {
                            trigger(entity, go);
                        });
                        return;
                    }
                }
                if(entityTask.name == closeName)
                {
                    if ((entityTask.type == EntityTask.Type.BOTH_OPEN_AND_CLOSE))
                    {
                        closeTriggerOpen = true;
                    }
                    s_EntitieTasks.RemoveAt(i);
                }
            }
            trigger?.Invoke(null,null);
        }

        [System.Obsolete("Use Close instead")]
        public static void CloseAndDestroy(string name)
        {
            InternalClose(name, true);
        }

        public static void CloseAndDestroyAll(params string[] excludes)
        {
            HashSet<string> hash = new HashSet<string>(excludes);
            for (int i = s_EntitieTasks.Count - 1; i >= 0; i--)
            {
                var entityTask = s_EntitieTasks[i];
                if (entityTask !=null && !hash.Contains(entityTask.name))
                {
                    Entity entity;
                    if (TryGet(s_Types[entityTask.name], out entity))
                    {
                        InternalOnlyClose(entity, true);
                    }
                    if(i < s_EntitieTasks.Count)
                        s_EntitieTasks.RemoveAt(i);
                }
            }

            for (int i = s_Entities.Count - 1; i >= 0; i--)
            {
                var entity = s_Entities[i];
                if (entity != null)
                {
                    bool needDestroy = entity.phase == Entity.Phase.CLOSED || 
                                       (!hash.Contains(entity.name) && entity.phase == Entity.Phase.OPENING);
                    if (needDestroy)
                    {
                        InternalOnlyClose(entity, true);
                    }
                }
            }
        }

        public static void CloseAllOpening()
        {
            for (int i = s_Entities.Count - 1; i >= 0; i--)
            {
                var entity = s_Entities[i];
                if (entity is { phase: Entity.Phase.OPENING })
                {
                    InternalOnlyClose(entity, false);
                }
            }
        }

        public static bool Exists(string name)
        {
            if (TryGet(s_Types[name], out var entity))
            {
                return entity.phase == Entity.Phase.OPENING || entity.phase == Entity.Phase.OPENED;
            }
            return false;
        }

        public static bool ExistsOpening(out string uiName)
        {
            uiName = "";
            for (int i = s_Entities.Count - 1; i >= 0; i--)
            {
                var entity = s_Entities[i];
                if (entity is { phase: Entity.Phase.OPENING })
                {
                    uiName = entity.name;
                    return true;
                }
            }
            return false;
        }
        
        public static List<EntityTask> GetTaskStack()
        {
            return s_EntitieTasks;
        }
        
        public static List<EntityTask> GetTaskStackCopy()
        {
            var copy = new List<EntityTask>(s_EntitieTasks.Count);
            foreach (EntityTask entityTask in s_EntitieTasks)
            {
                var newTask = new UIManager.EntityTask
                {
                    name = entityTask.name,
                    type = entityTask.type
                };
                copy.Add(newTask);
            }
            return copy;
        }
        public static void RegCloseBack(string name,Action<string,Action> onBack)
        {
            if (!s_RegTasks.TryGetValue(name, out var task))
            {
                task = new RegTask();
                s_RegTasks[name] = task;
            }
            task.onBack = onBack;
        }
        public static void UnRegCloseBack(string name)
        {
            s_RegTasks.Remove(name);
        }

        public static void RegVisibleChange(Action<string,bool> onChange)
        {
            OnVisibleChange-= onChange;
            OnVisibleChange+= onChange;
        }
        
        public static void UnRegVisibleChange(Action<string,bool> onChange)
        {
            OnVisibleChange -=onChange;
        }

        // ---- canvas-toggle 白名单（由 UICanvasToggleDef.Register 注入；未注册时为空 → 全部走 SetActive）----
        public static void RegCanvasToggleWhitelist(IReadOnlyList<string> names)
            => s_CanvasToggleNames = names != null ? new HashSet<string>(names) : new HashSet<string>();

        private static bool IsCanvasToggle(string name) => s_CanvasToggleNames.Contains(name);
        
        public static void GetTaskDebug(System.Text.StringBuilder sb)
        {
            sb.Clear();
            for (int i = 0; i < s_EntitieTasks.Count; i++)
            {
                if (i != 0)
                {
                    sb.Append(",");
                }
                var task = s_EntitieTasks[i];
                sb.Append(task.type == EntityTask.Type.NORMAL ? $"<color=white>{task.name}</color>" : $"<color=red>{task.name}</color>");
            }
        }

        private static bool TryGet(Type type, out Entity entity)
        {
            for (int i = s_Entities.Count - 1; i >= 0; i--) {
                entity = s_Entities[i];
                if (entity.view.GetType() == type) {
                       return true;
                }
            }
            entity = null;
            return false;
        }
        
        private static void SetViewVisible(Entity entity, bool visible)
        {
            if (entity == null || entity.view == null) return;
            UIView view = entity.view;
            GameObject go = view.gameObject;
            if (!go) return;
            bool canvasMode = IsCanvasToggle(entity.name);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            VisiblePerfBegin(entity.name, visible, go.transform);
#endif
            if (visible)
            {
                if (canvasMode && s_CanvasHidden.ContainsKey(entity.name))
                {
                    // 之前以 canvas 方式隐藏，重新显示时恢复 Canvas 并重新启用被禁用的补偿组件
                    SetCanvasEnabled(entity, true);
                    RestoreCanvasComponents(entity.name);
                    s_CanvasHidden.Remove(entity.name);
                }
                else
                {
                    go.SetActive(true);
                }
            }
            else
            {
                if (canvasMode)
                {
                    // canvas-toggle：关闭时只关 Canvas/GraphicRaycaster（含嵌套子 Canvas），
                    // 并禁用补偿组件（由组件 OnDisable 接管清理），规避整树 Enable/Disable 开销
                    HideCanvasComponents(view, entity.name);
                    SetCanvasEnabled(entity, false);
                }
                else
                {
                    go.SetActive(false);
                }
            }
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            VisiblePerfEnd(); // 结束 A（canvas-toggle）测量
            if (s_PerfAB) VisiblePerfRunB(entity, visible, go); // A/B 对比：额外跑 SetActive 路径探针
#endif
        }

        private static void SetCanvasEnabled(Entity entity, bool enabled)
        {
            var go = entity.view.gameObject;
            if (!enabled)
            {
                // 隐藏：禁用整棵子树内所有 Canvas / GraphicRaycaster（含嵌套子 Canvas）。
                // 子级 Canvas 是独立渲染单元，只关父 Canvas 无法隐藏它——这正是本方案之前漏掉的。
                // 保留已有的 comps（由 HideCanvasComponents 预先填入的补偿组件记录），避免被覆盖。
                CanvasToggleState state;
                if (s_CanvasHidden.TryGetValue(entity.name, out var existing))
                {
                    state = existing;
                    state.canvases.Clear();
                    state.raycasters.Clear();
                    if (state.comps == null) state.comps = new List<IUIVisibleNotify>();
                }
                else
                {
                    state = new CanvasToggleState
                    {
                        canvases = new List<Canvas>(),
                        raycasters = new List<GraphicRaycaster>(),
                        comps = new List<IUIVisibleNotify>(),
                    };
                }
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                long tCanvas = VisiblePerfNow();
#endif
                var canvases = go.GetComponentsInChildren<Canvas>(true);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                VisiblePerfAccCanvas(tCanvas);
                long tApplyCanvas = VisiblePerfNow();
#endif
                for (int i = 0; i < canvases.Length; i++)
                {
                    if (canvases[i].enabled) { canvases[i].enabled = false; state.canvases.Add(canvases[i]); }
                }
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                VisiblePerfAccApplyCanvas(tApplyCanvas);
                long tRaycast = VisiblePerfNow();
#endif
                var raycasters = go.GetComponentsInChildren<GraphicRaycaster>(true);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                VisiblePerfAccRaycast(tRaycast);
                long tApplyRaycast = VisiblePerfNow();
#endif
                for (int i = 0; i < raycasters.Length; i++)
                {
                    if (raycasters[i].enabled) { raycasters[i].enabled = false; state.raycasters.Add(raycasters[i]); }
                }
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                VisiblePerfAccApplyRaycast(tApplyRaycast);
#endif
                s_CanvasHidden[entity.name] = state; // 记录被关闭者，show 时只恢复这些
            }
            else
            {
                // 恢复：只重新启用当初被我们关闭的 Canvas / GraphicRaycaster，保留其它组件原有状态
                if (s_CanvasHidden.TryGetValue(entity.name, out var state))
                {
                    // 判空：记录的 Canvas/Raycaster 可能已随实体销毁而失效。
                    // 注意必须用 UnityEngine.Object 的 ==null 重载（此处 if(c)）才能识别"已销毁"，
                    // 普通 C# null 判断识别不了，仍会抛 MissingReferenceException。
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                    long tApplyCanvas = VisiblePerfNow();
#endif
                    for (int i = 0; i < state.canvases.Count; i++)
                    {
                        var c = state.canvases[i];
                        if (c) c.enabled = true;
                    }
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                    VisiblePerfAccApplyCanvas(tApplyCanvas);
                    long tApplyRaycast = VisiblePerfNow();
#endif
                    for (int i = 0; i < state.raycasters.Count; i++)
                    {
                        var r = state.raycasters[i];
                        if (r) r.enabled = true;
                    }
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                    VisiblePerfAccApplyRaycast(tApplyRaycast);
#endif
                }
                else
                {
                    // 兜底：直接全部启用（理论上不会走到，除非状态丢失）
                    var canvases = go.GetComponentsInChildren<Canvas>(true);
                    for (int i = 0; i < canvases.Length; i++) canvases[i].enabled = true;
                    var raycasters = go.GetComponentsInChildren<GraphicRaycaster>(true);
                    for (int i = 0; i < raycasters.Length; i++) raycasters[i].enabled = true;
                }
            }
        }

        // canvas 隐藏：禁用所有「处于 active 节点且自身 enabled」的 IUIVisibleNotify 补偿组件，
        // 由组件的 OnDisable 接管清理；记录被禁用的组件，show 时只恢复这些（不覆盖其它禁用态）。
        private static void HideCanvasComponents(UIView view, string name)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            long tFetch = VisiblePerfNow();
#endif
            var list = view.gameObject.GetComponentsInChildren<IUIVisibleNotify>(true);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            VisiblePerfAccFetch(tFetch, list.Length);
            long tLoop = VisiblePerfNow();
#endif
            if (!s_CanvasHidden.TryGetValue(name, out var state))
            {
                state = new CanvasToggleState
                {
                    canvases = new List<Canvas>(),
                    raycasters = new List<GraphicRaycaster>(),
                    comps = new List<IUIVisibleNotify>(),
                };
            }
            else
            {
                if (state.comps == null) state.comps = new List<IUIVisibleNotify>();
                else state.comps.Clear(); // 重新隐藏：重建记录，避免重复
            }
            for (int i = 0; i < list.Length; i++)
            {
                var mb = list[i] as MonoBehaviour;
                if (mb == null) continue;
                if (mb.gameObject.activeInHierarchy && mb.enabled)
                {
                    mb.enabled = false;
                    state.comps.Add(list[i]);
                }
            }
            s_CanvasHidden[name] = state;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            VisiblePerfAccLoop(tLoop);
#endif
        }

        // canvas 显示：只重新启用当初被 HideCanvasComponents 禁用的补偿组件（保留其它组件原有 enabled 状态）
        private static void RestoreCanvasComponents(string name)
        {
            if (!s_CanvasHidden.TryGetValue(name, out var state)) return;
            if (state.comps == null) return;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            long tLoop = VisiblePerfNow();
#endif
            for (int i = 0; i < state.comps.Count; i++)
            {
                var mb = state.comps[i] as MonoBehaviour;
                // 用 UnityEngine.Object 的 ==null 重载识别已销毁引用，避免 MissingReferenceException
                if (mb != null && mb.gameObject.activeInHierarchy)
                    mb.enabled = true;
            }
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            VisiblePerfAccLoop(tLoop);
#endif
        }
        
        private static void SetViewAnimOpen(UIView view)
        {
            if (view != null)
            {
                var anim = view.transform.GetComponent<UIViewAnim>();
                UIView.InternalOpenAnimPlay(view);
                if (anim)
                {
                    anim.AutoPlayOpen(() =>
                    {
                        if (view.gameObject && view.gameObject.activeSelf)
                        {
                            UIView.InternalOpenAnimPlayed(view);
                        }
                    });
                }
            }
        }

        private static void SetViewAnimClose(UIView view, Action finish)
        {
            if (view != null)
            {
                if (view.transform)
                {
                    var anim = view.transform.GetComponent<UIViewAnim>();
                    if (anim)
                    {
                        UIView.InternalCloseAnimPlay(view);
                        anim.AutoPlayClose(() =>
                        {
                            if (view.gameObject && view.gameObject.activeSelf)
                            {
                                UIView.InternalCloseAnimPlayed(view);
                            }
                            finish?.Invoke();
                        });
                    }
                    else
                    {
                        finish?.Invoke();
                    }
                }
                else
                {
                    finish?.Invoke();
                }
            }
        }
        
        private static void MakeUIHandler()
        {
            s_Types.Clear();
            
            var assemblies = Umbreey.GetReflectionAssemblies();
            foreach (Assembly a in assemblies)
            {
                foreach (Type t in a.GetTypes())
                {
                    if(t.IsSubclassOf(typeof(UIView)))
                    {
                        System.Attribute[] attrs = System.Attribute.GetCustomAttributes(t);
                        foreach (UIHandlerAttribute author in attrs)
                        {
#if UNITY_EDITOR
                            var interfaces = t.GetInterfaces();
                            if (interfaces.Length > 0)
                            {
                                Log.E($"Do not use interface in UIView: {t.Name}");
                                continue;
                            }
#endif
                            s_Types[author.GetName()] = t;
                        }
                    }
                }
            }
        }

        static void LoadPrefab(string assetName,GameObject check, Action<GameObject> finish)
        {
            if (check == null)
            {
                UILock.Lock();
                Assets.InstantiateAsync<GameObject>(assetName,(go) => {
                    UILock.UnLock();
                    finish?.Invoke(go);
                });
            }
            else
            {
                finish?.Invoke(check);
            }
        }
    }
}
