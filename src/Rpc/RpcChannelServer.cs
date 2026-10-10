// ZCode7 - RPC channel server: dispatch (channelName, name, arg) requests to services.
// Promise requests get PromiseSuccess/PromiseError responses.
// EventListen requests register subscriptions; events fire via EventFire frames.
using System;
using System.Collections.Generic;
using System.Text;

namespace ZCode7
{
    public enum RpcRequestType : int
    {
        Promise = 100,
        PromiseCancel = 101,
        EventListen = 102,
        EventDispose = 103
    }

    public enum RpcResponseType : int
    {
        Initialize = 200,
        PromiseSuccess = 201,
        PromiseError = 202,
        PromiseErrorObj = 203,
        EventFire = 204
    }

    public class RpcRequest
    {
        public RpcRequestType Type;
        public uint Id;
        public string ChannelName;
        public string Name;
        public object Arg;
        public byte[] RawFrame;
    }

    public interface IRpcChannel
    {
        object Call(string name, object arg);
    }

    /// <summary>Channel that supports event subscriptions in addition to calls.</summary>
    public interface IRpcChannelWithEvents : IRpcChannel
    {
        /// <summary>Register an event listener. Returns a push action.</summary>
        void Listen(string eventName, object arg, Action<object> fire);
    }

    public class RpcChannelServer
    {
        private readonly Dictionary<string, IRpcChannel> _channels = new Dictionary<string, IRpcChannel>(StringComparer.Ordinal);
        private readonly Dictionary<string, IRpcChannelWithEvents> _eventChannels = new Dictionary<string, IRpcChannelWithEvents>(StringComparer.Ordinal);
        private readonly Dictionary<uint, string> _activeEventSubs = new Dictionary<uint, string>();
        private Action<byte[]> _send;
        public event Action<RpcRequest> onRequestLogged;

        public RpcChannelServer(Action<byte[]> send)
        {
            _send = send ?? throw new ArgumentNullException(nameof(send));
        }

        public void RegisterChannel(string channelName, IRpcChannel channel)
        {
            _channels[channelName] = channel;
        }

        public void RegisterChannel(string channelName, IRpcChannelWithEvents channel)
        {
            _eventChannels[channelName] = channel;
            _channels[channelName] = channel;
        }

        public void SendInitialize()
        {
            var header = RpcCodec.Serialize(new List<object> { (int)RpcResponseType.Initialize });
            var body = RpcCodec.Serialize(null);
            var frame = new byte[header.Length + body.Length];
            Array.Copy(header, frame, header.Length);
            Array.Copy(body, 0, frame, header.Length, body.Length);
            _send(new RpcFrame(RpcMessageType.Regular, 0, 0, frame).ToBytes());
        }

        /// <summary>Fire an event to a subscribed client.</summary>
        public void FireEvent(uint listenId, object data)
        {
            var headerBuf = RpcCodec.Serialize(new List<object> { (int)RpcResponseType.EventFire, listenId });
            var bodyBuf = data == null ? Array.Empty<byte>() : RpcCodec.Serialize(data);
            var frame = new byte[headerBuf.Length + bodyBuf.Length];
            Array.Copy(headerBuf, frame, headerBuf.Length);
            Array.Copy(bodyBuf, 0, frame, headerBuf.Length, bodyBuf.Length);
            _send(new RpcFrame(RpcMessageType.Regular, 0, 0, frame).ToBytes());
        }

        public void OnFrame(byte[] frameBytes)
        {
            var frame = RpcFrame.Parse(frameBytes, 0, frameBytes.Length);
            try
            {
                int pos = 0;
                var header = RpcCodec.Deserialize(frame.Data, ref pos) as List<object>;
                var body = pos < frame.Data.Length ? RpcCodec.Deserialize(frame.Data, ref pos) : null;
                if (header == null || header.Count == 0) return;

                var type = (RpcRequestType)Convert.ToInt32(header[0]);
                uint id = Convert.ToUInt32(header[1]);
                string channelName = header.Count > 2 ? Convert.ToString(header[2]) : "";
                string name = header.Count > 3 ? Convert.ToString(header[3]) : "";

                switch (type)
                {
                    case RpcRequestType.Promise:
                    {
                        onRequestLogged?.Invoke(new RpcRequest { Type = type, Id = id, ChannelName = channelName, Name = name, Arg = body });
                        DispatchPromise(id, channelName, name, body);
                        return;
                    }
                    case RpcRequestType.EventListen:
                    {
                        onRequestLogged?.Invoke(new RpcRequest { Type = type, Id = id, ChannelName = channelName, Name = name, Arg = body });
                        DispatchEventListen(id, channelName, name, body);
                        return;
                    }
                    case RpcRequestType.PromiseCancel:
                    case RpcRequestType.EventDispose:
                        _activeEventSubs.Remove(id);
                        return;
                }
            }
            catch (Exception ex)
            {
                ZLog.Log("rpc frame error: " + ex.Message);
            }
        }

        private void DispatchEventListen(uint id, string channelName, string eventName, object arg)
        {
            // 官方协议：EventListen 无 PromiseSuccess 应答（channelServer.ts L205），
            // 多余的 ack 会被客户端当事件数据分发给监听器（读到 undefined 即崩溃）
            if (_eventChannels.TryGetValue(channelName, out var ch))
            {
                _activeEventSubs[id] = channelName;
                Action<object> fire = data => FireEvent(id, data);
                ch.Listen(eventName, arg, fire);
                return;
            }
            // 无事件的普通通道：登记订阅但不推送（broadcast.onMessage 等启动订阅走这里）
            if (_channels.ContainsKey(channelName))
            {
                _activeEventSubs[id] = channelName;
            }
            else
            {
                ZLog.Log("RPC-ERR eventlisten " + channelName + "." + eventName + ": channel not found");
                SendError(id, "channel '" + channelName + "' not found");
            }
        }

        private void DispatchPromise(uint id, string channelName, string name, object arg)
        {
            // try event channels first (they also support Call)
            if (_eventChannels.TryGetValue(channelName, out var ec))
            {
                try
                {
                    var data = ec.Call(name, arg);
                    SendResponse((int)RpcResponseType.PromiseSuccess, id, data);
                }
                catch (Exception ex)
                {
                    ZLog.Log("RPC-ERR " + channelName + "." + name + ": " + ex.Message);
                    SendError(id, ex.Message);
                }
                return;
            }
            if (_channels.TryGetValue(channelName, out var ch))
            {
                try
                {
                    var data = ch.Call(name, arg);
                    SendResponse((int)RpcResponseType.PromiseSuccess, id, data);
                }
                catch (Exception ex)
                {
                    ZLog.Log("RPC-ERR " + channelName + "." + name + ": " + ex.Message);
                    SendError(id, ex.Message);
                }
                return;
            }
            ZLog.Log("RPC-ERR " + channelName + "." + name + ": channel not found");
            SendError(id, "channel '" + channelName + "' not found");
        }

        private void SendResponse(int responseType, uint id, object data)
        {
            var headerBuf = RpcCodec.Serialize(new List<object> { responseType, id });
            var bodyBuf = data == null ? Array.Empty<byte>() : RpcCodec.Serialize(data);
            var frame = new byte[headerBuf.Length + bodyBuf.Length];
            Array.Copy(headerBuf, frame, headerBuf.Length);
            Array.Copy(bodyBuf, 0, frame, headerBuf.Length, bodyBuf.Length);
            _send(new RpcFrame(RpcMessageType.Regular, 0, 0, frame).ToBytes());
        }

        private void SendError(uint id, string message)
        {
            var header = RpcCodec.Serialize(new List<object> { (int)RpcResponseType.PromiseError, id });
            var data = RpcCodec.Serialize(new Dictionary<string, object>
            {
                { "id", id },
                { "data", new Dictionary<string, object> {
                    { "message", message ?? "error" },
                    { "name", "Error" },
                    { "stack", new List<object>() }
                }}
            });
            var frame = new byte[header.Length + data.Length];
            Array.Copy(header, frame, header.Length);
            Array.Copy(data, 0, frame, header.Length, data.Length);
            _send(new RpcFrame(RpcMessageType.Regular, 0, 0, frame).ToBytes());
        }
    }
}
