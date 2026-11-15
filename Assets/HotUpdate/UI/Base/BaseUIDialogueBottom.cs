// GEN by Umbreey! Do NOT edit it.
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Umbreey;
public class BaseUIDialogueBottom : BaseUI
{
	protected Umbreey.UIText m_SpeakerText;
	protected Umbreey.UIText m_DialogueText;
	private UIBind m_Bind;

	protected override void OnCreated()
	{
		base.OnCreated();
		m_Bind = transform.GetComponent<UIBind>();
		m_SpeakerText = m_Bind.Bind[0] as Umbreey.UIText;
		m_DialogueText = m_Bind.Bind[1] as Umbreey.UIText;
		OnBindEvents(m_Bind.Bind);
	}

}
// GEN by Umbreey! Do NOT edit it.
