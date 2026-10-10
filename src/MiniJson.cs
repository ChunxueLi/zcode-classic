// ZCode7 - minimal JSON parser/serializer (zero dependency, net48)
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ZCode7
{
    public static class MiniJson
    {
        // ---------- Parse ----------
        public static object Parse(string s)
        {
            if (s == null) throw new ArgumentNullException("s");
            int i = 0;
            object v = ParseValue(s, ref i);
            SkipWs(s, ref i);
            if (i < s.Length) throw new Exception("JSON: trailing content at " + i);
            return v;
        }

        public static Dictionary<string, object> ParseObject(string s)
        {
            object o = Parse(s);
            return o as Dictionary<string, object>;
        }

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n')) i++;
        }

        private static object ParseValue(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) throw new Exception("JSON: unexpected end");
            char c = s[i];
            switch (c)
            {
                case '{': return ParseObj(s, ref i);
                case '[': return ParseArr(s, ref i);
                case '"': return ParseString(s, ref i);
                case 't': Expect(s, ref i, "true"); return (object)true;
                case 'f': Expect(s, ref i, "false"); return (object)false;
                case 'n': Expect(s, ref i, "null"); return null;
                default: return ParseNumber(s, ref i);
            }
        }

        private static void Expect(string s, ref int i, string lit)
        {
            if (string.CompareOrdinal(s, i, lit, 0, lit.Length) != 0) throw new Exception("JSON: bad literal at " + i);
            i += lit.Length;
        }

        private static Dictionary<string, object> ParseObj(string s, ref int i)
        {
            var d = new Dictionary<string, object>();
            i++; // {
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return d; }
            while (true)
            {
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != '"') throw new Exception("JSON: expected key at " + i);
                string k = ParseString(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new Exception("JSON: expected : at " + i);
                i++;
                d[k] = ParseValue(s, ref i);
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == '}') { i++; return d; }
                throw new Exception("JSON: bad object at " + i);
            }
        }

        private static List<object> ParseArr(string s, ref int i)
        {
            var l = new List<object>();
            i++; // [
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return l; }
            while (true)
            {
                l.Add(ParseValue(s, ref i));
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == ']') { i++; return l; }
                throw new Exception("JSON: bad array at " + i);
            }
        }

        private static string ParseString(string s, ref int i)
        {
            var sb = new StringBuilder();
            i++; // "
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c == '\\')
                {
                    if (i >= s.Length) break;
                    char e = s[i++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (i + 4 > s.Length) throw new Exception("JSON: bad \\u");
                            sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber));
                            i += 4;
                            break;
                        default: throw new Exception("JSON: bad escape \\" + e);
                    }
                }
                else sb.Append(c);
            }
            throw new Exception("JSON: unterminated string");
        }

        private static object ParseNumber(string s, ref int i)
        {
            int start = i;
            while (i < s.Length && "-+.eE0123456789".IndexOf(s[i]) >= 0) i++;
            string t = s.Substring(start, i - start);
            if (t.IndexOf('.') >= 0 || t.IndexOf('e') >= 0 || t.IndexOf('E') >= 0)
                return double.Parse(t, CultureInfo.InvariantCulture);
            long l;
            if (long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out l)) return l;
            return double.Parse(t, CultureInfo.InvariantCulture);
        }

        // ---------- Serialize ----------
        public static string Serialize(object o)
        {
            var sb = new StringBuilder();
            WriteValue(sb, o);
            return sb.ToString();
        }

        private static void WriteValue(StringBuilder sb, object o)
        {
            if (o == null) { sb.Append("null"); return; }
            if (o is string) { WriteString(sb, (string)o); return; }
            if (o is bool) { sb.Append((bool)o ? "true" : "false"); return; }
            if (o is int || o is long || o is short || o is byte) { sb.Append(Convert.ToString(o, CultureInfo.InvariantCulture)); return; }
            if (o is double) { double d = (double)o; sb.Append(d.ToString("R", CultureInfo.InvariantCulture)); return; }
            if (o is float) { sb.Append(((float)o).ToString("R", CultureInfo.InvariantCulture)); return; }
            if (o is decimal) { sb.Append(((decimal)o).ToString(CultureInfo.InvariantCulture)); return; }
            var idict = o as System.Collections.IDictionary;
            if (idict != null && !(o is System.Collections.IList))
            {
                sb.Append('{');
                bool firstD = true;
                foreach (System.Collections.DictionaryEntry kv in idict)
                {
                    if (!firstD) sb.Append(',');
                    firstD = false;
                    WriteString(sb, Convert.ToString(kv.Key, CultureInfo.InvariantCulture));
                    sb.Append(':');
                    WriteValue(sb, kv.Value);
                }
                sb.Append('}');
                return;
            }
            var dict = o as Dictionary<string, object>;
            if (dict != null)
            {
                sb.Append('{');
                bool first = true;
                foreach (var kv in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(sb, kv.Key);
                    sb.Append(':');
                    WriteValue(sb, kv.Value);
                }
                sb.Append('}');
                return;
            }
            var list = o as System.Collections.IEnumerable;
            if (list != null)
            {
                sb.Append('[');
                bool f2 = true;
                foreach (object item in list)
                {
                    if (!f2) sb.Append(',');
                    f2 = false;
                    WriteValue(sb, item);
                }
                sb.Append(']');
                return;
            }
            WriteString(sb, o.ToString());
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // ---------- helpers ----------
        public static Dictionary<string, object> Obj()
        { return new Dictionary<string, object>(); }

        public static List<object> Arr()
        { return new List<object>(); }

        public static string GetStr(Dictionary<string, object> d, string key, string def = null)
        {
            if (d == null) return def;
            object v;
            if (!d.TryGetValue(key, out v)) return def;
            return v == null ? def : v.ToString();
        }

        public static Dictionary<string, object> GetDict(Dictionary<string, object> d, string key)
        {
            if (d == null) return null;
            object v;
            if (!d.TryGetValue(key, out v)) return null;
            return v as Dictionary<string, object>;
        }

        public static List<object> GetArr(Dictionary<string, object> d, string key)
        {
            if (d == null) return null;
            object v;
            if (!d.TryGetValue(key, out v)) return null;
            return v as List<object>;
        }
    }
}
