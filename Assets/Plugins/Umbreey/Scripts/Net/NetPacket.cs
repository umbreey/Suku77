using System;
using System.IO;
using Google.Protobuf;
using UnityEngine;

namespace Umbreey
{
    public interface INetPacketSerializer
    {
        public byte[] SerializeHead(ushort msgId, int bodyLen);
        public void DeserializeHead(byte[] data, int offset, int length, out ushort msgId, out int bodyLen);
        public IMessage ParseMessage(ushort msgId, byte[] data, int offset, int length);
    }

    public struct NetPacket
    {
        public static INetPacketSerializer Serializer;
        public IMessage Message { get; private set; }
        public ushort MsgId { get; private set; }
        public long Length { get; private set; }
        
        internal bool Serialize(ushort msgId, IMessage msg, ref MemoryStream stream)
        {
            Length = 0;
            MsgId = msgId;
            
            try
            {
                var bodyData = msg.ToByteArray();
                var headData = Serializer.SerializeHead(msgId, bodyData.Length);
                
                stream.Position = 0;
                stream.SetLength(0);
                stream.Write(SerializeUShort((ushort)headData.Length), 0, sizeof(ushort));
                stream.Write(headData);
                stream.Write(bodyData);
                Length = stream.Length;
            
                return true;
            }
            catch (Exception e)
            {
                Log.E($"Serialize error: {e}");
            }
            
            return false;
        }
        
        internal bool Deserialize(byte[] buffer,int len, int index, out int processLen)
        {
            Length = 0;
            processLen = 0;
            
            try
            {
                ushort headLen = DeserializeUShort(buffer,index);
                Serializer.DeserializeHead(buffer, index + sizeof(ushort), headLen, out var msgId, out var bodyLen);
                
                int packetLen = sizeof(ushort) + headLen + bodyLen;
                if (len - index < packetLen) // 校验完整长度     
                    return false;
            
                var bodyOffset = index + sizeof(ushort) + headLen;
            
                MsgId = msgId;
                Message = Serializer.ParseMessage(MsgId, buffer, bodyOffset, bodyLen);
                Length = packetLen;
            
                processLen = packetLen;
                return true;
            }
            catch (Exception e)
            {
                Log.E($"Deserialize error: {e}");
            }
            
            return false;
        }
        
        internal bool Deserialize(byte[] buffer)
        {
            Length = 0;
            
            try
            {
                var headLen = DeserializeUShort(buffer,0);
                Serializer.DeserializeHead(buffer,  sizeof(ushort), headLen, out var msgId, out var bodyLen);
                
                int packetLen = sizeof(ushort) + headLen + bodyLen;
                var bodyOffset = sizeof(ushort) + headLen;
            
                MsgId = msgId;
                Message = Serializer.ParseMessage(msgId, buffer, bodyOffset, bodyLen);
                Length = packetLen;
            
                return true;
            }
            catch (Exception e)
            {
                Log.E($"Deserialize error: {e}");
            }
            
            return false;
        }
        
        static byte[] SerializeUShort(ushort num)
        {
            byte[] buf = new byte[2];
            buf[0] = (byte)((num >> 8) & 0xFF);
            buf[1] = (byte)(num & 0xFF);
            return buf;
        }
		
        static ushort DeserializeUShort(byte[] buf, int index = 0)
        {
            ushort num = BitConverter.ToUInt16(buf, index);
            byte[] bytes = BitConverter.GetBytes(num);
            
            ushort res = (ushort)((bytes[0] << 8) | bytes[1]);
            return res;
        }
    }
}