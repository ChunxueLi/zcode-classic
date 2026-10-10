// ZCode7 - markdown (subset) -> RTF for selectable/copyable assistant messages
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace ZCode7
{
    public static class Md2Rtf
    {
        // RTF: \uc1? for CJK-safe unicode escapes
        private static string Esc(string s)
        {
            var sb = new StringBuilder();
            foreach (var ch in s)
            {
                if (ch == '\\') sb.Append(@"\\");
                else if (ch == '{') sb.Append(@"\{");
                else if (ch == '}') sb.Append(@"\}");
                else if (ch < 128) sb.Append(ch);
                else sb.Append(@"\uc1\u").Append((int)ch).Append('?');
            }
            return sb.ToString();
        }

        /// <summary>Convert a markdown subset to RTF with ZCode zai-light styling.</summary>
        public static string Convert(string md)
        {
            var sb = new StringBuilder();
            sb.Append(@"{\rtf1\ansi\deff0{\fonttbl{\f0\fswiss \'ce\xa2\'d1\'c7 Microsoft YaHei UI;}}");
            sb.Append(@"\viewkind4\uc1\pard\sa60\sl240\slmult1\f0\fs22\cf0"); // 11pt = fs22, spacing

            bool inCode = false;
            var para = new StringBuilder();
            int listNo = 0;

            Action flushPara = () =>
            {
                if (para.Length == 0) { para.Length = 0; return; }
                WriteInline(sb, para.ToString());
                para.Length = 0;
                sb.Append("\\par\n");
            };

            foreach (var rawLine in (md ?? "").Replace("\r\n", "\n").Split('\n'))
            {
                var line = rawLine.TrimEnd();
                if (line.StartsWith("```"))
                {
                    flushPara();
                    inCode = !inCode;
                    if (inCode) sb.Append(@"\pard\li200\box\brdrs\brdrw10\brsp100\brdrc15\f1\fs20 "); // boxed, mono 10pt
                    else sb.Append(@"\pard\sa60\sl240\slmult1\f0\fs22 ");
                    listNo = 0;
                    continue;
                }
                if (inCode)
                {
                    sb.Append(Esc(rawLine.Length == 0 ? " " : rawLine)).Append("\\par\n");
                    continue;
                }

                // headings
                var hm = Regex.Match(line, "^(#{1,4})\\s+(.*)$");
                if (hm.Success)
                {
                    flushPara();
                    int size = 28 - hm.Groups[1].Value.Length * 2; // # -> 14pt, ## -> 12pt...
                    sb.Append("\\b\\fs").Append(size).Append(' ');
                    WriteInline(sb, hm.Groups[2].Value);
                    sb.Append("\\b0\\fs22\\par\n");
                    listNo = 0;
                    continue;
                }

                // blockquote
                if (line.StartsWith("> "))
                {
                    flushPara();
                    sb.Append(@"\pard\li200\ri200\li300\fi-120\i ");
                    WriteInline(sb, line.Substring(2));
                    sb.Append(@"\i0\pard\sa60\sl240\slmult1\par\n".Replace("\\n", "\n"));
                    listNo = 0;
                    continue;
                }

                // ordered list
                var om = Regex.Match(line, @"^(\d+)\.\s+(.*)$");
                if (om.Success)
                {
                    flushPara();
                    sb.Append("\\pard\\li340\\fi-220 ").Append(om.Groups[1].Value).Append(".\\~ ");
                    WriteInline(sb, om.Groups[2].Value);
                    sb.Append("\\par\n");
                    sb.Append("\\pard\\sa60\\sl240\\slmult1");
                    listNo = 0;
                    continue;
                }

                // bullet list
                if (line.StartsWith("- ") || line.StartsWith("* "))
                {
                    flushPara();
                    sb.Append("\\pard\\li340\\fi-220\\'95\\~ ");
                    WriteInline(sb, line.Substring(2));
                    sb.Append("\\par\n");
                    sb.Append("\\pard\\sa60\\sl240\\slmult1");
                    listNo = 0;
                    continue;
                }

                // blank line = paragraph gap
                if (line.Length == 0)
                {
                    flushPara();
                    sb.Append("\\sa120\\par\\sa60\n");
                    listNo = 0;
                    continue;
                }

                para.Append(para.Length == 0 ? line : " " + line);
            }
            flushPara();
            sb.Append("}");
            return sb.ToString();
        }

        // inline: **bold** *italic* `code` [text](url) -> keep text (url)
        private static void WriteInline(StringBuilder sb, string text)
        {
            // links -> "text (url)"
            text = Regex.Replace(text, @"\[([^\]]+)\]\(([^)\s]+)\)", "$1 ($2)");
            int i = 0;
            var seg = new StringBuilder();
            Action<string, bool, bool> emit = (s2, bold, italic) =>
            {
                if (s2.Length == 0) return;
                sb.Append(bold ? "\\b " : "").Append(italic ? "\\i " : "").Append(Esc(s2))
                  .Append(bold || italic ? "\\b0\\i0 " : "");
            };
            while (i < text.Length)
            {
                if (i + 1 < text.Length && text[i] == '*' && text[i + 1] == '*')
                {
                    emit(seg.ToString(), false, false); seg.Length = 0;
                    int end = text.IndexOf("**", i + 2);
                    if (end < 0) { seg.Append(text.Substring(i)); break; }
                    emit(text.Substring(i + 2, end - i - 2), true, false);
                    i = end + 2;
                }
                else if (text[i] == '`')
                {
                    emit(seg.ToString(), false, false); seg.Length = 0;
                    int end = text.IndexOf('`', i + 1);
                    if (end < 0) { seg.Append(text.Substring(i)); break; }
                    sb.Append(@"\f1\fs20\highlight17 ").Append(Esc(text.Substring(i + 1, end - i - 1))).Append(@"\f0\fs22 ");
                    i = end + 1;
                }
                else if (i + 1 < text.Length && text[i] == '*' && text[i + 1] != '*' && text[i + 1] != ' ')
                {
                    // italic *...*
                    int end = text.IndexOf('*', i + 1);
                    if (end > 0 && end > i + 1)
                    {
                        emit(seg.ToString(), false, false); seg.Length = 0;
                        emit(text.Substring(i + 1, end - i - 1), false, true);
                        i = end + 1;
                    }
                    else { seg.Append(text[i]); i++; }
                }
                else { seg.Append(text[i]); i++; }
            }
            emit(seg.ToString(), false, false);
        }
    }
}
