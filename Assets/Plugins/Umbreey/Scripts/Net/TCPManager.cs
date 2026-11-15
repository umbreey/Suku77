using System;
using System.Collections;
using System.Collections.Concurrent;
using System.IO;
using Google.Protobuf;
using UnityEngine;

namespace Umbreey
{
	public class TcpConfig
	{
		public int RecvBufferSize = 1 * 1024 * 1024; // 最大的接收缓存大小 1MB
		public int KeepAliveIntervalMS = 3000; // 心跳包发送间隔
		public int ReConnectMaxCount = 5; //重连最大次数
		public INetPacketSerializer Serializer = null;
	}
	
	public static class TCPManager
	{
	#region Public Callbacks
		public static bool IsConnectingServer { get; internal set; }

		public static UmbreeyAction OnConnected = new UmbreeyAction(); // 主动连接成功
		public static UmbreeyAction OnConnectFailed = new UmbreeyAction(); // 主动连接失败
		public static UmbreeyAction OnReConnected = new UmbreeyAction();// 断线重连成功
		public static UmbreeyAction OnReConnectFailed = new UmbreeyAction();// 断线重连失败
		public static UmbreeyAction<int> OnReConnectStart = new UmbreeyAction<int>();// 开始断线重连
		public static UmbreeyAction<DisconnectStatusCode> OnDisConnected = new UmbreeyAction<DisconnectStatusCode>(); // 被动断开连接
		public static UmbreeyAction OnKeepAlive = new UmbreeyAction(); // 处理心跳包发送
		public static UmbreeyAction<NetPacket, long> OnDispatch = new UmbreeyAction<NetPacket, long>(); //分发协议
	#endregion


		private static TcpConfig _config = new TcpConfig();
		// 客户端
		private static string _connectIp;
		private static ushort _connectPort;
		private static TCPClient _tcpClient;
		private static bool _loadingScene;
		private static MemoryStream _stream = new MemoryStream();
		
		// 断线重连
        private static bool _reconnectAuto;
        private static bool _reconnectFailed;  
        private static int  _reconnectTimes;        
        
        // 消息队列
        private static ConcurrentQueue<NetPacket> _messageQueue = new ConcurrentQueue<NetPacket>();

        static TCPManager()
        {
	        TaskManager.Update(Tick);
        }

        public static void Config(TcpConfig config)
        {
	        _config = config;
	        NetPacket.Serializer = config.Serializer;
        }
        
		public static void Connect(string ip, ushort port)
		{
			_connectIp = ip;
			_connectPort = port;
			if (IsConnected())
			{
				_tcpClient.Disconnect(false);
			}
			InternalConnect(false);
		}
		
		public static void DisConnect(bool needReconnect = false)
		{
			_reconnectAuto = needReconnect;
			if (_tcpClient != null)
			{
				_tcpClient.Disconnect(needReconnect);
				_tcpClient = null;
			}
		}		
		
		public static bool IsConnected()
		{
			if (_tcpClient != null)
			{
				return _tcpClient.IsConnected;
			}
			return false;
		}	
		
        public static void StopConnect()
        {
            DisConnect();
        }
        
        public static long SendPacket(ushort msgId, IMessage message)
        {
	        if (_tcpClient != null)
	        {
		        NetPacket packet = new NetPacket();
		        if (packet.Serialize(msgId, message, ref _stream))
		        {
					_tcpClient.Send(_stream);	
					return packet.Length;
		        }
	        }
	        
            Log.E("Game Server not connected");
            return 0;
        }

        public static void SetLoadingScene(bool isLoading)
        {
	        _loadingScene = isLoading;
        }
        

        //网络回调，每1/10秒回调。如果卡机或者在后台，会跳回调
        static void Tick()
        {
	        if (_loadingScene) return;
	        
	        if (Time.frameCount % 5 == 0)
	        {
		        if (IsConnected())
		        {
			        _tcpClient.Tick();
		        }
		        else
		        {
			        CheckConnectState();
		        }
	        }
	        DoCmd();
        }
        
		static void CheckConnectState()
		{
			if (!IsConnected() && _reconnectAuto)
			{
				if (!IsConnectingServer)
				{
					if (_reconnectTimes < _config.ReConnectMaxCount)
					{
						_reconnectFailed = false;
						_reconnectTimes++;
						Log.I($"第{_reconnectTimes}次重连");
						OnReConnectStart?.Invoke(_reconnectTimes);
						InternalConnect(true);
					}
					else
					{
						if (!_reconnectFailed)
						{
							_reconnectFailed = true;
							Log.I($"重连彻底失败");
							OnReConnectFailed?.Invoke();
						}
					}
				}
			}
		}

		private static void DoCmd()
		{
			try
			{
				while (_messageQueue.TryDequeue(out NetPacket packet))
				{
					OnDispatch?.Invoke(packet, packet.Length);
				}
			}
			catch (System.Exception err)
			{
				Log.E("DoCmd Error: " + err.Message + err.StackTrace);
			}
		}

		static void InternalConnect(bool isReconnect)
		{
			if (_tcpClient == null)
			{
				_reconnectAuto = false;
				_tcpClient = new TCPClient();
				_tcpClient.SetRecvPacketCallback(OnRecvPacketCallback);
				_tcpClient.SetDisconnectCallback(OnDisconnectCallBack);
				_tcpClient.SetKeepAliveCallback(OnKeepAliveCallBack);
				_tcpClient.SetKeepAliveMS(_config.KeepAliveIntervalMS);
				_tcpClient.SetRecvBufferSize(_config.RecvBufferSize);
			}

			IsConnectingServer = true;
			_tcpClient.Connect(_connectIp, _connectPort, (connected) =>
			{
				if(connected)
				{
					_reconnectTimes = 0;
					IsConnectingServer = false;
					if(isReconnect)
						OnReConnected?.Invoke();
					else
						OnConnected?.Invoke();
				}
				else
				{
					IsConnectingServer = false;
					if(!isReconnect)
						OnConnectFailed?.Invoke();
				}
			});
		}
		
		private static void ConnectionFailed(string reason)
		{
			Log.E($"ConnectionFailed {reason}");
			DisConnect(true);
		}
		
		private static void OnDisconnectCallBack(bool reconnect)
		{
			_reconnectTimes = 0;
			_reconnectAuto = reconnect;
			OnDisConnected?.Invoke(DisconnectStatusCode.Unknown);
		}

		private static void OnKeepAliveCallBack()
		{
			OnKeepAlive?.Invoke();
		}

		private static void OnRecvPacketCallback(NetPacket packet)
		{
			_messageQueue.Enqueue(packet);
		}
		

	}
}
