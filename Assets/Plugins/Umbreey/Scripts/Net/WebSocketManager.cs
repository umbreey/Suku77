using System;
using System.IO;
using Google.Protobuf;
#if UMBREEY_UNITY_WEBSOCKET
using UnityWebSocket;
#endif

namespace Umbreey
{
    public enum DisconnectStatusCode
    {
        Unknown = 0,
        NormalClose = 1,
        AbnormalClose = 2,
        OnError = 3,
        SendFailed = 4,
    }
	
    public class WebSocketManager
    {
#if UMBREEY_UNITY_WEBSOCKET
        private static IWebSocket _client;
#endif
        
        private static string _connectIp;
        private static ushort _connectPort;
        
        public static bool IsConnecting 
        { 
            get
            {
#if UMBREEY_UNITY_WEBSOCKET
                if (_client != null)
                    return _client.ReadyState == WebSocketState.Connecting;
#endif
                return false;
            } 
        }

        public static bool IsConnected
        {
            get
            {
#if UMBREEY_UNITY_WEBSOCKET
                if (_client != null)
                    return _client.ReadyState == WebSocketState.Open;
#endif
                return false;
            }
        }
        
        private static MemoryStream _stream = new MemoryStream();
        
        public static UmbreeyAction OnConnected = new UmbreeyAction(); // 主动连接成功
        public static UmbreeyAction OnConnectFailed = new UmbreeyAction(); // 主动连接失败
        public static UmbreeyAction OnReConnected = new UmbreeyAction();// 断线重连成功
        public static UmbreeyAction OnReConnectFailed = new UmbreeyAction();// 断线重连失败
        public static UmbreeyAction<int> OnReConnectStart = new UmbreeyAction<int>();// 开始断线重连
        public static UmbreeyAction<DisconnectStatusCode> OnDisConnected = new UmbreeyAction<DisconnectStatusCode>(); // 被动断开连接, errCode
        public static UmbreeyAction<NetPacket, long> OnDispatch = new UmbreeyAction<NetPacket, long>();

        static WebSocketManager()
        {
            //TaskManager.Update(Tick);
        }
        
        public static void Connect(string ip, ushort port)
        {
#if !UMBREEY_UNITY_WEBSOCKET
            _connectIp = ip;
            _connectPort = port;
            Log.E("WebSocketManager: UnityWebSocket plugin not available. Define UMBREEY_UNITY_WEBSOCKET after importing it.");
            OnConnectFailed?.Invoke();
            return;
#else
            try
            {
                _connectIp = ip;
                _connectPort = port;
				
                if (_client is { ReadyState: WebSocketState.Connecting or WebSocketState.Open })
                {
                    DisConnect();
                }
                
                _client = new WebSocket($"{ip}:{port}");
                _client.OnOpen += Socket_OnOpen;
                _client.OnMessage += Socket_OnMessage;
                _client.OnClose += Socket_OnClose;
                _client.OnError += Socket_OnError;
                
                _client.ConnectAsync();
            }
            catch (Exception e)
            {
                Log.E(e);
            }
#endif
        }
        
        public static void DisConnect()
        {
#if UMBREEY_UNITY_WEBSOCKET
            if (_client != null)
            {
                _client.OnOpen -= Socket_OnOpen;
                _client.OnMessage -= Socket_OnMessage;
                _client.OnClose -= Socket_OnClose;
                _client.OnError -= Socket_OnError;
                _client.CloseAsync();
                _client = null;
            }
#endif
        }	
        
#if UMBREEY_UNITY_WEBSOCKET
        #region WebSocketCallBack

        private static void Socket_OnOpen(object sender, UnityWebSocket.OpenEventArgs e)
        {
            if (IsConnected)
            {
                Log.I($"{_connectIp}:{_connectPort} connected");
                OnConnected?.Invoke();
            }
            else
            {
                Log.I($"{_connectIp}:{_connectPort} connect failed");
                OnConnectFailed?.Invoke();
            }
        }

        private static void Socket_OnMessage(object sender, UnityWebSocket.MessageEventArgs e)
        {
            if (e.IsBinary)
            {
                NetPacket packet = new NetPacket();
                if (packet.Deserialize(e.RawData))
                {
                    OnDispatch?.Invoke(packet, packet.Length);
                }
            }
        }

        private static void Socket_OnClose(object sender, UnityWebSocket.CloseEventArgs e)
        {
            Log.E($"Close => Code : {e.Code}, Reason : {e.Reason}, StatusCode : {e.StatusCode}");
            DisConnect();
            var statusCode = e.Code == 65534 ? DisconnectStatusCode.Unknown : 
                (e.Code == 1000 ? DisconnectStatusCode.NormalClose : DisconnectStatusCode.AbnormalClose);
            OnDisConnected?.Invoke(statusCode);
        }

        private static void Socket_OnError(object sender, UnityWebSocket.ErrorEventArgs e)
        {
            Log.E($"Error => Exception : {e.Exception}, Message : {e.Message}");
            DisConnect();
            OnDisConnected?.Invoke(DisconnectStatusCode.OnError);
        }
		
        #endregion
#endif
        
        public static long SendPacket(ushort msgId, IMessage message)
        {
#if !UMBREEY_UNITY_WEBSOCKET
            Log.E("WebSocketManager.SendPacket: UnityWebSocket plugin not available.");
            OnDisConnected?.Invoke(DisconnectStatusCode.SendFailed);
            return 0;
#else
            try
            {
                NetPacket packet = new NetPacket();
                if (packet.Serialize(msgId, message, ref _stream))
                {
                    var sendData = new byte[packet.Length];
                    Array.Copy(_stream.GetBuffer(), sendData, sendData.Length);
                
                    _client.SendAsync(sendData);   
                    return packet.Length;
                }
            }
            catch (Exception e)
            {
                Log.E($"send failed: {e}");
            }

            DisConnect();
            OnDisConnected?.Invoke(DisconnectStatusCode.SendFailed);
            return 0;
#endif
        }
    }
}
