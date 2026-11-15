using System;
using UnityEngine;

#if UMBREEY_SDK_GME
using GME;

public partial class UmbreeySDK
{
    static string openId;
    private static int appId;
    private static string appToken;

    public static void GmeInit(int appid, string token)
    {
        appId = appid;
        appToken = token;
        _behaviour.update-= Update;
        _behaviour.update+= Update;
    }
    
    public static void SetPlayer(int playerId)
    {
        var newOpenid = playerId.ToString();
        if (!string.IsNullOrEmpty(openId) && openId != newOpenid)
        {
            ITMGContext.GetInstance().Uninit();
        }
        openId = newOpenid;
        int ret = ITMGContext.GetInstance().Init(appId.ToString(), openId);
        //通过返回值判断是否初始化成功
        if (ret == QAVError.OK)
        {
            LogGME("SDK初始化成功");
        }
    }
    
    //加入房间
    private static Action _enterRoomSuccess;
    private static Action _enterRoomError;
    public static void EnterRoom(string roomName, Action success,Action error)
    {
        _enterRoomSuccess = success;
        _enterRoomError = error;
        ITMGContext.GetInstance().OnEnterRoomCompleteEvent -= EnterRoomCallback;
        ITMGContext.GetInstance().OnEnterRoomCompleteEvent += EnterRoomCallback;
  
        var bytes=  QAVAuthBuffer.GenAuthBuffer(appId, roomName, openId, appToken);
        ITMGContext.GetInstance().EnterRoom(roomName, ITMGRoomType.ITMG_ROOM_TYPE_FLUENCY, bytes);
        LogGME($"JoinTeamRoom roomName={roomName}");
    }

    static void EnterRoomCallback(int result, string error_info)
    {
        if (result != 0) {
            LogGME("错误码:" + result + " 错误信息:" + error_info);
            _enterRoomError?.Invoke();
        }
        else{
            //进房成功
            _enterRoomSuccess?.Invoke();
        }
    }

    //退出房间
    private static Action _exitRoomSuccess;
    public static void QuitRoom(Action success)
    {
        _exitRoomSuccess= success;
        ITMGContext.GetInstance().OnExitRoomCompleteEvent -= ExitRoomCallback;
        ITMGContext.GetInstance().OnExitRoomCompleteEvent += ExitRoomCallback;
        ITMGContext.GetInstance().ExitRoom();
    }
    static void ExitRoomCallback()
    {
        _exitRoomSuccess?.Invoke();
    }
    
    //打开麦克风
    public static void OpenMic()
    {
        LogGME($"OpenMic");
        ITMGContext.GetInstance().GetAudioCtrl().EnableMic(true);
    }
    //关闭麦克风
    public static void CloseMic()
    {
        LogGME($"CloseMic");
        ITMGContext.GetInstance().GetAudioCtrl().EnableMic(false);
    }
    
    //打开听筒
    public static void OpenSpeaker()
    {
        LogGME($"OpenSpeaker");
        ITMGContext.GetInstance().GetAudioCtrl().EnableSpeaker(true);
    }
    
    //关闭听筒
    public static void CloseSpeaker()
    {
        LogGME($"CloseSpeaker");
        ITMGContext.GetInstance().GetAudioCtrl().EnableSpeaker(false);
    }

    static void LogGME(string log)
    {
        Debug.Log($"[GME: {log}]");
    }
    
    static float lastTime = 0;
    static void Update()
    {
        if (Time.time - lastTime > 0.1f)
        {
            ITMGContext.GetInstance().Poll();
            lastTime = Time.time;
        }
    }
}

#endif // UMBREEY_SDK_GME
