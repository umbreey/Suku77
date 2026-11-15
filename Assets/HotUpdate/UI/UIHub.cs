using Config;
using UnityEngine;
using UnityEngine.UI;
using Umbreey;

/// <summary>
/// 家 = 兰斯式据点一屏：全屏场景 + 热点设施 + 顶栏状态 + 结束阶段。
/// 出门按当前时段打开可选地点（占位）。
/// </summary>
[UIHandler("UIHub")]
public class UIHub : BaseUIHub
{
    private Text m_Day;
    private Text m_Phase;
    private Text m_Gold;
    private Text m_Ap;
    private Text m_Hint;
    private Button m_Sleep;
    private Button m_Warehouse;
    private Button m_Repair;
    private Button m_Craft;
    private Button m_Status;
    private Button m_GoOut;
    private Button m_EndPhase;

    private GameObject m_OutPanel;
    private bool m_Cached;

    protected override void OnOpened()
    {
        base.OnOpened();
        GameState.EnsureInitialized();
        CacheRefs();
        HideOutPanel();
        RefreshStatus();
        SetHint("点击屋内设施；出门按时段前往地点。结束阶段前须有行动。");
        Log.I("UIHub opened (home base)");
    }

    protected override void OnButtonClick(Component comp)
    {
        base.OnButtonClick(comp);
        CacheRefs();

        if (comp == m_Sleep)
        {
            GameState.Sleep();
            RefreshStatus();
            SetHint("休息完毕，行动力 +2。");
            return;
        }

        if (comp == m_Warehouse)
        {
            GameState.MarkActed();
            RefreshStatus();
            SetHint("仓库（占位）：日后可拖拽整理背包与仓库。");
            return;
        }

        if (comp == m_Repair)
        {
            GameState.MarkActed();
            RefreshStatus();
            SetHint("修理台（占位）：修复镐铲耐久。");
            return;
        }

        if (comp == m_Craft)
        {
            GameState.MarkActed();
            RefreshStatus();
            SetHint("整理 / 简单合成（占位）。");
            return;
        }

        if (comp == m_Status)
        {
            SetHint($"人物状态（占位）· 难度 {GameState.Difficulty} · 段位 闲手");
            return;
        }

        if (comp == m_GoOut)
        {
            ToggleOutPanel();
            return;
        }

        if (comp == m_EndPhase)
        {
            if (!GameState.TryEndPhase())
            {
                SetHint("本阶段还没做过任何事，不能空过。先睡觉或出门活动。");
                return;
            }
            HideOutPanel();
            RefreshStatus();
            SetHint($"进入：第 {GameState.Day} 天 · {GameState.PhaseName}");
            return;
        }
    }

    private void CacheRefs()
    {
        if (m_Cached) return;

        m_Day = m_TxtDay ?? GetText("e_TxtDay");
        m_Phase = m_TxtPhase ?? GetText("e_TxtPhase");
        m_Gold = m_TxtGold ?? GetText("e_TxtGold");
        m_Ap = m_TxtAp ?? GetText("e_TxtAp");
        m_Hint = m_TxtHint ?? GetText("e_TxtHint");

        m_Sleep = m_BtnSleep ?? GetButton("e_BtnSleep");
        m_Warehouse = m_BtnWarehouse ?? GetButton("e_BtnWarehouse");
        m_Repair = m_BtnRepair ?? GetButton("e_BtnRepair");
        m_Craft = m_BtnCraft ?? GetButton("e_BtnCraft");
        m_Status = m_BtnStatus ?? GetButton("e_BtnStatus");
        m_GoOut = m_BtnGoOut ?? GetButton("e_BtnGoOut");
        m_EndPhase = m_BtnEndPhase ?? GetButton("e_BtnEndPhase");

        var outT = FindDeep("OutPanel");
        if (outT != null)
        {
            m_OutPanel = outT.gameObject;
            var closeT = FindDeepUnder(outT, "BtnCloseOut");
            if (closeT != null)
            {
                var closeBtn = closeT.GetComponent<Button>();
                if (closeBtn != null)
                {
                    closeBtn.onClick.RemoveAllListeners();
                    closeBtn.onClick.AddListener(HideOutPanel);
                }
            }
        }

        m_Cached = true;
    }

    private void RefreshStatus()
    {
        if (m_Day != null) m_Day.text = $"第 {GameState.Day} 天";
        if (m_Phase != null) m_Phase.text = GameState.PhaseName;
        if (m_Gold != null) m_Gold.text = $"金钱 {GameState.Gold}";
        if (m_Ap != null) m_Ap.text = $"行动力 {GameState.ActionPoints}/{GameState.MaxActionPoints}";
    }

    private void SetHint(string msg)
    {
        if (m_Hint != null) m_Hint.text = msg ?? "";
        Log.I($"[UIHub] {msg}");
    }

    private void ToggleOutPanel()
    {
        if (m_OutPanel == null)
        {
            SetHint("出门面板缺失");
            return;
        }

        bool show = !m_OutPanel.activeSelf;
        if (show) RebuildOutPanel();
        m_OutPanel.SetActive(show);
        SetHint(show ? "选择要去的地点（占位，暂无子界面）。" : "留在家里。");
    }

    private void HideOutPanel()
    {
        if (m_OutPanel != null) m_OutPanel.SetActive(false);
    }

    private void RebuildOutPanel()
    {
        if (m_OutPanel == null) return;
        var ids = GameState.GetOutboundLocationIds();
        for (int i = 0; i < 3; i++)
        {
            var btnT = FindDeepUnder(m_OutPanel.transform, $"BtnLoc{i}");
            if (btnT == null) continue;
            var btn = btnT.GetComponent<Button>();
            var label = btnT.GetComponentInChildren<Text>();
            bool on = i < ids.Length;
            btnT.gameObject.SetActive(on);
            if (!on) continue;

            var loc = ConfigLoader.GetLocationByEnum(ids[i]);
            string name = loc != null ? loc.Name : ids[i].ToString();
            if (label != null) label.text = name;
            int capture = i;
            btn.onClick.RemoveAllListeners();
            btn.onClick.AddListener(() => OnPickLocation(ids[capture]));
        }
    }

    private void OnPickLocation(LocationId id)
    {
        var loc = ConfigLoader.GetLocationByEnum(id);
        string name = loc != null ? loc.Name : id.ToString();
        string ui = loc != null ? loc.UiName : "";
        GameState.MarkActed();
        RefreshStatus();
        HideOutPanel();
        SetHint($"前往「{name}」（占位）。目标 UI={ui}，尚未实现。");
    }

    private Text GetText(string name)
    {
        var t = FindDeep(name);
        return t != null ? t.GetComponent<Text>() : null;
    }

    private Button GetButton(string name)
    {
        var t = FindDeep(name);
        return t != null ? t.GetComponent<Button>() : null;
    }

    private Transform FindDeep(string name) => FindDeepUnder(transform, name);

    private static Transform FindDeepUnder(Transform root, string name)
    {
        if (root == null) return null;
        if (root.name == name) return root;
        for (int i = 0; i < root.childCount; i++)
        {
            var child = root.GetChild(i);
            if (child.name == name) return child;
            var nested = FindDeepUnder(child, name);
            if (nested != null) return nested;
        }
        return null;
    }
}
