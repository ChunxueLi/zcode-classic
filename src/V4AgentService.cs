// ZCode7 - v4 zcode-agent channel implementation (bridges official UI to C# Agent)
// Wire shapes follow packages/shared/src/zcode-protocol-v4 (snapshot/delta frames).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ZCode7
{
    using System.IO;
    /// <summary>附件分块上传暂存：uploadId → 临时文件；Commit 后路径供 sendText 引用。</summary>
    internal static class AttachmentStaging
    {
        private static readonly Dictionary<string, string> Paths = new Dictionary<string, string>();
        private static readonly object Gate = new object();

        public static void Begin(string uploadId)
        {
            lock (Gate)
            {
                var dir = Path.Combine(Path.GetTempPath(), "zcode7-uploads");
                Directory.CreateDirectory(dir);
                Paths[uploadId] = Path.Combine(dir, uploadId + ".bin");
                File.WriteAllText(Paths[uploadId], "");
            }
        }

        public static void Append(string uploadId, string dataBase64)
        {
            lock (Gate)
            {
                if (!Paths.TryGetValue(uploadId, out var f)) return;
                try
                {
                    var bytes = Convert.FromBase64String(dataBase64 ?? "");
                    using (var fs = new FileStream(f, FileMode.Append, FileAccess.Write))
                        fs.Write(bytes, 0, bytes.Length);
                }
                catch (FormatException) { }
            }
        }

        public static string Commit(string uploadId)
        {
            lock (Gate)
            {
                if (Paths.TryGetValue(uploadId, out var f) && File.Exists(f)) return f;
                return "";
            }
        }

        public static void Abort(string uploadId)
        {
            lock (Gate)
            {
                if (Paths.TryGetValue(uploadId, out var f) && File.Exists(f))
                    try { File.Delete(f); } catch { }
                Paths.Remove(uploadId);
            }
        }
    }

    /// <summary>zcode-agent channel: dispatches v4 RPC methods to C# Agent and
    /// pushes conversation snapshot/delta frames to the official UI.</summary>
    public class V4AgentChannel : IRpcChannelWithEvents
    {
        public event Action<string> SendTextRequested;
        public event Action StopRequested;
        public event Action NewSessionRequested;
        public event Action<string> RenameSessionRequested;
        public event Action<string> DeleteSessionRequested;
        public Func<string, bool, bool> ResolveApproval;   // interactionId, allow -> handled
        public Action<string, string, string> TaskBroadcast;   // id, title, phase -> 侧栏任务列表
        public Action<string> TaskRemoved;

        private readonly Agent _agent;
        private readonly Settings _cfg;
        private readonly Func<object> _listSessionsFn;
        private readonly Func<object> _todosFn;
        private readonly Func<string> _modelFn;
        private readonly Func<string> _sessionIdFn;
        private readonly Dictionary<string, Agent.ApprovalRequest> _approvals;
        private Action<object> _conversationFire;
        private Action<object> _sessionsIndexFire;
        private bool _idxSnapshotPending;
        private static ulong _subCounter;
        private static ulong _iaCounter;

        // ---- conversation projection state ----
        private readonly object _gate = new object();
        private readonly List<Dictionary<string, object>> _rows = new List<Dictionary<string, object>>();
        private readonly List<Dictionary<string, object>> _pendingInteractions = new List<Dictionary<string, object>>();
        private ulong _seq;
        private int _nextRowId;
        private string _turnId = "";
        private int _assistantRowId = -1;
        private int _reasoningRowId = -1;
        private int _openToolRowId = -1;
        private string _phase = "draft";
        private Dictionary<string, object> _lastErrorInfo; // 持续到下一回合开始，避免被完成帧覆盖
        private bool _snapshotPending;
        private readonly string _logEpoch = "e1";

        /// <summary>一次会话订阅 = UI 里一个 projection store。UI 只注册一次
        /// onDynamicConversationFrame EventListen（多 store 共享），所以每条帧要按
        /// 每个活跃订阅各发一份（subId/topic/seq 都是每订阅独立）。</summary>
        private class ConvSub
        {
            public string SubId;
            public string SessionId;
            public bool InitialPending;
            public ulong Seq;
        }
        private readonly List<ConvSub> _convSubs = new List<ConvSub>();

        /// <summary>sessions-index 同理可能多个 store 并存。</summary>
        private readonly List<string> _idxSubs = new List<string>();
        private ulong _idxSeq;

        public V4AgentChannel(
            Agent agent,
            Settings cfg,
            Func<object> listSessions,
            Func<object> todos,
            Func<string> model,
            Func<string> sessionId,
            Dictionary<string, Agent.ApprovalRequest> approvals)
        {
            _agent = agent;
            _cfg = cfg;
            _listSessionsFn = listSessions;
            _todosFn = todos;
            _modelFn = model;
            _sessionIdFn = sessionId;
            _approvals = approvals;
            WireAgentEvents();
        }

        private void WireAgentEvents()
        {
            // 审批请求在此登记 interactionId，pendingInteractions 随 state 帧下发
            _agent.OnApprovalNeeded += req =>
            {
                string id = "ia-" + (++_iaCounter).ToString("x8");
                lock (_gate) _approvals[id] = req;
                SafeFire(() => OnApproval(req, id));
            };
            _agent.OnThinkingDelta += t => SafeFire(() => StreamRow("reasoning", t));
            _agent.OnTextDelta += t => SafeFire(() => StreamRow("assistantText", t));
            _agent.OnToolStart += (tool, args) => SafeFire(() =>
            {
                if (tool == "Task") SubagentStart(args); else ToolStart(tool, args);
            });
            _agent.OnToolDone += (tool, result) => SafeFire(() =>
            {
                if (tool == "Task") SubagentDone(result); else ToolDone(result);
            });
            _agent.OnTurnDone += () => SafeFire(TurnDone);
            _agent.OnError += msg => SafeFire(() => TurnError(msg));
            _agent.OnTodosChanged += () => SafeFire(PushPlan);
        }

        public void Listen(string eventName, object arg, Action<object> fire)
        {
            if (eventName == "onDynamicConversationFrame")
            {
                _conversationFire = fire;
                bool fireSnapshot = false;
                lock (_gate) { if (_snapshotPending) { _snapshotPending = false; fireSnapshot = true; } }
                if (fireSnapshot) PushSnapshot();
            }
            else if (eventName == "onDynamicSessionsIndexFrame")
            {
                _sessionsIndexFire = fire;
                bool fireIdx = false;
                lock (_gate) { if (_idxSnapshotPending) { _idxSnapshotPending = false; fireIdx = true; } }
                if (fireIdx) PushIndexSnapshot();
            }
        }

        public object Call(string name, object arg)
        {
            switch (name)
            {
                case "helloConversationV4":
                    return new Dictionary<string, object>
                    {
                        { "kind", "hello" },
                        { "protocolVersion", 3 },
                        { "connectionId", "server-zcode7-" + Guid.NewGuid().ToString("N").Substring(0, 8) },
                        { "clientMode", "web-remote-replayable" },
                        { "deliveryProfile", "replayable" },
                        { "serverTime", NowMs() },
                        { "capabilities", new Dictionary<string, object>
                          {
                              { "nativeDialogs", false },
                              { "localTerminal", false },
                              { "binaryFrames", false },
                              { "compression", "none" },
                              { "workspaceHookReview", true },
                              { "independentPlanState", true },
                              { "workflowRunDeltas", true }
                          }
                        },
                        { "auth", new Dictionary<string, object>() }
                    };
                case "initializeConversationV4":
                    return null;
                case "subscribeConversationV4":
                {
                    var p = First(arg);
                    string sessionId = GetStr(p, "sessionId");
                    ZLog.Log("v4 subscribe sessionId=" + sessionId);
                    if (string.IsNullOrEmpty(sessionId)) sessionId = _sessionIdFn();
                    lock (_gate)
                    {
                        // 同 session 重复订阅复用旧订阅（UI 换 store 不换会话时常见）
                        var existing = _convSubs.Find(s => s.SessionId == sessionId);
                        if (existing == null)
                        {
                            existing = new ConvSub
                            {
                                SubId = "sub-conv-" + (++_subCounter),
                                SessionId = sessionId,
                                InitialPending = true
                            };
                            _convSubs.Add(existing);
                        }
                        ResetProjection(sessionId);
                        _snapshotPending = true;
                        if (_conversationFire != null)
                        {
                            _snapshotPending = false;
                            var sub = existing;
                            PushSnapshotTo(sub, "initial");
                        }
                        return new Dictionary<string, object>
                        {
                            { "ack", new Dictionary<string, object>
                              {
                                  { "subscriptionId", existing.SubId },
                                  { "mode", "snapshot" },
                                  { "logEpoch", _logEpoch }
                              }
                            }
                        };
                    }
                }
                case "subscribeSessionsIndexV4":
                {
                    string idxSub = "sub-idx-" + (++_subCounter);
                    lock (_gate)
                    {
                        _idxSubs.Add(idxSub);
                        _idxSnapshotPending = true;
                        if (_sessionsIndexFire != null)
                        {
                            _idxSnapshotPending = false;
                            PushIndexSnapshot();
                        }
                        return new Dictionary<string, object>
                        {
                            { "ack", new Dictionary<string, object>
                              {
                                  { "subscriptionId", idxSub },
                                  { "mode", "snapshot" },
                                  { "logEpoch", "idx-e1" }
                              }
                            }
                        };
                    }
                }
                case "resyncConversationV4":
                {
                    // recovery 应答：按请求的 subscriptionId 定向补发全量快照
                    var p = First(arg);
                    ZLog.Log("v4 conv resync params: " + MiniJson.Serialize(p));
                    string subId = GetStr(p, "subscriptionId");
                    lock (_gate)
                    {
                        var sub = _convSubs.Find(s => s.SubId == subId);
                        if (sub != null) PushSnapshotTo(sub, "recovery");
                        else ZLog.Log("v4 resync unknown sub " + subId);
                        return new Dictionary<string, object>
                        {
                            { "ack", new Dictionary<string, object>
                              { { "subscriptionId", subId }, { "mode", "snapshot" }, { "logEpoch", _logEpoch } }
                            }
                        };
                    }
                }
                case "resyncSessionsIndexV4":
                {
                    var p = First(arg);
                    ZLog.Log("v4 idx resync params: " + MiniJson.Serialize(p));
                    string subId2 = GetStr(p, "subscriptionId");
                    // recovery 飞行只收 deliveryKind=recovery 的帧（旧 gate 吞掉其它标记）
                    PushIndexSnapshotAs("recovery");
                    return new Dictionary<string, object>
                    {
                        { "ack", new Dictionary<string, object>
                          { { "subscriptionId", subId2 }, { "mode", "snapshot" }, { "logEpoch", "idx-e1" } }
                        }
                    };
                }
                case "unsubscribeConversationV4":
                {
                    var p = First(arg);
                    string subId = GetStr(p, "subscriptionId");
                    lock (_gate) _convSubs.RemoveAll(s => s.SubId == subId);
                    return new Dictionary<string, object>
                    {
                        { "ack", new Dictionary<string, object>
                          { { "subscriptionId", subId }, { "mode", "snapshot" }, { "logEpoch", _logEpoch } }
                        }
                    };
                }
                case "unsubscribeSessionsIndexV4":
                {
                    var p = First(arg);
                    string subId3 = GetStr(p, "subscriptionId");
                    lock (_gate) _idxSubs.RemoveAll(s => s == subId3);
                    return new Dictionary<string, object>
                    {
                        { "ack", new Dictionary<string, object>
                          { { "subscriptionId", subId3 }, { "mode", "snapshot" }, { "logEpoch", "idx-e1" } }
                        }
                    };
                }
                case "sendConversationCommandV4":
                    return HandleCommand(arg);
                case "attachmentBeginV4":
                {
                    var p = First(arg);
                    string uploadId = GetStr(p, "uploadId");
                    if (uploadId.Length == 0) uploadId = "up-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                    AttachmentStaging.Begin(uploadId);
                    return new Dictionary<string, object>
                    {
                        { "uploadId", uploadId },
                        { "state", "staging" },
                        { "nextChunkIndex", 0 }
                    };
                }
                case "attachmentChunkV4":
                {
                    var p = First(arg);
                    string uploadId = GetStr(p, "uploadId");
                    int idx = 0;
                    if (p is Dictionary<string, object> pc && pc.TryGetValue("chunkIndex", out var civ) && civ != null)
                        idx = Convert.ToInt32(civ);
                    string data = GetStr(p, "dataBase64");
                    AttachmentStaging.Append(uploadId, data);
                    return new Dictionary<string, object>
                    {
                        { "uploadId", uploadId },
                        { "nextChunkIndex", idx + 1 }
                    };
                }
                case "attachmentCommitV4":
                {
                    var p = First(arg);
                    string uploadId = GetStr(p, "uploadId");
                    string refPath = AttachmentStaging.Commit(uploadId);
                    return new Dictionary<string, object> { { "ref", refPath } };
                }
                case "attachmentAbortV4":
                {
                    var p = First(arg);
                    AttachmentStaging.Abort(GetStr(p, "uploadId"));
                    return new Dictionary<string, object>();
                }
                case "conversationRowsRangeV4":
                    return new Dictionary<string, object>
                    {
                        { "rows", RowsWindow() },
                        { "totalCount", RowsTotal() },
                        { "firstRowId", FirstRowId() }
                    };
                case "queryConversationCommandsV4":
                    return new Dictionary<string, object> { { "commands", new List<object>() } };
                case "conversationWorkflowRunsV4":
                    return new Dictionary<string, object> { { "runs", new List<object>() } };
                case "conversationPlansV4":
                    return new Dictionary<string, object> { { "plans", new List<object>() } };
                case "getTaskSessionFilePath":
                    return null;
                case "getTaskNativeSessionLogFile":
                    return null;
                case "listSessions":
                    return _listSessionsFn();
                case "getSkillReferenceCatalog":
                {
                    // $ 技能引用目录：真实技能清单（应用本地 + 官方 ~/.zcode/skills 合并）
                    var list = new List<object>();
                    try
                    {
                        foreach (var sk in Skills.LoadAll())
                        {
                            if (!sk.Enabled) continue;
                            list.Add(new Dictionary<string, object>
                            {
                                { "id", sk.Folder },
                                { "name", sk.Name },
                                { "description", sk.Description ?? "" },
                                { "path", sk.Path ?? "" },
                                { "scope", "user" },
                                { "enabled", true }
                            });
                        }
                    }
                    catch { }
                    return new Dictionary<string, object>
                    {
                        { "authority", "workspace" },
                        { "skills", list }
                    };
                }
                case "listMcpServerStatuses":
                    return new Dictionary<string, object> { { "statuses", new Dictionary<string, object>() } };
                case "getAppUsageStats":
                case "getTaskTokenUsage":
                    return new Dictionary<string, object>();
                default:
                    throw new NotSupportedException("v4 method '" + name + "' not implemented");
            }
        }

        // ---------------- projection: agent events -> v4 frames ----------------

        private void SafeFire(Action a)
        {
            try { if (_conversationFire != null) a(); } catch (Exception ex) { ZLog.Log("v4 frame: " + ex.Message); }
        }

        private string Sid()
        {
            var s = _sessionIdFn();
            return string.IsNullOrEmpty(s) ? "draft" : s;
        }

        // 当前投影所属会话：同会话重订阅时保留内存活投影（StartTurn 的行还在流式）
        private string _projSession = "";

        private void ResetProjection(string sessionId)
        {
            lock (_gate)
            {
                if ((sessionId == _projSession && _rows.Count > 0) || _agent.IsBusy)
                {
                    // 活投影继续用：不清 rows/行指针（会话快照会带上它们）。
                    // 回合进行中切过来的订阅也看活投影，避免首屏空白。
                    _projSession = sessionId;
                    return;
                }
                _projSession = sessionId;
                _rows.Clear();
                _pendingInteractions.Clear();
                _seq = 0;
                _nextRowId = 0;
                // 流式行指针一并重置：否则行 delta 引用 UI 未知 rowId → content fault
                _assistantRowId = _reasoningRowId = _openToolRowId = -1;
                _turnId = "";
                // 有历史消息的会话映射成只读行（userInput/assistantText），保证 resume 可见
                try
                {
                    if (!string.IsNullOrEmpty(sessionId))
                    {
                        foreach (var m in SessionStore.Load(sessionId))
                        {
                            string role = GetStr(m, "role");
                            string content = GetStr(m, "content");
                            if (content.Length == 0) continue;
                            if (role == "user" || role == "assistant")
                            {
                                var row = BaseRow(role == "user" ? "userInput" : "assistantText");
                                row["text"] = content;
                                if (role == "user") row["origin"] = "realUser";   // 必填枚举
                                else row["state"] = "complete";
                                _rows.Add(row);
                            }
                        }
                    }
                    _phase = _agent.IsBusy ? "running" : "completedSuccess";
                }
                catch { _phase = "draft"; }
            }
        }

        private void PushSnapshot()
        {
            lock (_gate)
            {
                // 临时诊断：把实际下发的快照 JSON 落日志（离线 zod 校验用）
                foreach (var sub in _convSubs) PushSnapshotTo(sub, sub.InitialPending ? "initial" : "online");
            }
        }

        /// <summary>向单个订阅发快照帧（deliveryKind: initial/online/recovery）。</summary>
        private void PushSnapshotTo(ConvSub sub, string deliveryKind)
        {
            sub.Seq++;
            var snap = SnapshotDict(sub.Seq, sub.SessionId);
            var inner = new Dictionary<string, object>
            {
                { "topic", "conversation/" + sub.SessionId },
                { "subscriptionId", sub.SubId },
                { "fromSeq", 0L },
                { "toSeq", (long)sub.Seq },
                { "sentAt", NowMs() },
                { "payload", new Dictionary<string, object>
                  {
                      { "kind", "snapshot" },
                      { "snapshot", snap }
                  }
                }
            };
            if (deliveryKind == "initial") sub.InitialPending = false;
            FireWire(inner, deliveryKind, _conversationFire);
        }

        /// <summary>向所有活跃订阅广播 delta（帧按订阅各自封装）。</summary>
        private void FireAll(object op)
        {
            lock (_gate)
            {
                foreach (var sub in _convSubs)
                {
                    // 帧区间语义 (fromSeq, toSeq]：fromSeq=已应用水位，toSeq=新水位
                    ulong from = sub.Seq;
                    sub.Seq++;
                    var inner = new Dictionary<string, object>
                    {
                        { "topic", "conversation/" + sub.SessionId },
                        { "subscriptionId", sub.SubId },
                        { "fromSeq", (long)from },
                        { "toSeq", (long)sub.Seq },
                        { "sentAt", NowMs() },
                        { "payload", new Dictionary<string, object>
                          {
                              { "kind", "deltas" },
                              { "deltas", new List<object> { op } }
                          }
                        }
                    };
                    FireWire(inner, "online", _conversationFire);
                }
            }
        }

        // ---------------- sessions index frames ----------------

        private static Dictionary<string, object> SessionSummary(string id, string title, DateTime updated, string phase)
        {
            return new Dictionary<string, object>
            {
                { "sessionId", id },
                { "workspaceId", RpcServicesRegistry.ConversationWorkspacePath },
                { "title", string.IsNullOrEmpty(title) ? id : title },
                { "titleSource", "default" },
                { "phase", phase },
                { "sessionEnded", phase != "running" },
                { "hasBackgroundWork", false },
                { "lastActivityAt", (long)(updated.ToUniversalTime() - new DateTime(1970, 1, 1)).TotalMilliseconds },
                { "createdAt", (long)(updated.ToUniversalTime() - new DateTime(1970, 1, 1)).TotalMilliseconds }
            };
        }

        // 官方索引 topic = sessions-index/<workspacePath|identity>（agentSessionsIndexTransport L83）
        private static string IndexTopic()
        {
            return "sessions-index/" + RpcServicesRegistry.WorkspacePath;
        }

        private void FireIndex(object payload)
        {
            // delta 帧：序列号必须连续（快照 toSeq=N 后下一条 delta 从 N+1 起，
            // 差 1 即被 store 判断档 → 强制重订阅循环）
            var fire = _sessionsIndexFire;
            if (fire == null) return;
            lock (_gate)
            {
                ulong idxFrom = _idxSeq;
                _idxSeq++;
                foreach (var subId in _idxSubs.ToArray())
                {
                    var inner = new Dictionary<string, object>
                    {
                        { "topic", IndexTopic() },
                        { "subscriptionId", subId },
                        { "fromSeq", (long)idxFrom },
                        { "toSeq", (long)_idxSeq },
                        { "sentAt", NowMs() },
                        { "payload", new Dictionary<string, object>
                          {
                              { "kind", "deltas" },
                              { "deltas", new List<object> { payload } }
                          }
                        }
                    };
                    FireWire(inner, "online", fire);
                }
            }
        }

        private void PushIndexSnapshot()
        {
            // 新订阅的首帧标 initial；重订阅（resync 之外）也用 initial
            PushIndexSnapshotAs("initial");
        }

        private void PushIndexSnapshotAs(string deliveryKind)
        {
            _idxSeq++;
            var sessions = new List<object>();
            try
            {
                foreach (var m in SessionStore.List())
                    sessions.Add(SessionSummary(m.Id, m.Title, m.Updated, "completedSuccess"));
            }
            catch { }
            var fire = _sessionsIndexFire;
            if (fire == null) return;
            foreach (var subId in _idxSubs.ToArray())
            {
                var inner = new Dictionary<string, object>
                {
                    { "topic", IndexTopic() },
                    { "subscriptionId", subId },
                    { "fromSeq", 0L },
                    { "toSeq", 1L },
                    { "sentAt", NowMs() },
                    { "payload", new Dictionary<string, object>
                      {
                          { "kind", "snapshot" },
                          { "snapshot", new Dictionary<string, object>
                            {
                                { "protocolVersion", 1 },
                                { "workspaceId", RpcServicesRegistry.WorkspacePath },
                                { "logEpoch", "idx-e1" },
                                { "sessions", sessions }
                            }
                          }
                        }
                    }
                };
                FireWire(inner, deliveryKind, fire);
            }
        }

        private void PushIndexUpsert(string id, string title, string phase)
        {
            try
            {
                var meta = SessionStore.List().FirstOrDefault(x => x.Id == id);
                FireIndex(new Dictionary<string, object>
                {
                    { "op", "session.upserted" },
                    { "session", SessionSummary(id, title, meta != null ? meta.Updated : DateTime.Now, phase) }
                });
            }
            catch (Exception ex) { ZLog.Log("idx upsert: " + ex.Message); }
        }

        private Dictionary<string, object> BaseRow(string kind)
        {
            return new Dictionary<string, object>
            {
                { "rowId", ++_nextRowId },
                { "turnId", _turnId },
                { "createdAt", NowMs() },
                { "createdAtSeq", (long)_seq + 1 },
                { "kind", kind }
            };
        }

        private void AppendRow(Dictionary<string, object> row)
        {
            _rows.Add(row);
            FireAll(new Dictionary<string, object> { { "op", "row.appended" }, { "row", row } });
        }

        private void UpsertRow(Dictionary<string, object> row)
        {
            FireAll(new Dictionary<string, object> { { "op", "row.upserted" }, { "row", row } });
        }

        private void FireStatePatch(Dictionary<string, object> patch)
        {
            FireAll(new Dictionary<string, object> { { "op", "state.updated" }, { "patch", patch } });
        }

        /// <summary>真实上下文用量：驱动 composer 用量显示与 /compact 入口（contextWindow=null 时面板不渲染）。</summary>
        private Dictionary<string, object> BuildUsageDict()
        {
            int used = 0;
            try { used = Compact.EstimateMessagesTokens(_agent.Messages); } catch { }
            int max = 200000, threshold = -1;
            try
            {
                max = _cfg != null ? _cfg.ContextWindowTokens : max;
                threshold = _cfg != null ? max * _cfg.ThresholdPercent / 100 : -1;
            }
            catch { }
            return new Dictionary<string, object>
            {
                { "contextWindow", new Dictionary<string, object>
                  {
                      { "usedTokens", used },
                      { "maxTokens", max },
                      { "autoCompactThresholdTokens", threshold > 0 ? (object)threshold : null }
                  }
                },
                { "cumulative", new Dictionary<string, object>
                  {
                      { "inputTokens", 0 }, { "outputTokens", 0 },
                      { "cacheReadTokens", 0 }, { "cacheWriteTokens", 0 }
                  }
                }
            };
        }

        private void FireUsage()
        {
            FireStatePatch(new Dictionary<string, object> { { "usage", BuildUsageDict() } });
        }

        private Dictionary<string, object> ControlDict()
        {
            bool running = _phase == "running";
            bool ended = _phase == "completedSuccess" || _phase == "completedInterrupted";
            var c = new Dictionary<string, object>
            {
                { "phase", _phase },
                { "sessionEnded", ended },
                { "canStop", running },
                { "stopState", running ? "stoppable" : "idle" },
                { "stopTargetKind", "unknown" },
                { "activeWorks", running
                    ? (object)new List<object> { new Dictionary<string, object>
                        { { "kind", "primaryTurn" }, { "startedAt", NowMs() } } }
                    : new List<object>() },
                { "lastError", _lastErrorInfo == null ? null : (object)_lastErrorInfo },
                { "apiRetry", null }
            };
            return c;
        }

        /// <summary>Todo 工具的待办状态 → 官方 plan 状态帧（待办面板数据源）。</summary>
        private void PushPlan()
        {
            lock (_gate)
            {
                var items = new List<object>();
                int i = 0;
                foreach (var t in _agent.Todos ?? new List<Dictionary<string, object>>())
                {
                    string status = GetStr(t, "status");
                    if (status == "in_progress") status = "inProgress";
                    if (status != "pending" && status != "inProgress" && status != "completed") status = "pending";
                    items.Add(new Dictionary<string, object>
                    {
                        { "id", "pi-" + (++i) },
                        { "content", GetStr(t, "content") },
                        { "status", status }
                    });
                }
                FireAll(new Dictionary<string, object>
                {
                    { "op", "state.updated" },
                    { "patch", new Dictionary<string, object>
                      {
                          { "plan", new Dictionary<string, object>
                            {
                                { "items", items },
                                { "updatedAt", NowMs() }
                            }
                          }
                      }
                    }
                });
            }
        }

        private void PatchControl(string phase)
        {
            _phase = phase;
            FireStatePatch(new Dictionary<string, object>
            {
                { "control", ControlDict() },
                { "pendingInteractions", PendingList() }
            });
        }

        private List<object> PendingList()
        {
            var l = new List<object>();
            foreach (var p in _pendingInteractions) l.Add(p);
            return l;
        }

        private object RowsWindow()
        {
            var l = new List<object>();
            foreach (var r in _rows) l.Add(r);
            return l;
        }
        private int RowsTotal() { return _rows.Count; }
        private object FirstRowId() { return _rows.Count > 0 ? (object)_rows[0]["rowId"] : 0; }

        private Dictionary<string, object> SnapshotDict(ulong subSeq, string subSession)
        {
            string model = _cfg != null ? _cfg.Model : "glm-5.3-flash";
            return new Dictionary<string, object>
            {
                { "protocolVersion", 1 },
                { "sessionId", subSession },
                { "logEpoch", _logEpoch },
                { "seq", (long)subSeq },
                { "revision", 1 },
                { "control", ControlDict() },
                { "availability", new Dictionary<string, object>
                  {
                      { "fork", new Dictionary<string, object> { { "allowed", true } } },
                      { "compact", new Dictionary<string, object> { { "allowed", true } } },
                      { "switchModelConfig", new Dictionary<string, object> { { "allowed", true } } },
                      { "setFollowupMode", new Dictionary<string, object> { { "allowed", true } } },
                      { "queueEdit", new Dictionary<string, object> { { "allowed", true } } },
                      { "sendQueuedNow", new Dictionary<string, object> { { "allowed", true } } },
                      { "pauseGoal", new Dictionary<string, object> { { "allowed", true } } },
                      { "resumeGoal", new Dictionary<string, object> { { "allowed", true } } }
                  }
                },
                { "inputRouting", new Dictionary<string, object> { { "mode", "startNow" } } },
                { "meta", new Dictionary<string, object>
                  { { "title", _agent.Title ?? "" }, { "titleSource", "default" } } },
                { "config", new Dictionary<string, object>
                  {
                      { "provider", "builtin:bigmodel-coding-plan" },
                      { "model", model },
                      { "thought", "high" },
                      { "thoughtLevels", new List<object> { "off", "low", "medium", "high", "max" } },
                      { "followupMode", "queue" },
                      { "mode", "build" }
                  }
                },
                { "modelTransition", null },
                { "usage", BuildUsageDict() },
                { "queue", new Dictionary<string, object>
                  { { "items", new List<object>() }, { "autoDrain", true } } },
                { "pendingInteractions", PendingList() },
                { "pendingCommands", new List<object>() },
                { "backgroundWorks", new List<object>() },
                { "goal", null },
                { "plan", null },
                { "workspaceHookAdmission", null },
                { "rows", new Dictionary<string, object>
                  {
                      { "window", RowsWindow() },
                      { "totalCount", RowsTotal() },
                      { "firstRowId", FirstRowId() }
                  }
                }
            };
        }

        // 每 route（topic+subId）单调递增的物理帧序号：assembler 有 ordinal 墓碑，
        // 必须 >=1 且递增，重复/回退的帧会被当迟到残片静默丢弃
        private static readonly Dictionary<string, ulong> _ordinalByRoute = new Dictionary<string, ulong>();

        private static void FireWire(Dictionary<string, object> inner, string deliveryKind, Action<object> fire)
        {
            if (fire == null) return;
            string route = inner["topic"] + "\u0000" + inner["subscriptionId"];
            ulong ordinal;
            lock (_ordinalByRoute)
            {
                _ordinalByRoute.TryGetValue(route, out ordinal);
                ordinal++;
                _ordinalByRoute[route] = ordinal;
            }
            var wire = new Dictionary<string, object>
            {
                { "wireVersion", 3 },
                { "kind", "complete" },
                { "deliveryKind", deliveryKind },
                { "logicalFrameId", Guid.NewGuid().ToString("N") },
                { "logicalFrameOrdinal", (long)ordinal },
                { "topic", inner["topic"] },
                { "subscriptionId", inner["subscriptionId"] },
                { "frame", inner }
            };
            try { ZLog.Log("WIREFIRE " + MiniJson.Serialize(wire)); } catch { }
            fire(wire);
        }

        private void StartTurn(string text)
        {
            lock (_gate)
            {
                _turnId = "turn-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                _assistantRowId = _reasoningRowId = _openToolRowId = -1;
                _lastErrorInfo = null;   // 新回合开始，清除上一轮错误横幅
                _phase = "running";
                _projSession = Sid();   // 回合归属当前会话：同会话重订阅保留活投影
                var userRow = BaseRow("userInput");
                userRow["text"] = text;
                userRow["origin"] = "realUser";   // userInputRowSchema 必填枚举
                AppendRow(userRow);
                var th = BaseRow("turnHeader");
                th["origin"] = "userInput";
                th["state"] = "running";
                th["startedAt"] = NowMs();
                AppendRow(th);
            }
            PatchControl("running");
        }

        private Dictionary<string, object> FindRow(int rowId)
        {
            foreach (var r in _rows) if ((int)r["rowId"] == rowId) return r;
            return null;
        }

        private void StreamRow(string kind, string append)
        {
            if (string.IsNullOrEmpty(append)) return;
            lock (_gate)
            {
                if (kind == "assistantText")
                {
                    if (_assistantRowId < 0)
                    {
                        var row = BaseRow("assistantText");
                        row["assistantResponseId"] = "resp-" + _turnId;
                        row["text"] = append;
                        row["state"] = "streaming";
                        _assistantRowId = (int)row["rowId"];
                        AppendRow(row);
                    }
                    else
                    {
                        var row = FindRow(_assistantRowId);
                        if (row != null) row["text"] = GetStr(row, "text") + append;
                        FireAll(new Dictionary<string, object>
                        {
                            { "op", "row.delta" },
                            { "rowId", _assistantRowId },
                            { "path", "text" },
                            { "append", append }
                        });
                    }
                }
                else // reasoning
                {
                    if (_reasoningRowId < 0)
                    {
                        var row = BaseRow("reasoning");
                        row["assistantResponseId"] = "resp-" + _turnId;
                        row["text"] = append;
                        row["state"] = "streaming";
                        _reasoningRowId = (int)row["rowId"];
                        AppendRow(row);
                    }
                    else
                    {
                        var row = FindRow(_reasoningRowId);
                        if (row != null) row["text"] = GetStr(row, "text") + append;
                        FireAll(new Dictionary<string, object>
                        {
                            { "op", "row.delta" },
                            { "rowId", _reasoningRowId },
                            { "path", "text" },
                            { "append", append }
                        });
                    }
                }
            }
        }

        private void ToolStart(string tool, string argsJson)
        {
            lock (_gate)
            {
                var row = BaseRow("toolCall");
                row["assistantResponseId"] = "resp-" + _turnId;
                row["toolCallId"] = "tc-" + row["rowId"];
                row["toolName"] = tool;
                row["status"] = "running";
                row["inputText"] = argsJson ?? "";
                _openToolRowId = (int)row["rowId"];
                AppendRow(row);
            }
        }

        private void ToolDone(string result)
        {
            lock (_gate)
            {
                if (_openToolRowId < 0) return;
                var row = FindRow(_openToolRowId);
                if (row == null) return;
                row["status"] = "success";
                UpsertRow(row);
                _openToolRowId = -1;
            }
        }

        private void SubagentStart(string argsJson)
        {
            lock (_gate)
            {
                var row = BaseRow("subagent");
                row["assistantResponseId"] = "resp-" + _turnId;
                row["subagentType"] = "general-purpose";
                row["status"] = "running";
                row["summaryText"] = "子代理运行中…";
                _openToolRowId = (int)row["rowId"];
                AppendRow(row);
            }
        }

        private void SubagentDone(string result)
        {
            lock (_gate)
            {
                if (_openToolRowId < 0) return;
                var row = FindRow(_openToolRowId);
                if (row == null) return;
                row["status"] = "success";
                string summary = result ?? "";
                if (summary.Length > 300) summary = summary.Substring(0, 300) + "…";
                row["summaryText"] = summary;
                UpsertRow(row);
                _openToolRowId = -1;
            }
        }

        private void OnApproval(Agent.ApprovalRequest req, string interactionId)
        {
            lock (_gate)
            {
                var payload = new Dictionary<string, object>
                {
                    { "kind", "permission" },
                    { "toolCallId", "tc-" + _openToolRowId },
                    { "toolName", req.ToolName ?? "" },
                    { "summary", req.ToolName ?? "" },
                    { "detail", req.ArgsJson ?? "" },
                    { "options", new List<object>
                      {
                          new Dictionary<string, object> { { "optionId", "allow" }, { "label", "允许" }, { "kind", "allowOnce" } },
                          new Dictionary<string, object> { { "optionId", "deny" }, { "label", "拒绝" }, { "kind", "deny" } }
                      }
                    }
                };
                var interaction = new Dictionary<string, object>
                {
                    { "interactionId", interactionId },
                    { "kind", "permission" },
                    { "anchorRowId", _openToolRowId > 0 ? (object)_openToolRowId : null },
                    { "createdAt", NowMs() },
                    { "payload", payload }
                };
                _pendingInteractions.Add(interaction);
                // 仅 toolCall 行支持 pendingApproval/approvalInteractionId
                // （subagent 行的 status 枚举没有这些语义，改了会被判内容违规）
                if (_openToolRowId > 0)
                {
                    var row = FindRow(_openToolRowId);
                    if (row != null && GetStr(row, "kind") == "toolCall")
                    {
                        row["status"] = "pendingApproval";
                        row["approvalInteractionId"] = interactionId;
                        UpsertRow(row);
                    }
                }
            }
            FireStatePatch(new Dictionary<string, object> { { "pendingInteractions", PendingList() } });
        }

        private void TurnDone()
        {
            try { FireUsage(); } catch { }
            lock (_gate)
            {
                foreach (var rid in new[] { _assistantRowId, _reasoningRowId })
                {
                    if (rid < 0) continue;
                    var row = FindRow(rid);
                    if (row != null && GetStr(row, "state") == "streaming")
                    {
                        row["state"] = "complete";
                        UpsertRow(row);
                    }
                }
                // turnHeader 收尾：state running → completedSuccess，否则"工作中"计时不停
                var th = _rows.LastOrDefault(r => GetStr(r, "kind") == "turnHeader");
                if (th != null && GetStr(th, "state") == "running")
                {
                    th["state"] = "completedSuccess";
                    th["endedAt"] = NowMs();
                    UpsertRow(th);
                }
                _openToolRowId = -1;
            }
            PatchControl("completedSuccess");
            try
            {
                PushIndexUpsert(_agent.SessionId, _agent.Title, "completedSuccess");
                TaskBroadcast?.Invoke(_agent.SessionId, _agent.Title, "completedSuccess");
            }
            catch { }
        }

        private void TurnError(string message)
        {
            lock (_gate)
            {
                _lastErrorInfo = new Dictionary<string, object>
                {
                    { "code", "agent" },
                    { "message", message ?? "" },
                    // 官方 sessionErrorInfoSchema 必填：recoverable/at/source，缺了整块被 zod 丢弃
                    { "recoverable", true },
                    { "at", NowMs() },
                    { "source", "runtime" }
                };
                var c = ControlDict();
                c["phase"] = "error";
                c["lastError"] = _lastErrorInfo;
                _phase = "error";
                var th = _rows.LastOrDefault(r => GetStr(r, "kind") == "turnHeader");
                if (th != null && GetStr(th, "state") == "running")
                {
                    th["state"] = "failed";
                    th["endedAt"] = NowMs();
                    UpsertRow(th);
                }
                FireStatePatch(new Dictionary<string, object>
                {
                    { "control", c },
                    { "pendingInteractions", PendingList() }
                });
            }
        }

        // ---------------- commands ----------------

        /// <summary>官方 composer 的模式/模型/思考选择映射到本地配置。</summary>
        private void ApplyMode(Dictionary<string, object> payload)
        {
            try
            {
                var src = payload;
                if (payload.TryGetValue("firstInput", out var fi) && fi is Dictionary<string, object> fd)
                {
                    // firstInput 与 config 同构，任取其一（firstInput 优先级更高）
                    src = fd;
                }
                string mode = GetStr(src, "mode");
                if (mode.Length == 0 && payload.TryGetValue("config", out var c) && c is Dictionary<string, object> cd)
                {
                    mode = GetStr(cd, "mode");
                    if (!src.ContainsKey("modelSelection")) src = cd;
                }
                if (mode == "yolo" || mode == "build" || mode == "auto" || mode == "autoEdit" || mode == "edit")
                {
                    _cfg.PermissionMode = mode;
                }
                if (src.TryGetValue("modelSelection", out var ms) && ms is Dictionary<string, object> md)
                {
                    string modelId = GetStr(md, "modelId");
                    if (modelId.Length > 0 && modelId != _cfg.Model)
                    {
                        _cfg.Model = modelId;
                        ZLog.Log("v4 model -> " + modelId);
                    }
                    if (md.TryGetValue("options", out var op) && op is Dictionary<string, object> od)
                    {
                        string level = GetStr(od, "reasoningLevel");
                        if (level.Length > 0)
                        {
                            _cfg.ThinkingLevel = level;
                            _cfg.ThinkingEnabled = level != "off";
                        }
                    }
                }
                try { _cfg.Save(); } catch { }
            }
            catch { }
        }

        private object HandleCommand(object arg)
        {
            // 线上形状：params = { envelope: { commandId, type, payload, ... }, connection? }
            var outer = First(arg) as Dictionary<string, object>;
            var env = outer != null && outer.TryGetValue("envelope", out var eObj)
                && eObj is Dictionary<string, object> ed
                ? ed : outer;
            if (env == null) return Ack("?", "failed", "bad envelope");
            string commandId = GetStr(env, "commandId");
            string type = GetStr(env, "type");
            ZLog.Log("v4 cmd " + type + " id=" + commandId);
            var payload = env.TryGetValue("payload", out var pl) && pl is Dictionary<string, object> pd
                ? pd : new Dictionary<string, object>();

            switch (type)
            {
                case "createSession":
                {
                    // 幂等收敛：回合已在跑时 UI 的预热 createSession 与首发 createSession
                    // 竞争，必须让两者拿到同一 sessionId，否则 UI 会把活会话当冗余删掉
                    bool turnLive = _agent.IsBusy || _projSession == _agent.SessionId && _rows.Count > 0;
                    if (turnLive && !string.IsNullOrEmpty(_agent.SessionId))
                    {
                        ZLog.Log("v4 createSession idempotent -> " + _agent.SessionId);
                        return AckOk(commandId, new Dictionary<string, object>
                        {
                            { "type", "createSession" },
                            { "sessionId", _agent.SessionId }
                        });
                    }
                    NewSessionRequested?.Invoke();
                    // Agent.NewSession 只做重置（原生流程首次保存才落 id）；v4 的
                    // createSession ACK 必须立刻带真实 sessionId，否则 UI 视为失败弃单
                    if (string.IsNullOrEmpty(_agent.SessionId))
                        _agent.SessionId = SessionStore.NewId();
                    SafeFire(() =>
                    {
                        TaskBroadcast?.Invoke(_agent.SessionId, _agent.Title, "running");
                        ZLog.Log("v4 cmd createSession -> " + _agent.SessionId);
                    });
                    // 无预热路径的首发消息随 createSession 携带（payload.firstInput.text），
                    // UI 之后不会再发独立 sendText —— 必须在这里启动回合
                    string firstText = GetStr(payload, "firstInput.text");
                    if (firstText.Length == 0 && payload.TryGetValue("firstInput", out var fi) && fi is Dictionary<string, object> fid)
                        firstText = GetStr(fid, "text");
                    ApplyMode(payload);
                    if (firstText.Length > 0)
                    {
                        ZLog.Log("v4 createSession carries firstInput (" + firstText.Length + " chars)");
                        StartTurn(firstText);
                        SendTextRequested?.Invoke(firstText);
                    }
                    return AckOk(commandId, new Dictionary<string, object>
                    {
                        { "type", "createSession" },
                        { "sessionId", _agent.SessionId }
                    });
                }
                case "sendText":
                {
                    string text = GetStr(payload, "text");
                    if (text.Length == 0) return Ack(commandId, "rejected", "empty text");
                    ApplyMode(payload);
                    StartTurn(text);
                    SendTextRequested?.Invoke(text);
                    return AckOk(commandId, new Dictionary<string, object>
                    {
                        { "type", "inputAccepted" },
                        { "delivery", "startNow" },
                        { "inputId", "in-" + NowMs() }
                    });
                }
                case "attachmentBeginV4":
                {
                    string uploadId = GetStr(payload, "uploadId");
                    if (uploadId.Length == 0) uploadId = "up-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                    AttachmentStaging.Begin(uploadId);
                    return AckOk(commandId, new Dictionary<string, object>
                    {
                        { "uploadId", uploadId },
                        { "state", "staging" },
                        { "nextChunkIndex", 0 }
                    });
                }
                case "attachmentChunkV4":
                {
                    string uploadId = GetStr(payload, "uploadId");
                    int idx = 0;
                    if (payload.TryGetValue("chunkIndex", out var civ) && civ != null)
                        idx = Convert.ToInt32(civ);
                    string data = GetStr(payload, "dataBase64");
                    AttachmentStaging.Append(uploadId, data);
                    return AckOk(commandId, new Dictionary<string, object>
                    {
                        { "uploadId", uploadId },
                        { "nextChunkIndex", idx + 1 }
                    });
                }
                case "attachmentCommitV4":
                {
                    string uploadId = GetStr(payload, "uploadId");
                    string refPath = AttachmentStaging.Commit(uploadId);
                    return AckOk(commandId, new Dictionary<string, object>
                    {
                        { "ref", refPath },
                        { "uploadId", uploadId }
                    });
                }
                case "attachmentAbortV4":
                {
                    AttachmentStaging.Abort(GetStr(payload, "uploadId"));
                    return AckOk(commandId, new Dictionary<string, object>());
                }
                case "stop":
                    StopRequested?.Invoke();
                    lock (_gate)
                    {
                        // 停止时丢弃全部挂起审批（Tcs 置 false 解除 Agent 阻塞）
                        foreach (var kv in _pendingInteractions)
                        {
                            string iid = GetStr(kv, "interactionId");
                            if (_approvals.TryGetValue(iid, out var req))
                            {
                                _approvals.Remove(iid);
                                req.Tcs.TrySetResult(false);
                            }
                        }
                        if (_pendingInteractions.Count > 0)
                        {
                            _pendingInteractions.Clear();
                            FireAll(new Dictionary<string, object>
                            {
                                { "op", "state.updated" },
                                { "patch", new Dictionary<string, object>
                                  { { "pendingInteractions", new List<object>() } }
                                }
                            });
                        }
                    }
                    return AckOk(commandId, null);
                case "resolveInteraction":
                {
                    string interactionId = GetStr(payload, "interactionId");
                    var answer = payload.TryGetValue("answer", out var ansObj) && ansObj is Dictionary<string, object> ad
                        ? ad : new Dictionary<string, object>();
                    string action = answer.TryGetValue("optionId", out var opt) ? opt?.ToString() : null;
                    if (string.IsNullOrEmpty(action) && answer.TryGetValue("action", out var act))
                        action = act?.ToString();
                    bool allow = action == "allow" || action == "accept" || action == "allowOnce";
                    ResolveApproval?.Invoke(interactionId, allow);
                    lock (_gate)
                    {
                        _pendingInteractions.RemoveAll(p => GetStr(p, "interactionId") == interactionId);
                        if (_openToolRowId > 0)
                        {
                            var row = FindRow(_openToolRowId);
                            if (row != null && GetStr(row, "kind") == "toolCall" && GetStr(row, "status") == "pendingApproval")
                            {
                                row["status"] = allow ? "running" : "cancelled";
                                UpsertRow(row);
                                if (!allow) _openToolRowId = -1;
                            }
                        }
                        // 审批已决：立刻清 pendingInteractions，否则官方 UI 卡片一直挂着
                        FireAll(new Dictionary<string, object>
                        {
                            { "op", "state.updated" },
                            { "patch", new Dictionary<string, object>
                              { { "pendingInteractions", PendingList() } }
                            }
                        });
                    }
                    return AckOk(commandId, new Dictionary<string, object>
                    {
                        { "type", "resolveInteraction" },
                        { "resolvedBy", new Dictionary<string, object>
                          { { "clientId", "zcode7-web" }, { "optionId", allow ? "allow" : "deny" } }
                        }
                    });
                }
                case "renameSession":
                {
                    string title = GetStr(payload, "title");
                    if (_agent.SessionId.Length > 0) SessionStore.SetTitle(_agent.SessionId, title);
                    _agent.Title = title;
                    TaskBroadcast?.Invoke(_agent.SessionId, title, _phase);
                    RenameSessionRequested?.Invoke(title);
                    return AckOk(commandId, null);
                }
                case "deleteSession":
                {
                    string sessionId = GetStr(env, "sessionId");
                    if (string.IsNullOrEmpty(sessionId))
                        sessionId = payload.TryGetValue("sessionId", out var so) ? so?.ToString() : "";
                    // 活会话/回合进行中不允许删（UI 清理冗余预热会话时可能误伤）
                    if (string.IsNullOrEmpty(sessionId) || sessionId == _agent.SessionId && _agent.IsBusy)
                    {
                        ZLog.Log("v4 deleteSession skipped (live) target=" + sessionId);
                        return AckOk(commandId, null);
                    }
                    if (!string.IsNullOrEmpty(sessionId))
                    {
                        ZLog.Log("v4 cmd deleteSession target=" + sessionId);
                        SessionStore.Delete(sessionId);
                        TaskRemoved?.Invoke(sessionId);
                        var f = _sessionsIndexFire;
                        if (f != null) FireIndex(new Dictionary<string, object>
                        {
                            { "op", "session.removed" },
                            { "sessionId", sessionId }
                        });
                    }
                    DeleteSessionRequested?.Invoke(sessionId);
                    return AckOk(commandId, null);
                }
                case "compact":
                {
                    var _ = Task.Run(async () =>
                    {
                        try { await _agent.CompactNow(); }
                        catch (Exception ex) { ZLog.Log("compact: " + ex.Message); }
                    });
                    return AckOk(commandId, new Dictionary<string, object>
                    {
                        { "type", "compact" },
                        { "started", true }
                    });
                }
                default:
                    return Ack(commandId, "rejected", "unsupported command: " + type);
            }
        }

        private static Dictionary<string, object> AckOk(string commandId, object result)
        {
            return new Dictionary<string, object>
            {
                { "commandId", commandId },
                { "status", "accepted" },
                { "revisionAtDecision", 1 },
                { "result", result }
            };
        }

        private static Dictionary<string, object> Ack(string commandId, string status, string reason)
        {
            var d = new Dictionary<string, object>
            {
                { "commandId", commandId },
                { "status", status },
                { "revisionAtDecision", 1 }
            };
            if (reason != null) d["reasonCode"] = reason;
            return d;
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
