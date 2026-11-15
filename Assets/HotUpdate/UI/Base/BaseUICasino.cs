// GEN by Umbreey! Do NOT edit it.
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Umbreey;
public class BaseUICasino : BaseUI
{
	protected UnityEngine.UI.Image m_Bg;
	protected UnityEngine.UI.Image m_BoardCard0;
	protected UnityEngine.UI.Image m_BoardCard1;
	protected UnityEngine.UI.Image m_BoardCard2;
	protected UnityEngine.UI.Image m_BoardCard3;
	protected UnityEngine.UI.Image m_BoardCard4;
	protected Umbreey.UIText m_TxtPot;
	protected UnityEngine.UI.Image m_Seat0_IngLight;
	protected UnityEngine.UI.Image m_Seat0_Card0;
	protected UnityEngine.UI.Image m_Seat0_Card1;
	protected UnityEngine.UI.Image m_Seat0_Tip;
	protected Umbreey.UIText m_Seat0_TxtName;
	protected Umbreey.UIText m_Seat0_TxtStack;
	protected UnityEngine.UI.Image m_Seat1_IngLight;
	protected UnityEngine.UI.Image m_Seat1_Card0;
	protected UnityEngine.UI.Image m_Seat1_Card1;
	protected UnityEngine.UI.Image m_Seat1_Tip;
	protected Umbreey.UIText m_Seat1_TxtName;
	protected Umbreey.UIText m_Seat1_TxtStack;
	protected UnityEngine.UI.Image m_Seat2_IngLight;
	protected UnityEngine.UI.Image m_Seat2_Card0;
	protected UnityEngine.UI.Image m_Seat2_Card1;
	protected UnityEngine.UI.Image m_Seat2_Tip;
	protected Umbreey.UIText m_Seat2_TxtName;
	protected Umbreey.UIText m_Seat2_TxtStack;
	protected UnityEngine.UI.Image m_Seat3_IngLight;
	protected UnityEngine.UI.Image m_Seat3_Card0;
	protected UnityEngine.UI.Image m_Seat3_Card1;
	protected UnityEngine.UI.Image m_Seat3_Tip;
	protected Umbreey.UIText m_Seat3_TxtName;
	protected Umbreey.UIText m_Seat3_TxtStack;
	protected UnityEngine.UI.Image m_Seat4_IngLight;
	protected UnityEngine.UI.Image m_Seat4_Card0;
	protected UnityEngine.UI.Image m_Seat4_Card1;
	protected UnityEngine.UI.Image m_Seat4_Tip;
	protected Umbreey.UIText m_Seat4_TxtName;
	protected Umbreey.UIText m_Seat4_TxtStack;
	protected UnityEngine.UI.Button m_BtnFold;
	protected UnityEngine.UI.Button m_BtnCheckCall;
	protected UnityEngine.UI.Button m_BtnRaise;
	protected UnityEngine.UI.Button m_BtnAllIn;
	private UIBind m_Bind;

	protected override void OnCreated()
	{
		base.OnCreated();
		m_Bind = transform.GetComponent<UIBind>();
		m_Bg = m_Bind.Bind[0] as UnityEngine.UI.Image;
		m_BoardCard0 = m_Bind.Bind[1] as UnityEngine.UI.Image;
		m_BoardCard1 = m_Bind.Bind[2] as UnityEngine.UI.Image;
		m_BoardCard2 = m_Bind.Bind[3] as UnityEngine.UI.Image;
		m_BoardCard3 = m_Bind.Bind[4] as UnityEngine.UI.Image;
		m_BoardCard4 = m_Bind.Bind[5] as UnityEngine.UI.Image;
		m_TxtPot = m_Bind.Bind[6] as Umbreey.UIText;
		m_Seat0_IngLight = m_Bind.Bind[7] as UnityEngine.UI.Image;
		m_Seat0_Card0 = m_Bind.Bind[8] as UnityEngine.UI.Image;
		m_Seat0_Card1 = m_Bind.Bind[9] as UnityEngine.UI.Image;
		m_Seat0_Tip = m_Bind.Bind[10] as UnityEngine.UI.Image;
		m_Seat0_TxtName = m_Bind.Bind[11] as Umbreey.UIText;
		m_Seat0_TxtStack = m_Bind.Bind[12] as Umbreey.UIText;
		m_Seat1_IngLight = m_Bind.Bind[13] as UnityEngine.UI.Image;
		m_Seat1_Card0 = m_Bind.Bind[14] as UnityEngine.UI.Image;
		m_Seat1_Card1 = m_Bind.Bind[15] as UnityEngine.UI.Image;
		m_Seat1_Tip = m_Bind.Bind[16] as UnityEngine.UI.Image;
		m_Seat1_TxtName = m_Bind.Bind[17] as Umbreey.UIText;
		m_Seat1_TxtStack = m_Bind.Bind[18] as Umbreey.UIText;
		m_Seat2_IngLight = m_Bind.Bind[19] as UnityEngine.UI.Image;
		m_Seat2_Card0 = m_Bind.Bind[20] as UnityEngine.UI.Image;
		m_Seat2_Card1 = m_Bind.Bind[21] as UnityEngine.UI.Image;
		m_Seat2_Tip = m_Bind.Bind[22] as UnityEngine.UI.Image;
		m_Seat2_TxtName = m_Bind.Bind[23] as Umbreey.UIText;
		m_Seat2_TxtStack = m_Bind.Bind[24] as Umbreey.UIText;
		m_Seat3_IngLight = m_Bind.Bind[25] as UnityEngine.UI.Image;
		m_Seat3_Card0 = m_Bind.Bind[26] as UnityEngine.UI.Image;
		m_Seat3_Card1 = m_Bind.Bind[27] as UnityEngine.UI.Image;
		m_Seat3_Tip = m_Bind.Bind[28] as UnityEngine.UI.Image;
		m_Seat3_TxtName = m_Bind.Bind[29] as Umbreey.UIText;
		m_Seat3_TxtStack = m_Bind.Bind[30] as Umbreey.UIText;
		m_Seat4_IngLight = m_Bind.Bind[31] as UnityEngine.UI.Image;
		m_Seat4_Card0 = m_Bind.Bind[32] as UnityEngine.UI.Image;
		m_Seat4_Card1 = m_Bind.Bind[33] as UnityEngine.UI.Image;
		m_Seat4_Tip = m_Bind.Bind[34] as UnityEngine.UI.Image;
		m_Seat4_TxtName = m_Bind.Bind[35] as Umbreey.UIText;
		m_Seat4_TxtStack = m_Bind.Bind[36] as Umbreey.UIText;
		m_BtnFold = m_Bind.Bind[37] as UnityEngine.UI.Button;
		m_BtnCheckCall = m_Bind.Bind[38] as UnityEngine.UI.Button;
		m_BtnRaise = m_Bind.Bind[39] as UnityEngine.UI.Button;
		m_BtnAllIn = m_Bind.Bind[40] as UnityEngine.UI.Button;
		OnBindEvents(m_Bind.Bind);
	}

}
// GEN by Umbreey! Do NOT edit it.
