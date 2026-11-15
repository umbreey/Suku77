// GEN by Umbreey! Do NOT edit it.
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Umbreey;
public class BaseUIHub : BaseUI
{
	protected Umbreey.UIImage m_Bg;
	protected Umbreey.UIText m_TxtDay;
	protected Umbreey.UIText m_TxtPhase;
	protected Umbreey.UIText m_TxtGold;
	protected Umbreey.UIText m_TxtAp;
	protected Umbreey.UIText m_TxtHint;
	protected UnityEngine.UI.Button m_BtnSleep;
	protected UnityEngine.UI.Button m_BtnWarehouse;
	protected UnityEngine.UI.Button m_BtnRepair;
	protected UnityEngine.UI.Button m_BtnCraft;
	protected UnityEngine.UI.Button m_BtnStatus;
	protected UnityEngine.UI.Button m_BtnGoOut;
	protected UnityEngine.UI.Button m_BtnEndPhase;
	private UIBind m_Bind;

	protected override void OnCreated()
	{
		base.OnCreated();
		m_Bind = transform.GetComponent<UIBind>();
		m_Bg = m_Bind.Bind[0] as Umbreey.UIImage;
		m_TxtDay = m_Bind.Bind[1] as Umbreey.UIText;
		m_TxtPhase = m_Bind.Bind[2] as Umbreey.UIText;
		m_TxtGold = m_Bind.Bind[3] as Umbreey.UIText;
		m_TxtAp = m_Bind.Bind[4] as Umbreey.UIText;
		m_TxtHint = m_Bind.Bind[5] as Umbreey.UIText;
		m_BtnSleep = m_Bind.Bind[6] as UnityEngine.UI.Button;
		m_BtnWarehouse = m_Bind.Bind[7] as UnityEngine.UI.Button;
		m_BtnRepair = m_Bind.Bind[8] as UnityEngine.UI.Button;
		m_BtnCraft = m_Bind.Bind[9] as UnityEngine.UI.Button;
		m_BtnStatus = m_Bind.Bind[10] as UnityEngine.UI.Button;
		m_BtnGoOut = m_Bind.Bind[11] as UnityEngine.UI.Button;
		m_BtnEndPhase = m_Bind.Bind[12] as UnityEngine.UI.Button;
		OnBindEvents(m_Bind.Bind);
	}

}
// GEN by Umbreey! Do NOT edit it.
