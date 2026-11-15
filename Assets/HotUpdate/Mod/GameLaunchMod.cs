using Umbreey;

namespace HotUpdate
{
    /// <summary>
    /// 游戏启动编排（对齐 client_template GameLaunchMod 的职责：进入首个正式界面）。
    /// 当前项目首屏为 UITitle；后续可在此扩展场景加载 / 登录等阶段。
    /// </summary>
    [ModHandler(ModDef.Game)]
    public class GameLaunchMod : Mod
    {
        protected override void OnStarted()
        {
            base.OnStarted();
            Log.I("[GameLaunchMod] Open UITitle");
            UIManager.Open(UIDef.UITitle);
        }
    }
}
