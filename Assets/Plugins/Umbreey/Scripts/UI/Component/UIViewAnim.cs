using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;

namespace Umbreey
{
    public class UIViewAnim : MonoBehaviour,IExportProcess, IUIVisibleNotify
    {
        [SerializeField] public string enterClipName;
        [SerializeField] public string loopClipName;
        [SerializeField] public string exitClipName;
        [SerializeField, ReadOnly] public Animation anim;
        [SerializeField] public bool playAutomatically = true;
        [SerializeField] public bool playAutomaticallyClose = false;

        private string clipEnterFormat = "_Enter";
        private string clipExitFormat = "_Exit";
        private string clipLoopFormat = "_Loop";
        
        private string lockAnim;
        private int m_TimeIndex = 0;

        public Animation Anim
        {
            get
            {
                if(!anim)
                    anim = gameObject.GetComponent<Animation>();
                return anim;
            }
        }
        public void PlayWithCallBack(string clipName, Action finish)
        {
            CleanTimerAction(); 
            anim = gameObject.GetComponent<Animation>();
            if (anim)
            {
                anim.Stop();
                var clip = anim.GetClip(clipName);
                if (clip != null)
                {
                    lockAnim = clipName;
                    var length = clip.length;
                    anim.Play(clipName);
                    m_TimeIndex = TaskManager.Timeout(() =>
                    {
                        finish?.Invoke();   
                    }, length);
                }
                else
                {
                    finish?.Invoke();
                }
            }
            else
            {
                finish?.Invoke();
            }
        }

        public void AutoPlayOpen(Action finish)
        {
            if (!playAutomatically)
            {
                finish?.Invoke();
                return;
            }
            PlayOpen(finish);
        }

        public void PlayOpen(Action finish = null)
        {
            CleanTimerAction();
            
            Action action = () =>
            {
                finish?.Invoke();
                PlayLoop();
            };
            if (string.IsNullOrEmpty(enterClipName))
            {
                action?.Invoke();
            }
            //Log.D($"PlayOpen {enterClipName}");
            ResetAnimState(gameObject.GetComponent<Animation>());
            PlayWithCallBack(enterClipName, action);
        }

        public void AutoPlayClose(Action finish)
        {
            if (!playAutomaticallyClose)
            {
                finish?.Invoke();
                return;
            }
            PlayClose(finish);
        }

        public void PlayClose(Action finish = null)
        {
            CleanTimerAction();
            
            if(string.IsNullOrEmpty(exitClipName))
            {
                finish?.Invoke();
                return;
            }

            UILock.Lock(exitClipName);
            PlayWithCallBack(exitClipName, ()=>
            {
                UILock.UnLock(exitClipName);
                finish?.Invoke();
            });
        }

        private void PlayLoop()
        {
            CleanTimerAction();
            SetAnimEnded(gameObject, enterClipName);
            if(string.IsNullOrEmpty(loopClipName))
            {
                return;
            }

            PlayWithCallBack(loopClipName, null);
        }
        
        public void CleanTimerAction()
        {
            if (!string.IsNullOrEmpty(lockAnim))
            {
                UILock.UnLock(lockAnim);
                lockAnim = null;
            }
            
            if (m_TimeIndex > 0)
            {
                TaskManager.Remove(m_TimeIndex);
                m_TimeIndex = 0;
            } 
        }
        
        private void ResetAnimState(Animation anim, string animName = null)
        {
            if(anim == null)
            {
                Log.E("ui animation not found");
                return;
            }

            string playingAnim = null;
            foreach (AnimationState state in anim)
            {
                if (!string.IsNullOrEmpty(animName))
                {
                    if (anim.IsPlaying(state.name))
                    {
                        playingAnim = state.name;
                    }
                    else if(state.name == animName)
                    {
                        anim.Play(state.name);
                        state.time = 0;
                        anim.Sample();
                        state.enabled = false;
                    }
                }
                else
                {
                    anim.Play(state.name);
                    state.time = 0;
                    anim.Sample();
                    state.enabled = false;
                }
            }

            if (!string.IsNullOrEmpty(playingAnim))
            {
                anim.Play(playingAnim);
            }
        }
        
        private void SetAnimEnded(GameObject go, string animName)
        {
            var anim = go.GetComponent<Animation>();
            
            if(anim == null)
            {
                Log.W("ui animation not found");
                return;
            }

            foreach (AnimationState state in anim)
            {
                if(state.name == animName)
                {
                    state.time = state.length;
                    anim.Sample();
                    state.enabled = false;
                    break;
                }
            }
        }

        private void OnDisable()
        {
            CleanTimerAction();
        }

        void OnDestroy()
        {
            CleanTimerAction();
        }
        
#if UNITY_EDITOR

        private void Reset()
        {
            anim = gameObject.GetComponent<Animation>();
            if (anim)
            {
                anim.playAutomatically = false;

                foreach (AnimationState state in anim)
                {
                    if(state.name.EndsWith(clipEnterFormat))
                    {
                        enterClipName = state.name;
                    }
                    else if(state.name.EndsWith(clipExitFormat))
                    {
                        exitClipName = state.name;
                    }
                    else if(state.name.EndsWith(clipLoopFormat))
                    {
                        loopClipName = state.name;
                    }
                }
            }
        }
        
#endif

        public void Process()
        {
#if UNITY_EDITOR
            Animation aniamtion = gameObject.GetComponent<Animation>();
            if (aniamtion)
            {
                //aniamtion.playAutomatically = false;
            }
#endif
        }
    }
}