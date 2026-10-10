// ZCode7 - RPC serialization codec, faithful port of packages/rpc/src/serialization.ts
// Format: [1B DataType tag][VQL length if needed][data]
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ZCode7
{
    public enum RpcDataType : byte
    {
        Undefined = 0,
        String = 1,
        Buffer = 2,
        VsBuffer = 3,
        Array = 4,
        Object = 5,   // JSON fallback
        Int = 6       // VQL-encoded integer
    }

    /// <summary>Decoded RPC value. Kind tells which DataType produced it.</summary>
    public class RpcValue
    {
        public RpcDataType Kind;
        public string Str;                 // String / Object(raw json)
        public byte[] Bytes;               // Buffer / VsBuffer
        public List<RpcValue> Arr;         // Array
        public long Int;                   // Int

        public override string ToString() { return Str; }
    }

    public static class RpcCodec
    {
        // ---------- VQL (variable-length quantity, 7 bits/group little-endian groups) ----------

        public static long ReadIntVql(byte[] buf, ref int pos)
        {
            long value = 0;
            for (int n = 0; ; n += 7)
            {
                byte next = buf[pos++];
                value |= (next & 0x7FL) << n;
                if ((next & 0x80) == 0) return value;
            }
        }

        public static void WriteIntVql(List<byte> outBuf, long value)
        {
            if (value == 0) { outBuf.Add(0x00); return; }
            // JS algorithm: little-endian 7-bit groups, all but last have high bit set
            long v = value;
            int len = 0;
            for (long t = v; t != 0; t = t >> 7) len++;
            var scratch = new byte[len];
            int i = 0;
            for (long t2 = v; t2 != 0; i++)
            {
                scratch[i] = (byte)(t2 & 0x7F);
                t2 = t2 >> 7;
                if (t2 > 0) scratch[i] |= 0x80;
            }
            for (int k = 0; k < len; k++) outBuf.Add(scratch[k]);
        }

        // ---------- serialize ----------

        public static byte[] Serialize(object data)
        {
            var outBuf = new List<byte>(256);
            Ser(outBuf, data);
            return outBuf.ToArray();
        }

        private static void Ser(List<byte> o, object data)
        {
            if (data == null)
            {
                o.Add((byte)RpcDataType.Undefined);
                return;
            }
            if (data is string str)
            {
                o.Add((byte)RpcDataType.String);
                var b = Encoding.UTF8.GetBytes(str);
                WriteIntVql(o, b.Length);
                o.AddRange(b);
                return;
            }
            if (data is byte[] bytes)
            {
                o.Add((byte)RpcDataType.Buffer);
                WriteIntVql(o, bytes.Length);
                o.AddRange(bytes);
                return;
            }
            if (data is RpcValue rv)
            {
                // passthrough pre-decoded value (preserves original kind)
                switch (rv.Kind)
                {
                    case RpcDataType.Undefined: o.Add((byte)RpcDataType.Undefined); return;
                    case RpcDataType.String: Ser(o, rv.Str); return;
                    case RpcDataType.Buffer:
                    case RpcDataType.VsBuffer:
                        o.Add((byte)rv.Kind); WriteIntVql(o, rv.Bytes.Length); o.AddRange(rv.Bytes); return;
                    case RpcDataType.Int: o.Add((byte)RpcDataType.Int); WriteIntVql(o, rv.Int); return;
                    case RpcDataType.Array:
                        o.Add((byte)RpcDataType.Array); WriteIntVql(o, rv.Arr.Count);
                        foreach (var e in rv.Arr) Ser(o, e);
                        return;
                    case RpcDataType.Object:
                        o.Add((byte)RpcDataType.Object);
                        var ob = Encoding.UTF8.GetBytes(rv.Str);
                        WriteIntVql(o, ob.Length); o.AddRange(ob);
                        return;
                }
            }
            if (data is ArraySegment<byte> seg)
            {
                o.Add((byte)RpcDataType.Buffer);
                WriteIntVql(o, seg.Count);
                for (int i = 0; i < seg.Count; i++) o.Add(seg.Array[seg.Offset + i]);
                return;
            }
            if (data is bool bv)
            {
                // booleans ride the Object(JSON) path like the TS impl (JSON.stringify(true))
                ObjSer(o, bv ? "true" : "false");
                return;
            }
            if (data is int || data is long || data is short || data is byte ||
                data is uint || data is ulong || data is ushort || data is sbyte)
            {
                o.Add((byte)RpcDataType.Int);
                WriteIntVql(o, Convert.ToInt64(data));
                return;
            }

            // arrays / dictionaries / everything else: Object(JSON fallback)
            string json;
            if (data is IEnumerable<RpcValue> rvList)
            {
                var arr = new List<object>();
                foreach (var e in rvList) arr.Add(e);
                json = NewtonsoftJson(arr);
            }
            else json = NewtonsoftJson(data);
            ObjSer(o, json);
        }

        private static void ObjSer(List<byte> o, string json)
        {
            o.Add((byte)RpcDataType.Object);
            var b = Encoding.UTF8.GetBytes(json);
            WriteIntVql(o, b.Length);
            o.AddRange(b);
        }

        // Minimal JSON writer for net48 (no System.Text.Json). Handles the shapes we emit.
        private static string NewtonsoftJson(object data)
        {
            var sb = new StringBuilder();
            WriteJson(sb, data);
            return sb.ToString();
        }

        private static void WriteJson(StringBuilder sb, object o)
        {
            if (o == null) { sb.Append("null"); return; }
            if (o is string s) { WriteJsonString(sb, s); return; }
            if (o is bool b) { sb.Append(b ? "true" : "false"); return; }
            if (o is int || o is long || o is short || o is byte || o is uint || o is ulong) { sb.Append(Convert.ToString(o, CultureInfo.InvariantCulture)); return; }
            if (o is double d) { sb.Append(d.ToString("R", CultureInfo.InvariantCulture)); return; }
            if (o is IEnumerable<object> list)
            {
                sb.Append('[');
                bool first = true;
                foreach (var e in list) { if (!first) sb.Append(','); first = false; WriteJson(sb, e); }
                sb.Append(']');
                return;
            }
            if (o is IDictionary<string, object> dict)
            {
                sb.Append('{');
                bool f = true;
                foreach (var kv in dict)
                {
                    if (!f) sb.Append(','); f = false;
                    WriteJsonString(sb, kv.Key); sb.Append(':'); WriteJson(sb, kv.Value);
                }
                sb.Append('}');
                return;
            }
            // fallback: stringify
            WriteJsonString(sb, o.ToString());
        }

        private static void WriteJsonString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (var ch in s)
            {
                switch (ch)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (ch < 0x20) sb.Append("\\u").Append(((int)ch).ToString("x4"));
                        else sb.Append(ch);
                        break;
                }
            }
            sb.Append('"');
        }

        // ---------- deserialize ----------

        public static object Deserialize(byte[] buf, ref int pos)
        {
            var tag = buf[pos++];
            switch ((RpcDataType)tag)
            {
                case RpcDataType.Undefined: return null;
                case RpcDataType.String:
                {
                    int len = (int)ReadIntVql(buf, ref pos);
                    var s = Encoding.UTF8.GetString(buf, pos, len);
                    pos += len;
                    return s;
                }
                case RpcDataType.Buffer:
                case RpcDataType.VsBuffer:
                {
                    int len = (int)ReadIntVql(buf, ref pos);
                    var b = new byte[len];
                    Array.Copy(buf, pos, b, 0, len);
                    pos += len;
                    return b;
                }
                case RpcDataType.Array:
                {
                    long len = ReadIntVql(buf, ref pos);
                    var arr = new List<object>((int)len);
                    for (long i = 0; i < len; i++) arr.Add(Deserialize(buf, ref pos));
                    return arr;
                }
                case RpcDataType.Object:
                {
                    int len = (int)ReadIntVql(buf, ref pos);
                    var json = Encoding.UTF8.GetString(buf, pos, len);
                    pos += len;
                    return MiniJson.ParseObject(json) ?? (object)new Dictionary<string, object>();
                }
                case RpcDataType.Int: return ReadIntVql(buf, ref pos);
                default: throw new FormatException("unknown rpc dataType " + tag);
            }
        }

        public static object Deserialize(byte[] buf) { int pos = 0; return Deserialize(buf, ref pos); }
    }
}
