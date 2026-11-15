using Config;
using UnityEngine;
using UnityEngine.UI;
using Umbreey;

/// <summary>
/// 底部对话框：从 Luban 对话表按顺序播放。
/// ESC 切换 DialogueFrame；左键 / 空格 / Enter 下一句。
/// </summary>
[UIHandler("UIDialogueBottom")]
public class UIDialogueBottom : BaseUIDialogueBottom
{
    private const int DefaultChapter = 1;
    private const int BodyFontSize = 40;

    private GameObject m_DialogueFrame;
    private Text m_BodyText;
    private Text m_SpeakerText;
    private GameObject m_SpeakerBox;

    private int m_Chapter = DefaultChapter;
    private DialogueLine m_Current;
    private bool m_Finished;

    protected override void OnOpened()
    {
        base.OnOpened();
        EnsureConfig();
        CacheUi();
        if (m_DialogueFrame != null)
            m_DialogueFrame.SetActive(true);

        StartChapter(DefaultChapter);
        m_OnTimer.Update(TickInput);
        Log.I("UIDialogueBottom opened");
    }

    protected override void OnClosed()
    {
        m_OnTimer.Clear();
        base.OnClosed();
    }

    protected override void OnDestroyed()
    {
        m_OnTimer.Clear();
        base.OnDestroyed();
    }

    /// <summary>从指定章节开头播放（按 order 最小的一句）。</summary>
    public void StartChapter(int chapter)
    {
        m_Chapter = chapter;
        m_Finished = false;
        m_Current = ConfigLoader.GetFirstDialogue(chapter);
        ApplyCurrent();
    }

    /// <summary>从指定 id 播放。</summary>
    public void StartDialogue(int chapter, int id)
    {
        m_Chapter = chapter;
        m_Finished = false;
        m_Current = ConfigLoader.GetDialogue(chapter, id);
        ApplyCurrent();
    }

    private static void EnsureConfig()
    {
        if (ConfigLoader.IsLoaded) return;
#if UNITY_EDITOR
        ConfigLoader.TryLoadFromEditorFile();
#else
        Log.W("[UIDialogueBottom] Config not loaded");
#endif
    }

    private void CacheUi()
    {
        if (m_DialogueFrame == null)
        {
            var t = FindDeep("DialogueFrame") ?? FindDeep("Frame");
            if (t != null) m_DialogueFrame = t.gameObject;
        }

        if (m_BodyText == null)
        {
            var body = FindDeep("e_DialogueText");
            if (body != null) m_BodyText = body.GetComponent<Text>();
        }

        // SpeakerBox 可能在 DialogueFrame 内，也可能被挪到 UI 根节点下
        if (m_SpeakerBox == null)
        {
            var box = FindDeep("SpeakerBox");
            if (box != null) m_SpeakerBox = box.gameObject;
        }

        if (m_SpeakerText == null)
        {
            Transform speaker = null;
            if (m_SpeakerBox != null)
                speaker = m_SpeakerBox.transform.Find("e_SpeakerText")
                          ?? FindDeepUnder(m_SpeakerBox.transform, "e_SpeakerText");
            if (speaker == null)
                speaker = FindDeep("e_SpeakerText");
            if (speaker != null) m_SpeakerText = speaker.GetComponent<Text>();
        }

        if (m_BodyText != null)
            m_BodyText.fontSize = BodyFontSize;
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

    private void SetDialogueVisible(bool show)
    {
        if (m_DialogueFrame != null)
            m_DialogueFrame.SetActive(show);

        // SpeakerBox 若不在 Frame 内，需与 Frame 同步显隐
        if (m_SpeakerBox != null)
        {
            bool underFrame = m_DialogueFrame != null
                              && m_SpeakerBox.transform.IsChildOf(m_DialogueFrame.transform);
            if (!underFrame)
            {
                if (!show)
                    m_SpeakerBox.SetActive(false);
                else
                    m_SpeakerBox.SetActive(m_Current != null && !string.IsNullOrEmpty(m_Current.Speaker));
            }
        }
    }

    private void TickInput()
    {
        if (Input.GetKeyDown(KeyCode.Escape))
        {
            CacheUi();
            if (m_DialogueFrame == null) return;
            bool show = !m_DialogueFrame.activeSelf;
            SetDialogueVisible(show);
            Log.I(show ? "UIDialogueBottom DialogueFrame show" : "UIDialogueBottom DialogueFrame hide");
            return;
        }

        CacheUi();
        if (m_DialogueFrame == null || !m_DialogueFrame.activeSelf || m_Finished)
            return;

        bool advance =
            Input.GetMouseButtonDown(0) ||
            Input.GetKeyDown(KeyCode.Space) ||
            Input.GetKeyDown(KeyCode.Return) ||
            Input.GetKeyDown(KeyCode.KeypadEnter);

        if (advance)
            Advance();
    }

    private void Advance()
    {
        if (m_Current == null)
        {
            FinishAndEnterHub();
            return;
        }

        if (m_Current.NextId <= 0)
        {
            Log.I($"UIDialogueBottom chapter {m_Chapter} finished at id={m_Current.Id}");
            FinishAndEnterHub();
            return;
        }

        var next = ConfigLoader.GetDialogue(m_Chapter, m_Current.NextId);
        if (next == null)
        {
            Log.W($"UIDialogueBottom missing next id={m_Current.NextId} chapter={m_Chapter}");
            FinishAndEnterHub();
            return;
        }

        m_Current = next;
        ApplyCurrent();
    }

    private void FinishAndEnterHub()
    {
        if (m_Finished) return;
        m_Finished = true;
        GameState.EnsureInitialized();
        Log.I("UIDialogueBottom → open UIHub (home base), close dialogue");
        UIManager.Open(UIDef.UIHub);
        UIManager.Close(UIDef.UIDialogueBottom);
    }

    private void ApplyCurrent()
    {
        CacheUi();
        if (m_Current == null)
        {
            if (m_BodyText != null) m_BodyText.text = "";
            if (m_SpeakerText != null) m_SpeakerText.text = "";
            if (m_SpeakerBox != null) m_SpeakerBox.SetActive(false);
            return;
        }

        if (m_BodyText != null)
        {
            m_BodyText.fontSize = BodyFontSize;
            m_BodyText.text = m_Current.Text ?? "";
        }

        string speaker = m_Current.Speaker ?? "";
        if (m_SpeakerText != null)
            m_SpeakerText.text = speaker;
        if (m_SpeakerBox != null)
            m_SpeakerBox.SetActive(!string.IsNullOrEmpty(speaker));

        Log.I($"UIDialogueBottom line id={m_Current.Id} speaker={speaker}");
    }
}
