using Config;
using Umbreey;

/// <summary>
/// 运行时日程状态（家据点可见：日 / 阶段 / 金 / 行动力）。
/// 阶段按定案：白天 → 晚上 → 深夜 → 翌日白天。
/// </summary>
public static class GameState
{
    public enum Phase
    {
        Day = 0,
        Evening = 1,
        LateNight = 2,
    }

    public const int MaxActionPoints = 5;

    public static int Day { get; private set; } = 1;
    public static Phase CurrentPhase { get; private set; } = Phase.Day;
    public static int Gold { get; private set; }
    public static int ActionPoints { get; private set; }
    public static string Difficulty { get; private set; } = "普通";
    public static bool IsReady { get; private set; }

    /// <summary>本阶段是否已有合法主行为（睡/出门活动等）。空过结束阶段会被拒。</summary>
    public static bool ActedThisPhase { get; private set; }

    public static string PhaseName => CurrentPhase switch
    {
        Phase.Day => "白天",
        Phase.Evening => "晚上",
        Phase.LateNight => "深夜",
        _ => "?"
    };

    public static void ResetNewGame(string difficulty = "普通")
    {
        Difficulty = difficulty ?? "普通";
        Day = 1;
        CurrentPhase = Phase.Day;
        ActedThisPhase = false;

        var g = ConfigLoader.GetGlobal();
        Gold = g != null ? g.StartGold : 100;
        int startAp = g != null ? g.StartStamina : MaxActionPoints;
        ActionPoints = UnityEngine.Mathf.Clamp(startAp, 0, MaxActionPoints);
        IsReady = true;
        Log.I($"[GameState] new game difficulty={Difficulty} day={Day} gold={Gold} ap={ActionPoints}");
    }

    public static void EnsureInitialized()
    {
        if (!IsReady)
            ResetNewGame(Difficulty);
    }

    public static void MarkActed()
    {
        ActedThisPhase = true;
    }

    public static void AddGold(int delta)
    {
        Gold = UnityEngine.Mathf.Max(0, Gold + delta);
    }

    public static void AddActionPoints(int delta)
    {
        ActionPoints = UnityEngine.Mathf.Clamp(ActionPoints + delta, 0, MaxActionPoints);
    }

    /// <summary>睡觉：+2 行动力，并计为本阶段已行动。</summary>
    public static void Sleep()
    {
        AddActionPoints(2);
        MarkActed();
        Log.I($"[GameState] sleep → ap={ActionPoints}");
    }

    /// <summary>结束本阶段。禁止空过。返回 false 表示被拒。</summary>
    public static bool TryEndPhase()
    {
        if (!ActedThisPhase)
        {
            Log.W("[GameState] 本阶段尚未行动，不能结束阶段");
            return false;
        }

        if (CurrentPhase == Phase.LateNight)
        {
            Day++;
            CurrentPhase = Phase.Day;
        }
        else
        {
            CurrentPhase = (Phase)((int)CurrentPhase + 1);
        }

        ActedThisPhase = false;
        Log.I($"[GameState] end phase → day={Day} {PhaseName}");
        return true;
    }

    /// <summary>当前阶段允许出门去的地点枚举（与 TbLocation 对齐的 MVP 子集）。</summary>
    public static LocationId[] GetOutboundLocationIds()
    {
        return CurrentPhase switch
        {
            Phase.Day => new[] { LocationId.Shop },
            Phase.Evening => new[] { LocationId.Casino },
            Phase.LateNight => new[] { LocationId.Grave },
            _ => System.Array.Empty<LocationId>()
        };
    }
}
