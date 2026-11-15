using Umbreey;

namespace HotUpdate
{
    /// <summary>
    /// 进入 Game 组时确保日程状态可用（新开局由标题难度选定后 Reset）。
    /// </summary>
    [ModHandler(ModDef.Game)]
    public class GameStateMod : Mod
    {
        protected override void OnStarted()
        {
            base.OnStarted();
            if (!ConfigLoader.IsLoaded)
            {
#if UNITY_EDITOR
                ConfigLoader.TryLoadFromEditorFile();
#endif
            }
            GameState.EnsureInitialized();
            Log.I("[GameStateMod] ready");
        }
    }
}
