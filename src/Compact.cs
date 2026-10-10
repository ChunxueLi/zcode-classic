// ZCode7 - context compaction, ported from ZCode core/src/compact
// policy.ts: threshold on effective context window (window - output reserve), buffer, percent
// microcompact.ts: truncate old tool results before full compaction
// prompt.ts: 8-section summarization prompt
using System;
using System.Collections.Generic;
using System.Text;

namespace ZCode7
{
    public static class Compact
    {
        public const int DefaultContextWindow = 200000;
        public const int DefaultOutputReserve = 32000;
        public const int DefaultBuffer = 13000;
        public const int DefaultThresholdPercent = 100;
        public const int MaxConsecutiveFailures = 3;

        // ---- token estimation (heuristic; ZCode uses estimateMessageTokens) ----
        // CJK chars ~1 token each; ASCII ~4 chars per token.
        public static int EstimateTokens(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            int cjk = 0, other = 0;
            foreach (char c in s)
            {
                if (c > 0x2E80) cjk++; else other++;
            }
            return cjk + (other + 3) / 4;
        }

        public static int EstimateMessagesTokens(List<Dictionary<string, object>> messages)
        {
            int total = 0;
            foreach (var m in messages)
            {
                total += EstimateTokens(MiniJson.GetStr(m, "content", ""));
                var tcs = MiniJson.GetArr(m, "tool_calls");
                if (tcs != null) total += EstimateTokens(MiniJson.Serialize(tcs));
            }
            return total;
        }

        public static int EffectiveWindow(Settings cfg)
        {
            int window = cfg.ContextWindowTokens > 0 ? cfg.ContextWindowTokens : DefaultContextWindow;
            int reserve = Math.Min(cfg.OutputReserveTokens > 0 ? cfg.OutputReserveTokens : DefaultOutputReserve, window);
            return Math.Max(0, window - reserve);
        }

        public static int ThresholdTokens(Settings cfg)
        {
            double eff = EffectiveWindow(cfg);
            double pct = cfg.ThresholdPercent > 0 ? cfg.ThresholdPercent : DefaultThresholdPercent;
            return (int)(eff * pct / 100.0);
        }

        public static bool HasEnoughMessages(List<Dictionary<string, object>> messages)
        {
            // need at least: system + 2 real messages
            return messages.Count >= 4;
        }

        // ---- microcompact: truncate old tool results (keep last 3 intact) ----
        public static int Microcompact(List<Dictionary<string, object>> messages)
        {
            var toolMsgIdx = new List<int>();
            for (int i = 0; i < messages.Count; i++)
                if (MiniJson.GetStr(messages[i], "role", "") == "tool")
                    toolMsgIdx.Add(i);
            int changed = 0;
            // keep the last 3 tool results intact
            for (int k = 0; k < toolMsgIdx.Count - 3; k++)
            {
                var m = messages[toolMsgIdx[k]];
                string c = MiniJson.GetStr(m, "content", "");
                if (c.Length > 600)
                {
                    m["content"] = c.Substring(0, 600) + "\n[earlier tool result truncated to save context]";
                    changed++;
                }
            }
            return changed;
        }

        // ---- full compaction ----
        public const string NoToolsPreamble =
            "CRITICAL: Respond with TEXT ONLY. Do NOT call any tools.\n" +
            "- Do NOT use Read, Bash, Grep, Glob, Edit, Write, or ANY other tool.\n" +
            "- You already have all the context you need in the conversation above.\n" +
            "- Tool calls will be REJECTED and will waste your only turn — you will fail the task.\n" +
            "- Your entire response must be plain text: an <analysis> block followed by a <summary> block.\n";

        private const string NoToolsTrailer =
            "\n\nREMINDER: Do NOT call tools. Respond with plain text only — an <analysis> block followed by a <summary> block.";

        private const string BaseCompactPrompt =
            "Your task is to create a detailed summary of the conversation so far, paying close attention to the user's explicit requests and your previous actions.\n" +
            "This summary should be thorough in capturing technical details, code patterns, and architectural decisions that would be essential for continuing development work without losing context.\n" +
            "Before providing your final summary, wrap your analysis in <analysis> tags to organize your thoughts and ensure you've covered all necessary points. In your analysis process:\n" +
            "1. Chronologically analyze each message and section of the conversation. For each section thoroughly identify:\n" +
            "   - The user's explicit requests and intents\n" +
            "   - Your approach to addressing the user's requests\n" +
            "   - Key decisions, technical concepts and code patterns\n" +
            "   - Specific details like: file names, full code snippets, function signatures, file edits\n" +
            "   - Errors that you ran into and how you fixed them\n" +
            "   - Pay special attention to specific user feedback that you received, especially if the user told you to do something differently.\n" +
            "   - Note any security-relevant instructions or constraints the user stated. These MUST be preserved verbatim in the summary so they continue to apply after compaction.\n" +
            "2. Double-check for technical accuracy and completeness, addressing each required element thoroughly.\n" +
            "Your summary should include the following sections:\n" +
            "1. Primary Request and Intent: Capture all of the user's explicit requests and intents in detail\n" +
            "2. Key Technical Concepts: List all important technical concepts, technologies, and frameworks discussed.\n" +
            "3. Files and Code Sections: Enumerate specific files and code sections examined, modified, or created.\n" +
            "4. Errors and fixes: List all errors that you ran into, and how you fixed them.\n" +
            "5. Problem Solving: Document problems solved and any ongoing troubleshooting efforts.\n" +
            "6. All user messages: List ALL user messages that are not tool results. Preserve any security-relevant constraints verbatim.\n" +
            "7. Pending Tasks: Outline any pending tasks that you have explicitly been asked to work on.\n" +
            "8. Current Work: Describe in detail precisely what was being worked on immediately before this summary request.\n" +
            "9. Optional Next Step: List the next step directly in line with the user's most recent explicit requests.\n" +
            "请用中文输出摘要（<analysis> 和 <summary> 两个块，标签保留英文）。";

        // Build the transcript fed to the summarizer
        public static string BuildTranscript(List<Dictionary<string, object>> messages)
        {
            var sb = new StringBuilder();
            foreach (var m in messages)
            {
                string role = MiniJson.GetStr(m, "role", "");
                if (role == "system") continue;
                string c = MiniJson.GetStr(m, "content", "");
                if (c.Length == 0)
                {
                    var tcs = MiniJson.GetArr(m, "tool_calls");
                    if (tcs != null) c = MiniJson.Serialize(tcs);
                }
                sb.Append('[').Append(role).Append("] ").Append(ToolsHelper.Trunc(c, 2000)).Append('\n');
            }
            return sb.ToString();
        }

        public static string BuildSummaryPrompt(string transcript)
        {
            return NoToolsPreamble + "\n" + BaseCompactPrompt + "\n\n<conversation>\n" + transcript + "\n</conversation>" + NoToolsTrailer;
        }

        // Extract the <summary> block from the model response
        public static string ExtractSummary(string response)
        {
            int start = response.IndexOf("<summary>");
            if (start >= 0)
            {
                int end = response.IndexOf("</summary>", start);
                if (end > start)
                    return response.Substring(start + 9, end - start - 9).Trim();
                return response.Substring(start + 9).Trim();
            }
            return response.Trim();
        }
    }
}
