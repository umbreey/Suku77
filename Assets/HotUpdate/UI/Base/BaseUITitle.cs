// GEN by Umbreey! Do NOT edit it.
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Umbreey;
public class BaseUITitle : BaseUI
{
	protected Umbreey.UIImage m_Bg;
	protected UnityEngine.UI.Button m_BtnNewGame;
	protected UnityEngine.UI.Button m_BtnLoad;
	protected UnityEngine.UI.Button m_BtnChapter;
	protected UnityEngine.UI.Button m_BtnSettings;
	protected UnityEngine.UI.Button m_BtnQuit;
	private UIBind m_Bind;

	protected override void OnCreated()
	{
		base.OnCreated();
		m_Bind = transform.GetComponent<UIBind>();
		m_Bg = m_Bind.Bind[0] as Umbreey.UIImage;
		m_BtnNewGame = m_Bind.Bind[1] as UnityEngine.UI.Button;
		m_BtnLoad = m_Bind.Bind[2] as UnityEngine.UI.Button;
		m_BtnChapter = m_Bind.Bind[3] as UnityEngine.UI.Button;
		m_BtnSettings = m_Bind.Bind[4] as UnityEngine.UI.Button;
		m_BtnQuit = m_Bind.Bind[5] as UnityEngine.UI.Button;
		OnBindEvents(m_Bind.Bind);
	}

}
// GEN by Umbreey! Do NOT edit it.
