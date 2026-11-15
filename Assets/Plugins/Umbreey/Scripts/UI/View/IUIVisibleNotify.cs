namespace Umbreey
{
    /// <summary>
    /// canvas-toggle 隐藏模式下，标记该组件需要参与可见性补偿。
    ///
    /// 注意：本接口为「空标记接口」，不再携带 Visible/SetVisible 契约。
    /// 框架在 UI 以 canvas 方式隐藏/显示时，直接切换组件自身的 <c>enabled</c>
    /// （而非调用 SetVisible），由组件自身的 <c>OnDisable</c>/<c>OnEnable</c> 接管显隐逻辑。
    /// 这样 canvas 与非 canvas 两条路径统一走原生生命周期，去掉了原先的 Visible 标志与去重状态机。
    ///
    /// 约束：实现方必须是 <c>MonoBehaviour</c>（框架需操作 <c>enabled</c>）。
    /// 实现方直接在自身的 <c>OnDisable</c>/<c>OnEnable</c> 里写显隐逻辑即可，无需额外基类。
    /// </summary>
    public interface IUIVisibleNotify
    {
    }
}
