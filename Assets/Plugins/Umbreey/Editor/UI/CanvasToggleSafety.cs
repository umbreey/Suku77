using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Umbreey;

namespace Umbreey.Editor
{
    /// <summary>
    /// canvas-toggle 安全性共享判定：Gen（白名单生成）与 Validator（后编译/生成后校验）共用同一套逻辑，避免两边漂移。
    ///
    /// 一个组件允许“所在 prefab 走 canvas-toggle 隐藏（只关 Canvas，不触发其 OnDisable）”当且仅当满足以下任一：
    ///  0. 命中 kConfirmedSafeComponents 类型白名单——人工读过源码确认安全，优先级最高（可覆盖下面的例外）；
    ///  1. Unity 原生组件（沿继承链含自身命中 UnityEngine/UnityEditor/TMPro 命名空间或程序集）；
    ///  2. 良性 B 类（UIImage/UIText/UITextMeshPro/UIRawImage 及基类）；
    ///  3. 无 OnDisable/OnEnable 生命周期方法（OnDestroy 仅销毁时触发，不计入）；
    ///  4. 已实现 IUIVisibleNotify——组件自行接管可见性回调。
    ///
    /// 例外：UGUIEffect 套件（UIShiny/UIShadow/UIHsvModifier/UIGradient/UIDissolve/UIFlip）经 BaseMeshEffect/
    /// BaseMaterialEffect 派生自 Unity 基类，但其 OnEnable/OnDisable 注册全局特效列表、操作共享材质，必须保持“需补偿”。
    /// 故在 IsUnityNative 命中后仍用 IsUnsafeUnityDerived 强制判不安全；其中经人工核实确实无害的成员可加进
    /// kConfirmedSafeComponents 单独放行（白名单判定在最前，可覆盖此例外）。
    ///
    /// 注意：本判定只覆盖“生命周期方法”驱动的副作用。Update 驱动的空转（如 LoopListView2/GridView）不实现任何生命周期方法，
    /// 本函数会误判为安全；这类由调用方加进 kConfirmedSafeComponents 兜底（当前已列入，若产生性能问题再移出）。
    /// </summary>
    public static class CanvasToggleSafety
    {
        // 良性 B 类：本地资源持有，canvas-toggle 下常驻持有是净收益，OnDisable 跳过无正确性问题。
        private static readonly HashSet<string> kBenignComponents = new HashSet<string>
        {
            "UIImage", "UIText", "UITextMeshPro", "UIRawImage",
        };

        /// <summary>
        /// 已确认安全的组件类型白名单（按类型名精确匹配，不沿继承链，避免误放行派生子类）。
        /// 用途：替代在组件类上标注特性的做法——集中维护，业务/第三方源码零改动。
        /// 收录标准：人工读过源码，确认 OnDisable/OnEnable 无跨子树外部副作用；每条写明理由与核实日期。
        /// 优先级最高，可覆盖 IsUnsafeUnityDerived 的强制不安全判定；写错等于把一个真不安全的组件静默放行。
        /// </summary>
        private static readonly HashSet<string> kConfirmedSafeComponents = new HashSet<string>
        {
            // UIGradient（Coffee.UIEffects，2026-09-07 核实）：
            // 没 override OnEnable/OnDisable；继承的 BaseMeshEffect.OnDisable 仅
            // connector.OnDisable(graphic)（GraphicConnector 默认空实现）+ SetVerticesDirty。
            // canvas 隐藏后 VertexHelper 不再调 ModifyMesh，SetVerticesDirty 无副作用；重开时自动重算顶点。
            "UIGradient",

            // ======== 以下 5 个为 UGUIEffect 套件成员（2026-09-07 逐个核实后放行）========
            // 共性结论：paramTex 槽位不累积、EffectPlayer 均未播放、材质按引用计数配对，跳过 OnDisable 无正确性问题：
            //   ① paramTex 槽位【不累积泄漏】：ParameterTexture.Register 幂等且仅在 OnEnable 调用
            //      （ParameterTexture.cs:54）；canvas-toggle 隐藏期间既不 OnDisable 也不 OnEnable
            //      ⇒ 每实例恒占 1 槽，与开关次数无关。未 Unregister 的槽位不会被别的组件 Pop 走
            //      （_stack 空闲栈，:39-43），不会串扰。
            //   ② EffectPlayer【全部未播放】：业务代码从无 Play() 调用；Gen 下 29 个含 m_Player 的
            //      prefab 中 play:1 出现 0 次 ⇒ OnWillRenderCanvases 首行 !play 早退
            //      （EffectPlayer.cs:135）⇒ 无 tick、无「动画偷偷播完停在终态」、无参数纹理重传。
            //   ③ 材质：MaterialCache 按 Hash128 去重 + 引用计数，Register(new)+Unregister(old)
            //      原子配对；隐藏仅把释放推迟到 Destroy，随常驻 UI 数量有界增长，不随时间发散。
            //   ④ 综上，跳过 OnDisable 的代价仅为「槽位/材质常驻持有 + 隐藏期不渲染」，
            //      无逻辑错误、无状态错乱、无内存泄漏。
            //
            // ⚠️ 约束：这些组件的 m_Player.play 必须保持 false，禁止调用 Play()。
            //    一旦启用 play，隐藏期间动画会偷偷播完并停在终态（重开不会重播），且每帧重传参数纹理
            //    （4 个静态 paramTex 合计约 7KB/帧，开销可忽略但语义已变）。届时须改为 fork 补偿：
            //    OnHide 调 effectPlayer.OnDisable() + paramTex.Unregister() + MaterialCache.Unregister()。
            // ⚠️ 余量：UIShiny 独立池 128，当前 48/128 最紧；若实例数接近 128 需重新评估（耗尽会
            //    静默错乱，且关闭再打开也不会自愈）。另见 UICanvasToggleGen 顶部 TODO。

            // UIShadow：无 EffectPlayer，不参与每帧 tick；仅 paramTex 注册，且复用同物体 UGUIEffect 的
            // 1024 池（同物体无 UGUIEffect 时根本不 Register，UIShadow.cs:131）。当前 37 实例。
            "UIShadow",

            // UIHsvModifier：无 EffectPlayer，未 override 生命周期方法；独立 128 池，仅 3 实例。
            "UIHsvModifier",

            // UIShiny：有 EffectPlayer 但 play 恒 false（见上约束）；独立 128 池，当前 48/128。
            "UIShiny",

            // UIDissolve：有 EffectPlayer 但 play 恒 false；独立 128 池，仅 3 实例。
            "UIDissolve",

            // UGUIEffect：套件主组件，静态 s_ParamTex(4,1024) 容量充裕（5 实例）。
            // 注：UIShadow 依赖同物体的本组件才能取到 paramTex，故两者需同时放行。
            "UGUIEffect",
            
            //主要是Update，若存在性能问题再优化
            "LoopListView2",
            
            "LoopGridView",
        };
        
        /// <summary>显式强制排除名单（当前为空）。</summary>
        private static readonly HashSet<string> kForceExclude = new HashSet<string> { };

        public static bool IsComponentCanvasSafe(Type t)
        {
            if (t == null) return false;
            // 白名单优先级最高：可覆盖下面的 IsUnsafeUnityDerived（人工已核实安全）。
            if (IsConfirmedSafe(t)) return true;
            if (IsConfirmedUnSafe(t)) return false;
            // 原生组件直接放行；
            if (IsUnityNative(t)) return true;
            if (IsBenign(t)) return true;
            if (!HasLifecycle(t)) return true;
            if (IsCompensated(t)) return true;
            return false;
        }

        /// <summary>类型白名单精确匹配（不沿继承链，避免误放行派生子类）。</summary>
        private static bool IsConfirmedSafe(Type t)
        {
            return kConfirmedSafeComponents.Contains(t.Name);
        }
        
        private static bool IsConfirmedUnSafe(Type t)
        {
            return kForceExclude.Contains(t.Name);
        }

        /*// UGUIEffect 套件（UIShiny/UIShadow/UIHsvModifier/UIGradient/UIDissolve/UIFlip 等）虽继承自 Unity 基类（UIBehaviour），
        // 但其 OnEnable/OnDisable 注册全局特效列表、操作共享材质，canvas-toggle 跳过 OnDisable 会泄漏全局状态，
        // 必须保持“需补偿”。按 BaseMeshEffect / BaseMaterialEffect 基类名判定（命名空间无关，避免耦合）。
        private static bool IsUnsafeUnityDerived(Type t)
        {
            for (var cur = t; cur != null && cur != typeof(MonoBehaviour) && cur != typeof(Component) && cur != typeof(object); cur = cur.BaseType)
                if (cur.Name == "BaseMeshEffect" || cur.Name == "BaseMaterialEffect") return true;
            return false;
        }*/

        // 只认 OnDisable/OnEnable：这两个是“隐藏/显示”相关的生命周期，canvas-toggle 跳过 OnDisable 才需要补偿。
        // OnDestroy 仅 GameObject 真正销毁时触发，canvas-toggle 不阻止销毁，故其存在不构成隐藏风险，不应据此排除。
        public static bool HasLifecycle(Type t)
        {
            var flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
            return t.GetMethod("OnDisable", flags) != null
                || t.GetMethod("OnEnable", flags) != null;
        }

        /// <summary>仅认 IUIVisibleNotify：组件自行接管可见性回调。</summary>
        public static bool IsCompensated(Type t)
        {
            return typeof(IUIVisibleNotify).IsAssignableFrom(t);
        }

        // B 类及其基类（沿继承链任一类型名命中即良性）
        public static bool IsBenign(Type t)
        {
            for (var cur = t; cur != null && cur != typeof(MonoBehaviour) && cur != typeof(Component) && cur != typeof(object); cur = cur.BaseType)
                if (kBenignComponents.Contains(cur.Name)) return true;
            return false;
        }

        // 沿继承链（含组件自身）判断：组件自身常位于业务/框架命名空间，但其基类可能在 UnityEngine.*，
        // 故必须从 t 自身开始向上遍历；Image/Mask/CanvasScaler 等自身即 UnityEngine.UI 的组件也能命中。
        // UGUIEffect 套件同样会命中本判定，但其全局副作用由 IsComponentCanvasSafe 内的 IsUnsafeUnityDerived 兜底。
        public static bool IsUnityNative(Type t)
        {
            //UGUIEffect 套件虽继承 Unity 基类却带全局副作用，须排除在原生安全之外。
            /*if (!IsUnsafeUnityDerived(t))
            {
                return false;
            }*/
            for (var cur = t; cur != null && cur != typeof(MonoBehaviour) && cur != typeof(Component) && cur != typeof(object); cur = cur.BaseType)
            {
                var ns = cur.Namespace;
                if (ns != null && (ns.StartsWith("UnityEngine") || ns.StartsWith("UnityEditor")
                    || ns == "TMPro" || ns.StartsWith("TMPro.")))
                    return true;
                var asm = cur.Assembly.GetName().Name;
                if (asm == "UnityEngine" || asm.StartsWith("UnityEngine.") || asm == "UnityEditor" || asm.StartsWith("UnityEditor."))
                    return true;
                // TextMeshPro 是 Unity 官方包（TMPro 命名空间 / Unity.TextMeshPro 程序集），其 OnDisable 仅做内部 mesh/输入清理，
                // canvas-toggle 跳过无正确性问题，按 Unity 原生同等对待。
                if (asm == "Unity.TextMeshPro" || asm.StartsWith("Unity.TextMeshPro"))
                    return true;
            }
            return false;
        }
    }
}
