// ZCode7 - session persistence (id, title, messages, todos)
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ZCode7
{
    public class SessionMeta
    {
        public string File;
        public string Id;
        public string Title;
        public DateTime Updated;
        public bool Archived;
    }

    public static class SessionStore
    {
        public static string NewId()
        {
            return DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" +
                Guid.NewGuid().ToString("N").Substring(0, 6);
        }

        // messages: List<Dictionary<string,object>> (OpenAI format); todos: object (list) or null
        // atomic write: tmp file + replace, so a hard kill can never truncate a session file
        public static string Save(List<Dictionary<string, object>> messages, string sessionId,
            string title, List<Dictionary<string, object>> todos)
        {
            string id = string.IsNullOrEmpty(sessionId) ? NewId() : sessionId;
            string path = PathOf(id);
            // title 为空时保留磁盘上已有标题（否则重命名/自动命名会被回合结束保存覆盖回空）
            string effectiveTitle = title ?? "";
            try
            {
                if (string.IsNullOrEmpty(effectiveTitle) && File.Exists(path))
                {
                    var prev = MiniJson.ParseObject(File.ReadAllText(path, Encoding.UTF8));
                    if (prev != null && prev.ContainsKey("title"))
                    {
                        string prevTitle = prev["title"] as string;
                        if (!string.IsNullOrEmpty(prevTitle)) effectiveTitle = prevTitle;
                    }
                }
            }
            catch { }
            var root = MiniJson.Obj();
            root["id"] = id;
            root["title"] = effectiveTitle;
            root["updated"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            root["model"] = "";
            root["messages"] = messages;
            root["todos"] = todos ?? (object)MiniJson.Arr();
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, MiniJson.Serialize(root), Encoding.UTF8);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
            return id;
        }

        public static string PathOf(string sessionId)
        {
            return Path.Combine(Settings.SessionsDir, sessionId + ".json");
        }

        public static List<Dictionary<string, object>> Load(string sessionId)
        {
            string file = PathOf(sessionId);
            var root = MiniJson.ParseObject(File.ReadAllText(file, Encoding.UTF8));
            var list = MiniJson.GetArr(root, "messages");
            var outList = new List<Dictionary<string, object>>();
            if (list != null)
                foreach (object o in list)
                {
                    var d = o as Dictionary<string, object>;
                    if (d != null) outList.Add(d);
                }
            return outList;
        }

        public static List<Dictionary<string, object>> LoadTodos(string sessionId)
        {
            try
            {
                string file = PathOf(sessionId);
                var root = MiniJson.ParseObject(File.ReadAllText(file, Encoding.UTF8));
                var list = MiniJson.GetArr(root, "todos");
                var outList = new List<Dictionary<string, object>>();
                if (list != null)
                    foreach (object o in list)
                    {
                        var d = o as Dictionary<string, object>;
                        if (d != null) outList.Add(d);
                    }
                return outList;
            }
            catch { return new List<Dictionary<string, object>>(); }
        }

        public static List<SessionMeta> List()
        {
            var l = new List<SessionMeta>();
            string dir = Settings.SessionsDir;
            if (!Directory.Exists(dir)) return l;
            foreach (var f in Directory.GetFiles(dir, "*.json"))
            {
                try
                {
                    var root = MiniJson.ParseObject(File.ReadAllText(f, Encoding.UTF8));
                    l.Add(new SessionMeta
                    {
                        File = f,
                        Id = MiniJson.GetStr(root, "id", Path.GetFileNameWithoutExtension(f)),
                        Title = MiniJson.GetStr(root, "title", ""),
                        Updated = File.GetLastWriteTime(f),
                        Archived = MiniJson.GetStr(root, "archived", "") == "1"
                    });
                }
                catch
                {
                    // never silently vanish: quarantine corrupt files so they are
                    // visible on disk and can be inspected/repaired
                    try
                    {
                        var corrupt = f + "." + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".corrupt";
                        File.Move(f, corrupt);
                        ZLog.Log("session corrupt, quarantined: " + corrupt);
                    }
                    catch { }
                }
            }
            l.Sort((a, b) => b.Updated.CompareTo(a.Updated));
            return l;
        }

        public static string GetTitle(string sessionId)
        {
            try
            {
                var root = MiniJson.ParseObject(File.ReadAllText(PathOf(sessionId), Encoding.UTF8));
                return MiniJson.GetStr(root, "title", "");
            }
            catch { return ""; }
        }

        public static void SetTitle(string sessionId, string title)
        {
            try
            {
                string file = PathOf(sessionId);
                if (!File.Exists(file)) return;
                var root = MiniJson.ParseObject(File.ReadAllText(file, Encoding.UTF8));
                root["title"] = title;
                File.WriteAllText(file, MiniJson.Serialize(root), Encoding.UTF8);
            }
            catch { }
        }

        /// <summary>读取归档状态（供列表过滤）。</summary>
        public static bool IsArchived(string sessionId)
        {
            try
            {
                string path = PathOf(sessionId);
                if (!File.Exists(path)) return false;
                var root = MiniJson.ParseObject(File.ReadAllText(path, Encoding.UTF8));
                return root != null && MiniJson.GetStr(root, "archived", "") == "1";
            }
            catch { }
            return false;
        }

        /// <summary>归档状态持久化（archived=1 写入会话 JSON）。</summary>
        public static void SetArchived(string sessionId, bool archived)
        {
            try
            {
                string path = PathOf(sessionId);
                if (!File.Exists(path)) return;
                var root = MiniJson.ParseObject(File.ReadAllText(path, Encoding.UTF8));
                if (root == null) return;
                root["archived"] = archived ? "1" : "0";
                File.WriteAllText(path, MiniJson.Serialize(root), Encoding.UTF8);
            }
            catch { }
        }

        public static void Delete(string sessionId)
        {
            try { File.Delete(PathOf(sessionId)); } catch { }
        }
    }
}
