// ZCode7 - RPC frame (13-byte header), faithful port of packages/rpc/src/protocol.ts
// [0]type UInt8 | [1-4]id UInt32BE | [5-8]ack UInt32BE | [9-12]dataLen UInt32BE | [13..]data
using System;

namespace ZCode7
{
    public enum RpcMessageType : byte
    {
        None = 0,
        Regular = 1,
        Control = 2,
        Ack = 3,
        Disconnect = 5,
        ReplayRequest = 6,
        Pause = 7,
        Resume = 8,
        KeepAlive = 9
    }

    public class RpcFrame
    {
        public const int HeaderSize = 13;

        public RpcMessageType Type;
        public uint Id;
        public uint Ack;
        public byte[] Data;

        public RpcFrame() { Data = Array.Empty<byte>(); }

        public RpcFrame(RpcMessageType type, uint id, uint ack, byte[] data = null)
        {
            Type = type; Id = id; Ack = ack; Data = data ?? Array.Empty<byte>();
        }

        public byte[] ToBytes()
        {
            var buf = new byte[HeaderSize + Data.Length];
            buf[0] = (byte)Type;
            WriteUInt32BE(buf, 1, Id);
            WriteUInt32BE(buf, 5, Ack);
            WriteUInt32BE(buf, 9, (uint)Data.Length);
            Array.Copy(Data, 0, buf, HeaderSize, Data.Length);
            return buf;
        }

        public static RpcFrame Parse(byte[] buf, int offset, int count)
        {
            if (count < HeaderSize) throw new FormatException("frame shorter than header");
            var f = new RpcFrame
            {
                Type = (RpcMessageType)buf[offset],
                Id = ReadUInt32BE(buf, offset + 1),
                Ack = ReadUInt32BE(buf, offset + 5),
            };
            int len = (int)ReadUInt32BE(buf, offset + 9);
            f.Data = new byte[len];
            Array.Copy(buf, offset + HeaderSize, f.Data, 0, len);
            return f;
        }

        public static void WriteUInt32BE(byte[] buf, int pos, uint v)
        {
            buf[pos] = (byte)(v >> 24);
            buf[pos + 1] = (byte)(v >> 16);
            buf[pos + 2] = (byte)(v >> 8);
            buf[pos + 3] = (byte)v;
        }

        public static uint ReadUInt32BE(byte[] buf, int pos)
        {
            return (uint)(buf[pos] << 24 | buf[pos + 1] << 16 | buf[pos + 2] << 8 | buf[pos + 3]);
        }
    }
}
