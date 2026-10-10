// ZCode7 - window-controller channel: feeds the official sidebar "任务" list.
// Frames follow packages/shared/src/zcode-protocol-v4/controller.ts (raw, no wire envelope).
using System;
using System.Collections.Generic;
using System.Linq;

namespace ZCode7
{
    /// <summary>window-controller 通道：subscribeControllerV4 两个 topic
    /// （controller/workspaces、controller/tasks-index）+ onDynamicControllerFrame 推送。</summary>
    public class WindowControllerChannel : IRpcChannelWithEvents
    {
        private readonly Agent _agent;
        private Action<object> _fire;
        private readonly object _gate = new object();
        private readonly List<CtlSub> _subs = new List<CtlSub>();
        private const string Epoch = "wc-e1";

        private class CtlSub
        {
            public string SubId;
            public string Topic;
            public bool InitialPending;
            public ulong Seq;   // 每订阅独立序列（两 topic 共享计数会造成 gap 判断档）
        }

        public WindowControllerChannel(Agent agent)
        {
            _agent = agent;
        }

        /// <summary>任务行有变化时由 V4AgentChannel 调用（create/rename/turn-done）。</summary>
        public void UpsertTask(string id, string title, string phase)
        {
            var row = TaskRow(id, title, phase);
            FireIndex(new Dictionary<string, object>
            {
                { "op", "task.upserted" },
                { "task", row }
            });
        }

        public void RemoveTask(string id)
        {
            FireIndex(new Dictionary<string, object>
            {
                { "op", "task.removed" },
                { "address", new Dictionary<string, object>
                  {
                      { "workspacePath", RpcServicesRegistry.WorkspacePath },
                      { "taskId", id }
                  }
                }
            });
        }

        public void Listen(string eventName, object arg, Action<object> fire)
        {
            if (eventName == "onDynamicControllerFrame")
            {
                _fire = fire;
                List<CtlSub> pending;
                lock (_gate)
                {
                    pending = _subs.Where(s => s.InitialPending).ToList();
                    foreach (var s in pending) s.InitialPending = false;
                }
                foreach (var sub in pending) PushSnapshot(sub, "initial");
            }
        }

        public object Call(string name, object arg)
        {
            var p = First(arg);
            switch (name)
            {
                case "listTaskList":
                {
                    string kind = GetStr(p, "kind");
                    var items = new List<object>();
                    int total = 0;
                    string scopePath = RpcServicesRegistry.WorkspacePath;
                    var pinnedIds = RpcServicesRegistry.LoadPinnedIds();
                    foreach (var m in SessionStore.List())
                    {
                        bool pinned = pinnedIds.Contains(m.Id);
                        bool archived = m.Archived;
                        // 只返回查询 scope 匹配的会话
                        bool member = kind switch
                        {
                            "pinned" => pinned && !archived,
                            "archived" => archived,
                            "active" => !archived,
                            _ => !pinned && !archived,
                        };
                        if (!member) continue;
                        total++;
                        items.Add(TaskListItem(m, LivePhase(m.Id)));
                    }
                    return new Dictionary<string, object>
                    {
                        { "items", items },
                        { "total", total },
                        { "hasMore", false }
                    };
                }
                case "subscribeControllerV4":
                {
                    string topic = GetStr(p, "topic");
                    var sub = new CtlSub
                    {
                        SubId = "wc-" + Guid.NewGuid().ToString("N").Substring(0, 8),
                        Topic = topic,
                        InitialPending = true
                    };
                    lock (_gate) _subs.Add(sub);
                    if (_fire != null)
                    {
                        lock (_gate) sub.InitialPending = false;
                        PushSnapshot(sub, "initial");
                    }
                    return new Dictionary<string, object>
                    {
                        { "ack", new Dictionary<string, object>
                          {
                              { "subscriptionId", sub.SubId },
                              { "mode", "snapshot" },
                              { "logEpoch", Epoch }
                          }
                        }
                    };
                }
                case "resyncControllerV4":
                {
                    string subId = GetStr(p, "subscriptionId");
                    CtlSub sub;
                    lock (_gate) sub = _subs.FirstOrDefault(s => s.SubId == subId);
                    if (sub != null) PushSnapshot(sub, "recovery");
                    return new Dictionary<string, object>
                    {
                        { "ack", new Dictionary<string, object>
                          { { "subscriptionId", subId }, { "mode", "snapshot" }, { "logEpoch", Epoch } }
                        }
                    };
                }
                case "unsubscribeControllerV4":
                {
                    string subId = GetStr(p, "subscriptionId");
                    lock (_gate)
                    {
                        var sub = _subs.FirstOrDefault(s => s.SubId == subId);
                        if (sub != null) _subs.Remove(sub);
                    }
                    return null;
                }
                case "mutateTask":
                    return null;
                default:
                    throw new NotSupportedException("window-controller.'" + name + "' not implemented");
            }
        }

        // ---------------- frames ----------------

        private void PushSnapshot(CtlSub sub, string deliveryKind)
        {
            lock (_gate)
            {
                sub.Seq++;
                object snapshot;
                if (sub.Topic == "controller/workspaces")
                {
                    snapshot = new Dictionary<string, object>
                    {
                        { "protocolVersion", 1 },
                        { "logEpoch", Epoch },
                        { "workspaces", new List<object>
                          {
                              new Dictionary<string, object>
                              {
                                  { "workspacePath", RpcServicesRegistry.WorkspacePath },
                                  { "sourceAvailability", "online" },
                                  { "connectionState", "online" }
                              }
                          }
                        }
                    };
                }
                else
                {
                    var tasks = new List<object>();
                    try
                    {
                        foreach (var m in SessionStore.List())
                            tasks.Add(TaskRow(m.Id, m.Title, LivePhase(m.Id)));
                    }
                    catch { }
                    snapshot = new Dictionary<string, object>
                    {
                        { "protocolVersion", 1 },
                        { "logEpoch", Epoch },
                        { "tasks", tasks }
                    };
                }
                var frame = new Dictionary<string, object>
                {
                    { "topic", sub.Topic },
                    { "subscriptionId", sub.SubId },
                    { "logEpoch", Epoch },
                    { "fromSeq", 0L },
                    { "toSeq", (long)sub.Seq },
                    { "sentAt", NowMs() },
                    { "payload", new Dictionary<string, object>
                      {
                          { "kind", "snapshot" },
                          { "snapshot", snapshot }
                      }
                    }
                };
                var fire = _fire;
                if (fire != null) fire(frame);
            }
        }

        private void FireIndex(object delta)
        {
            var fire = _fire;
            if (fire == null) return;
            List<CtlSub> targets;
            lock (_gate)
            {
                targets = _subs.Where(s => s.Topic == "controller/tasks-index").ToList();
                foreach (var sub in targets)
                {
                    // 区间语义 (fromSeq, toSeq]：fromSeq=该订阅当前水位
                    ulong from = sub.Seq;
                    sub.Seq++;
                    var frame = new Dictionary<string, object>
                    {
                        { "topic", sub.Topic },
                        { "subscriptionId", sub.SubId },
                        { "logEpoch", Epoch },
                        { "fromSeq", (long)from },
                        { "toSeq", (long)sub.Seq },
                        { "sentAt", NowMs() },
                        { "payload", new Dictionary<string, object>
                          {
                              { "kind", "deltas" },
                              { "deltas", new List<object> { delta } }
                          }
                        }
                    };
                    fire(frame);
                }
            }
        }

        // ---------------- task rows ----------------

        private string LivePhase(string id)
        {
            return id == _agent.SessionId && _agent.IsBusy ? "running" : "completedSuccess";
        }

        private static long Ms(DateTime t)
        {
            return (long)(t.ToUniversalTime() - new DateTime(1970, 1, 1)).TotalMilliseconds;
        }

        private Dictionary<string, object> TaskMeta(string id, string title, DateTime updated)
        {
            return new Dictionary<string, object>
            {
                { "taskId", id },
                { "traceId", id },
                { "title", string.IsNullOrEmpty(title) ? id : title },
                { "workspacePath", RpcServicesRegistry.WorkspacePath },
                { "createdAt", Ms(updated) },
                { "updatedAt", Ms(updated) },
                { "mode", "build" },
                { "model", _cfg != null ? _cfg.Model : "glm-5.3-flash" }
            };
        }

        private Settings _cfg;

        public void SetConfig(Settings cfg) { _cfg = cfg; }

        private Dictionary<string, object> TaskRow(string id, string title, string phase)
        {
            DateTime updated = DateTime.Now;
            try
            {
                var meta = SessionStore.List().FirstOrDefault(x => x.Id == id);
                if (meta != null) updated = meta.Updated;
            }
            catch { }
            bool running = phase == "running";
            return new Dictionary<string, object>
            {
                { "address", new Dictionary<string, object>
                  {
                      { "workspacePath", RpcServicesRegistry.WorkspacePath },
                      { "taskId", id }
                  }
                },
                { "meta", TaskMeta(id, title, updated) },
                { "membership", new Dictionary<string, object>
                  {
                      { "pinned", RpcServicesRegistry.LoadPinnedIds().Contains(id) },
                      { "archived", SessionStoreGetArchived(id) },
                      { "active", !running && !SessionStoreGetArchived(id) }
                  }
                },
                { "sourceAvailability", "online" },
                { "liveStatus", running ? "running" : "completed" },
                { "activity", new Dictionary<string, object>
                  {
                      { "phase", phase },
                      { "lastActivityAt", Ms(updated) },
                      { "hasBackgroundWork", false }
                  }
                }
            };
        }

        private static bool SessionStoreGetArchived(string id)
        {
            try { return SessionStore.IsArchived(id); } catch { return false; }
        }

        private object TaskListItem(SessionMeta m, string phase)
        {
            var meta = TaskMeta(m.Id, m.Title, m.Updated);
            bool running = phase == "running";
            meta["sourceAvailability"] = "online";
            meta["liveStatus"] = running ? "running" : "completed";
            meta["activity"] = new Dictionary<string, object>
            {
                { "phase", phase },
                { "lastActivityAt", Ms(m.Updated) },
                { "hasBackgroundWork", false }
            };
            return meta;
        }

        private static long NowMs()
        {
            return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds;
        }

        private static object First(object arg)
        {
            if (arg is List<object> l && l.Count > 0) return l[0];
            return arg;
        }

        private static string GetStr(object d, string key)
        {
            if (d is Dictionary<string, object> dict && dict.TryGetValue(key, out var v))
                return v?.ToString() ?? "";
            return "";
        }
    }
}


        
