using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

namespace Umbreey
{
    public class Umbreey : MonoBehaviour
    {
        // 配置项
        [Header("UI_RUNTIME")]
        public Transform UIRoot;
        public Camera UICamera;
        public RenderMode UIRenderMode = RenderMode.ScreenSpaceOverlay;
        public float UIDestroyTime = 15f;
        
        // 默认按钮点击事件
        public Action<Button> UIBtnClick;
        public Action<Toggle> UITogClick;
        public Action OnQuit;
        
        private static Umbreey s_Inst;
        public static Umbreey Inst
        {
            get
            {
                if (s_Inst == null && (!Quitting || !Application.isPlaying))
                {
                    s_Inst = GameObject.FindObjectOfType(typeof(Umbreey)) as Umbreey;
                    if (s_Inst == null && Application.isPlaying)
                    {
                        Log.W("create default umbreey instance");
                        s_Inst = new GameObject("Umbreey").AddComponent<Umbreey>();
                    }
                }
                return s_Inst;
            }
        }
        public static bool Quitting { get; private set; } = false;
        
        void Awake()
        {
            DontDestroyOnLoad(this.gameObject);
            if (!UIRoot)
            {
                UIRoot = new GameObject("UIRoot").transform;
            }
            if (UIRoot)
            {
                DontDestroyOnLoad(UIRoot.gameObject);
            }

            if (!UICamera)
            {
                UICamera = GameObject.Find("UICamera")?.GetComponent<Camera>();
            }

            if (UICamera && UIRenderMode != RenderMode.ScreenSpaceCamera)
            {
                UICamera.gameObject.SetActive(false);
            }
        }

        void OnApplicationQuit() 
        {
            Quitting = true;
            OnQuit?.Invoke();
        }
        
        void StopAllCoroutines() // 重载用来检查误调
        {
            Log.E("can not call this function!");
        }

    #region Reflection Assembly
        public static Action OnReflectionAssemblySet;
        private static Assembly s_Assembly;
        
        public static void SetReflectionAssembly(Assembly ass)
        {
            s_Assembly = ass;
            OnReflectionAssemblySet?.Invoke();
        }

        public static Assembly[] GetReflectionAssemblies()
        {
            if (s_Assembly != null)
                return new Assembly[] { s_Assembly };
            else
                return AppDomain.CurrentDomain.GetAssemblies();
        }
    #endregion

        
    }

}