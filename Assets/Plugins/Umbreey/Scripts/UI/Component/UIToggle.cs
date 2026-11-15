using System;
using UnityEngine;
using UnityEngine.UI;

namespace Umbreey
{
    [AddComponentMenu("UI/UIToggle", 30)]
    [RequireComponent(typeof(RectTransform))]
    public class UIToggle : Toggle
    {
        public GameObject m_On;
        public GameObject m_Off;

        protected override void Awake()
        {
            base.Awake();

            transition = Transition.None;
            this.onValueChanged.AddListener(OnValueChanged);
        }

    
        private void OnValueChanged(bool on)
        {
            if (transition == Transition.None)
            {
                if (m_On)
                {
                    m_On.SetActive(on);
                }
                if (m_Off)
                {
                    m_Off.SetActive(!on);
                }
            }
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();
            this.onValueChanged.RemoveListener(OnValueChanged);
        }
        
#if UNITY_EDITOR

        protected override void OnValidate()
        {
            base.OnValidate();
            
            OnValueChanged(isOn);
        }

#endif
    }
}
