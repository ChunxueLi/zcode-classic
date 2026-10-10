// ZCode7 - Zhipu Coding Plan quota monitor (api/monitor/usage/quota/limit)
using System;
using System.Drawing;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace ZCode7
{
    public class ZhipuUsage
    {
        public int Pct5h = -1;    // 5-hour rolling window, used %
        public int PctWeek = -1;  // weekly tokens, used %

        public string Summary
        {
            get
            {
                var parts = new List<string>();
                if (Pct5h >= 0) parts.Add("5h " + Pct5h + "%");
                if (PctWeek >= 0) parts.Add("周 " + PctWeek + "%");
                return string.Join(" · ", parts.ToArray());
            }
        }

        public static Color UsageColor(int pct)
        {
            if (pct >= 90) return Theme.Destructive;
            if (pct >= 80) return Theme.Warning;
            return Theme.Success;
        }

        /// <summary>Fetch quota percentages. Returns null on any failure. Official endpoint only.</summary>
        public static ZhipuUsage Fetch(string apiKey)
        {
            if (string.IsNullOrEmpty(apiKey)) return null;
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12
                    | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
                using (var client = new HttpClient(new HttpClientHandler { UseProxy = false }))
                {
                    client.Timeout = TimeSpan.FromSeconds(15);
                    var req = new HttpRequestMessage(HttpMethod.Get,
                        "https://open.bigmodel.cn/api/monitor/usage/quota/limit");
                    req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
                    var resp = client.SendAsync(req).GetAwaiter().GetResult();
                    var body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    if (!resp.IsSuccessStatusCode) return null;
                    var d = MiniJson.ParseObject(body);
                    if (d == null || !d.ContainsKey("data")) return null;
                    var data = d["data"] as Dictionary<string, object>;
                    if (data == null || !data.ContainsKey("limits")) return null;
                    var limits = data["limits"] as List<object>;
                    if (limits == null) return null;
                    var u = new ZhipuUsage();
                    foreach (var o in limits)
                    {
                        var it = o as Dictionary<string, object>;
                        if (it == null) continue;
                        string type = MiniJson.GetStr(it, "type", "");
                        long unit = GetLong(it, "unit");
                        if (type == "TOKENS_LIMIT" && unit == 3) u.Pct5h = (int)GetLong(it, "percentage");
                        if (type == "TOKENS_LIMIT" && unit == 6) u.PctWeek = (int)GetLong(it, "percentage");
                    }
                    return u;
                }
            }
            catch { return null; }
        }

        private static long GetLong(Dictionary<string, object> d, string key)
        {
            object v;
            if (!d.TryGetValue(key, out v)) return -1;
            try { return Convert.ToInt64(v); } catch { return -1; }
        }
    }
}
