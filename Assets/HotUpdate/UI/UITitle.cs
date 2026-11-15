using UnityEngine;
using UnityEngine.UI;
using Umbreey;

/// <summary>
/// Title screen logic. Depends on UIGen BaseUITitle.
/// 新游戏 → 难度选择 → 任选难度后渐黑并切换 UIDialogueBottom。
/// </summary>
[UIHandler("UITitle")]
public class UITitle : BaseUITitle
{
    private const float FadeToBlackSeconds = 2f;
    private const float HoldBlackSeconds = 2f;
    private const float HoldBeforeDialogueSeconds = 1f;

    private Image m_Fade;
    private GameObject m_Menu;
    private GameObject m_DifficultyPanel;
    private CanvasGroup m_DifficultyCanvasGroup;
    private Button m_BtnBack;
    private Button m_BtnDiffEasy;
    private Button m_BtnDiffNormal;
    private Button m_BtnDiffHard;
    private bool m_ExtraBound;
    private bool m_FlowBusy;
    private float m_FadeElapsed;
    private float m_HoldElapsed;
    private int m_Phase; // 0 idle, 1 fade→difficulty, 2 hold→show difficulty, 3 fade→dialogue, 4 hold→switch

    protected override void OnOpened()
    {
        base.OnOpened();
        CacheExtraNodes();
        BindExtraButtons();
        ResetFadeAndDifficulty();
        SetTitleChromeVisible(true);
        m_FlowBusy = false;
        m_Phase = 0;
        Log.I("UITitle opened");
    }

    protected override void OnClosed()
    {
        StopFlow();
        base.OnClosed();
    }

    protected override void OnDestroyed()
    {
        StopFlow();
        base.OnDestroyed();
    }

    protected override void OnButtonClick(Component comp)
    {
        base.OnButtonClick(comp);

        if (comp == m_BtnNewGame)
        {
            if (m_FlowBusy) return;
            CacheExtraNodes();
            if (m_Fade == null || m_DifficultyPanel == null)
            {
                Log.E("UITitle missing Fade or DifficultyPanel in prefab");
                return;
            }
            BeginFadeToDifficulty();
            return;
        }

        if (comp == m_BtnQuit)
        {
            Log.I("UITitle quit");
            UmbreeySDK.QuitGame();
        }
    }

    private void CacheExtraNodes()
    {
        if (m_Fade == null)
        {
            var fadeTf = transform.Find("Fade");
            if (fadeTf != null)
                m_Fade = fadeTf.GetComponent<Image>();
        }
        if (m_Menu == null)
        {
            var menuTf = transform.Find("Menu");
            if (menuTf != null)
                m_Menu = menuTf.gameObject;
        }
        if (m_DifficultyPanel == null)
        {
            var panelTf = transform.Find("DifficultyPanel");
            if (panelTf != null)
                m_DifficultyPanel = panelTf.gameObject;
        }
        if (m_DifficultyPanel == null) return;

        if (m_DifficultyCanvasGroup == null)
        {
            m_DifficultyCanvasGroup = m_DifficultyPanel.GetComponent<CanvasGroup>();
            if (m_DifficultyCanvasGroup == null)
                m_DifficultyCanvasGroup = m_DifficultyPanel.AddComponent<CanvasGroup>();
        }

        if (m_BtnBack == null)
        {
            var t = m_DifficultyPanel.transform.Find("BtnBack");
            if (t != null) m_BtnBack = t.GetComponent<Button>();
        }
        if (m_BtnDiffEasy == null)
        {
            var t = m_DifficultyPanel.transform.Find("DifficultyCenter/BtnDiffEasy")
                    ?? FindDeep(m_DifficultyPanel.transform, "BtnDiffEasy");
            if (t != null) m_BtnDiffEasy = t.GetComponent<Button>();
        }
        if (m_BtnDiffNormal == null)
        {
            var t = m_DifficultyPanel.transform.Find("DifficultyCenter/BtnDiffNormal")
                    ?? FindDeep(m_DifficultyPanel.transform, "BtnDiffNormal");
            if (t != null) m_BtnDiffNormal = t.GetComponent<Button>();
        }
        if (m_BtnDiffHard == null)
        {
            var t = m_DifficultyPanel.transform.Find("DifficultyCenter/BtnDiffHard")
                    ?? FindDeep(m_DifficultyPanel.transform, "BtnDiffHard");
            if (t != null) m_BtnDiffHard = t.GetComponent<Button>();
        }
    }

    private static Transform FindDeep(Transform root, string name)
    {
        if (root.name == name) return root;
        for (int i = 0; i < root.childCount; i++)
        {
            var found = FindDeep(root.GetChild(i), name);
            if (found != null) return found;
        }
        return null;
    }

    private void BindExtraButtons()
    {
        if (m_ExtraBound) return;
        if (m_BtnBack != null)
            m_BtnBack.onClick.AddListener(ReturnToMainMenu);
        if (m_BtnDiffEasy != null)
            m_BtnDiffEasy.onClick.AddListener(() => OnDifficultyChosen("easy"));
        if (m_BtnDiffNormal != null)
            m_BtnDiffNormal.onClick.AddListener(() => OnDifficultyChosen("normal"));
        if (m_BtnDiffHard != null)
            m_BtnDiffHard.onClick.AddListener(() => OnDifficultyChosen("hard"));
        m_ExtraBound = true;
    }

    /// <summary>主界面背景 / 菜单显隐。进难度后保持隐藏，避免渐黑时穿帮。</summary>
    private void SetTitleChromeVisible(bool visible)
    {
        if (m_Bg != null)
            m_Bg.gameObject.SetActive(visible);
        if (m_Menu != null)
            m_Menu.SetActive(visible);
    }

    private void ResetFadeAndDifficulty()
    {
        if (m_Fade != null)
        {
            var c = m_Fade.color;
            c.r = c.g = c.b = 0f;
            c.a = 0f;
            m_Fade.color = c;
            m_Fade.gameObject.SetActive(false);
        }
        if (m_DifficultyPanel != null)
            m_DifficultyPanel.SetActive(false);
        if (m_DifficultyCanvasGroup != null)
        {
            m_DifficultyCanvasGroup.alpha = 1f;
            m_DifficultyCanvasGroup.blocksRaycasts = true;
            m_DifficultyCanvasGroup.interactable = true;
        }
    }

    private void ReturnToMainMenu()
    {
        if (m_FlowBusy && (m_Phase == 3 || m_Phase == 4)) return;
        StopFlow();
        ResetFadeAndDifficulty();
        SetTitleChromeVisible(true);
        Log.I("UITitle return to main menu");
    }

    private void OnDifficultyChosen(string difficulty)
    {
        if (m_FlowBusy) return;
        CacheExtraNodes();
        if (m_Fade == null || m_DifficultyPanel == null)
        {
            Log.E("UITitle missing Fade/DifficultyPanel");
            return;
        }
        Log.I($"UITitle difficulty chosen: {difficulty}");
        GameState.ResetNewGame(difficulty);
        BeginFadeToDialogue();
    }

    private void BeginFadeToDifficulty()
    {
        StopFlow();
        m_FlowBusy = true;
        m_Phase = 1;
        m_FadeElapsed = 0f;
        m_HoldElapsed = 0f;

        m_DifficultyPanel.SetActive(false);
        m_Fade.gameObject.SetActive(true);
        m_Fade.raycastTarget = true;
        m_Fade.transform.SetAsLastSibling();

        var color = m_Fade.color;
        color.r = color.g = color.b = 0f;
        color.a = 0f;
        m_Fade.color = color;

        Log.I("UITitle new game → fade to black");
        m_OnTimer.Update(TickFlow);
    }

    private void BeginFadeToDialogue()
    {
        StopFlow();
        m_FlowBusy = true;
        m_Phase = 3;
        m_FadeElapsed = 0f;
        m_HoldElapsed = 0f;

        // 立刻藏掉标题层，避免 Fade 透明时穿帮露出 Menu / e_Bg
        SetTitleChromeVisible(false);

        // 全黑垫底保持不透明；难度层用 CanvasGroup 2s 淡出到黑
        m_Fade.gameObject.SetActive(true);
        m_Fade.raycastTarget = true;
        var color = m_Fade.color;
        color.r = color.g = color.b = 0f;
        color.a = 1f;
        m_Fade.color = color;
        m_Fade.transform.SetAsFirstSibling();

        m_DifficultyPanel.SetActive(true);
        m_DifficultyPanel.transform.SetAsLastSibling();
        if (m_DifficultyCanvasGroup != null)
        {
            m_DifficultyCanvasGroup.alpha = 1f;
            m_DifficultyCanvasGroup.blocksRaycasts = false;
            m_DifficultyCanvasGroup.interactable = false;
        }

        Log.I("UITitle difficulty → fade to UIDialogueBottom");
        m_OnTimer.Update(TickFlow);
    }

    private void StopFlow()
    {
        m_OnTimer.Clear();
        m_FlowBusy = false;
        m_Phase = 0;
    }

    private void TickFlow()
    {
        if (m_Fade == null)
        {
            StopFlow();
            return;
        }

        float dt = Time.deltaTime;

        // 1→2：进难度
        if (m_Phase == 1)
        {
            m_FadeElapsed += dt;
            var color = m_Fade.color;
            color.a = Mathf.Clamp01(m_FadeElapsed / FadeToBlackSeconds);
            m_Fade.color = color;
            if (m_FadeElapsed >= FadeToBlackSeconds)
            {
                color.a = 1f;
                m_Fade.color = color;
                m_Phase = 2;
                m_HoldElapsed = 0f;
            }
            return;
        }

        if (m_Phase == 2)
        {
            m_HoldElapsed += dt;
            if (m_HoldElapsed >= HoldBlackSeconds)
            {
                // 标题层关掉，只留黑底 + 难度 UI
                SetTitleChromeVisible(false);
                m_Fade.gameObject.SetActive(true);
                var color = m_Fade.color;
                color.a = 1f;
                m_Fade.color = color;
                m_Fade.transform.SetAsFirstSibling();

                if (m_DifficultyCanvasGroup != null)
                {
                    m_DifficultyCanvasGroup.alpha = 1f;
                    m_DifficultyCanvasGroup.blocksRaycasts = true;
                    m_DifficultyCanvasGroup.interactable = true;
                }
                m_DifficultyPanel.SetActive(true);
                m_DifficultyPanel.transform.SetAsLastSibling();

                m_OnTimer.Clear();
                m_Phase = 0;
                m_FlowBusy = false;
                Log.I("UITitle difficulty panel shown");
            }
            return;
        }

        // 3→4：难度 UI 淡出到黑底，再切 UIDialogueBottom
        if (m_Phase == 3)
        {
            m_FadeElapsed += dt;
            float a = 1f - Mathf.Clamp01(m_FadeElapsed / FadeToBlackSeconds);
            if (m_DifficultyCanvasGroup != null)
                m_DifficultyCanvasGroup.alpha = a;
            if (m_FadeElapsed >= FadeToBlackSeconds)
            {
                if (m_DifficultyCanvasGroup != null)
                    m_DifficultyCanvasGroup.alpha = 0f;
                m_DifficultyPanel.SetActive(false);
                m_Phase = 4;
                m_HoldElapsed = 0f;
            }
            return;
        }

        if (m_Phase == 4)
        {
            m_HoldElapsed += dt;
            if (m_HoldElapsed >= HoldBeforeDialogueSeconds)
            {
                m_OnTimer.Clear();
                m_Phase = 0;
                m_FlowBusy = false;
                Log.I("UITitle → open UIDialogueBottom, close UITitle");
                UIManager.Open(UIDef.UIDialogueBottom);
                UIManager.Close(UIDef.UITitle);
            }
        }
    }
}
