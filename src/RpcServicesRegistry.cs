// ZCode7 - RPC services registry (M1): complete boot-sequence service implementations
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ZCode7
{
    /// <summary>Simple channel backed by a method-name -> handler map.</summary>
    public class RpcServiceChannel : IRpcChannel
    {
        private readonly Dictionary<string, Func<object, object>> _methods
            = new Dictionary<string, Func<object, object>>(StringComparer.Ordinal);

        public void On(string name, Func<object, object> handler) { _methods[name] = handler; }

        public object Call(string name, object arg)
        {
            if (_methods.TryGetValue(name, out var h)) return h(arg);
            throw new NotSupportedException("method '" + name + "' not implemented on this channel");
        }
    }

    /// <summary>Builds and registers the M1 boot-sequence services (official web UI startup).</summary>
    public static class RpcServicesRegistry
    {
        public static string WorkspacePath
        {
            get
            {
                var d = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".zcode7", "workspace");
                Directory.CreateDirectory(d);
                return d;
            }
        }

        private static string TaskIdFromQuery(object q)
        {
            try
            {
                // 官方客户端以数组传参 [params]，需先解包
                var d = First(q) as Dictionary<string, object>;
                if (d == null) return "";
                if (d.TryGetValue("taskId", out var t) && t != null) return t.ToString();
                if (d.TryGetValue("address", out var a) && a is Dictionary<string, object> ad
                    && ad.TryGetValue("taskId", out var t2) && t2 != null) return t2.ToString();
            }
            catch { }
            return "";
        }

        /// <summary>置顶任务 id 清单（config.json "pinnedTaskIds"，跨重启持久）。</summary>
        public static List<string> LoadPinnedIds()
        {
            var l = new List<string>();
            try
            {
                var d = ReadAppConfig();
                if (d != null && d.TryGetValue("pinnedTaskIds", out var pv) && pv is List<object> pl)
                    foreach (var x in pl)
                        if (x != null && x.ToString().Length > 0) l.Add(x.ToString());
            }
            catch { }
            return l;
        }

        public static void SavePinnedIds(List<string> ids)
        {
            try
            {
                string cfgPath = Path.Combine(Settings.AppDir, "config.json");
                var d = ReadAppConfig() ?? new Dictionary<string, object>();
                var objs = new List<object>(); foreach (var x in ids) objs.Add(x); d["pinnedTaskIds"] = objs;
                File.WriteAllText(cfgPath, MiniJson.Serialize(d), Encoding.UTF8);
            }
            catch { }
        }

        private static object PinnedTaskMetas()
        {
            var ids = LoadPinnedIds();
            var items = new List<object>();
            try
            {
                foreach (var m in SessionStore.List())
                {
                    if (m.Archived || !ids.Contains(m.Id)) continue;
                    items.Add(new Dictionary<string, object>
                    {
                        { "taskId", m.Id },
                        { "traceId", m.Id },
                        { "title", string.IsNullOrEmpty(m.Title) ? m.Id : m.Title },
                        { "workspacePath", WorkspacePath },
                        { "createdAt", (long)(m.Updated.ToUniversalTime() - new DateTime(1970, 1, 1)).TotalMilliseconds },
                        { "updatedAt", (long)(m.Updated.ToUniversalTime() - new DateTime(1970, 1, 1)).TotalMilliseconds },
                        { "mode", "build" },
                        { "model", "" }
                    });
                }
            }
            catch { }
            return items;
        }

        private static ulong _wcSub;

        /// <summary>对话工作区（conversation purpose）：与项目 workspace 分开，官方语义。</summary>
        public static string ConversationWorkspacePath
        {
            get
            {
                var d = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".zcode7", "workspace", "conversations");
                Directory.CreateDirectory(d);
                return d;
            }
        }

        private static string SettingPath
        {
            get { return Path.Combine(Settings.AppDir, "app-settings.json"); }
        }

        // 文件里保存过的字段优先，缺的字段用默认补齐（尤其 providerFamilyDomain 这类启动门字段）
        private static object ReadAppSettings()
        {
            var root = DefaultAppSettings();
            try
            {
                if (File.Exists(SettingPath))
                {
                    var disk = MiniJson.ParseObject(File.ReadAllText(SettingPath));
                    if (disk != null)
                        foreach (var kv in disk) root[kv.Key] = kv.Value;
                }
            }
            catch { }
            return root;
        }

        // AppSettings required fields (packages/shared/src/protocol.ts L237): recentProjects[], locale
        // providerFamilyDomain 只认 "zai"|"bigmodel"（normalizeProviderFamilyDomain），
        // 为空会触发 Root.tsx 的 startup-provider-required 登录门。
        private static Dictionary<string, object> DefaultAppSettings()
        {
            return new Dictionary<string, object>
            {
                { "recentProjects", new List<object>() },
                { "locale", "zh-CN" },
                { "providerFamilyDomain", "bigmodel" },
                { "onboardingOccupation", "developer" },   // 存量用户判定：跳过首次启动引导
                { "startPlanRecommendationDismissed", true },
                { "messageStreamShowReasoning", true },
                { "taskAutoArchiveEnabled", false }
            };
        }

        // 本地免登录模式的用户身份：C# Agent 直连智谱 API，无需真实 OAuth 账号
        private static object LocalUser()
        {
            string name;
            try { name = Environment.UserName; } catch { name = "Administrator"; }
            if (string.IsNullOrEmpty(name)) name = "Administrator";
            return new Dictionary<string, object>
            {
                { "id", "zcode7-local" },
                { "username", name },
                { "displayName", name }
            };
        }

        private static object ModelEntry(string id)
        {
            // ModelSelectionModelView: {modelId, config} — config.optionSpecs.reasoningLevel.values
            // 供 validateModelSelectionOptions 校验思考档位（缺失会导致发送键永远禁用）
            return new Dictionary<string, object>
            {
                { "modelId", id },
                { "config", ModelConfigFromStored(FindStoredModel(id), null) }
            };
        }

        /// <summary>在 customProviders 里查找模型条目（含 properties 元数据）。</summary>
        private static Dictionary<string, object> FindStoredModel(string modelId)
        {
            foreach (var cp in LoadCustomProviders())
            {
                if (!(cp.TryGetValue("models", out var ml) && ml is List<object> models)) continue;
                foreach (var m in models)
                {
                    if (m is Dictionary<string, object> md
                        && md.TryGetValue("modelId", out var mid) && mid != null
                        && mid.ToString() == modelId)
                        return md;
                }
            }
            return null;
        }

        /// <summary>
        /// 模型 config：optionSpecs（思考档位校验）+ properties（上下文窗口/输入输出格式，
        /// "模型配置不完整"红字的消除条件）。properties 取自定义供应商存储，缺省给文本模型默认值。
        /// </summary>
        private static Dictionary<string, object> ModelConfigFromStored(Dictionary<string, object> stored, Dictionary<string, object> specCfg)
        {
            var cfg = new Dictionary<string, object>();
            if (specCfg != null) cfg["optionSpecs"] = specCfg["optionSpecs"];
            else
                cfg["optionSpecs"] = new Dictionary<string, object>
                {
                    { "reasoningLevel", new Dictionary<string, object>
                      {
                          { "values", new List<object> { "off", "low", "medium", "high", "max" } }
                      }
                    }
                };
            object props = null;
            if (stored != null && stored.TryGetValue("properties", out var pv) && pv is Dictionary<string, object> pd)
                props = pd;
            if (props == null)
            {
                // 缺省：文本模型（文本+图片+PDF 输入，文本输出，128k 上下文）
                props = new Dictionary<string, object>
                {
                    { "contextWindow", 131072 },
                    { "inputFormat", new Dictionary<string, object>
                      {
                          { "supportsText", true },
                          { "supportsImage", true },
                          { "supportsVideo", false },
                          { "supportsAudio", false },
                          { "supportsPdf", true }
                      }
                    },
                    { "outputFormat", new Dictionary<string, object>
                      {
                          { "supportsText", true }
                      }
                    }
                };
            }
            cfg["properties"] = props;
            return cfg;
        }

        private static object First(object arg)
        {
            if (arg is List<object> l && l.Count > 0) return l[0];
            return arg;
        }

        /// <summary>自定义供应商清单（config.json "customProviders"），模型设置页增删改的持久层。</summary>
        public static List<Dictionary<string, object>> LoadCustomProviders()
        {
            var l = new List<Dictionary<string, object>>();
            try
            {
                string cfgPath = Path.Combine(Settings.AppDir, "config.json");
                if (File.Exists(cfgPath))
                {
                    var d = MiniJson.ParseObject(File.ReadAllText(cfgPath, Encoding.UTF8));
                    if (d != null && d.TryGetValue("customProviders", out var cp) && cp is List<object> list)
                        foreach (var x in list)
                            if (x is Dictionary<string, object> p) l.Add(p);
                }
            }
            catch { }
            return l;
        }

        public static void SaveCustomProviders(List<Dictionary<string, object>> list)
        {
            try
            {
                string cfgPath = Path.Combine(Settings.AppDir, "config.json");
                var root = File.Exists(cfgPath)
                    ? MiniJson.ParseObject(File.ReadAllText(cfgPath, Encoding.UTF8))
                    : new Dictionary<string, object>();
                if (root == null) root = new Dictionary<string, object>();
                root["customProviders"] = list;
                File.WriteAllText(cfgPath, MiniJson.Serialize(root), Encoding.UTF8);
            }
            catch { }
        }

        /// <summary>按 modelId 解析自定义供应商端点并写入 cfg（ApiClient 实时读取）。</summary>
        public static void ResolveCustomApi(Settings cfg, string modelId)
        {
            try
            {
                if (cfg == null || modelId == null) return;
                foreach (var p in LoadCustomProviders())
                {
                    if (!(p.TryGetValue("models", out var ml) && ml is List<object> models)) continue;
                    foreach (var m in models)
                    {
                        if (m is Dictionary<string, object> md
                            && md.TryGetValue("modelId", out var mid) && mid != null
                            && mid.ToString() == modelId)
                        {
                            if (p.TryGetValue("baseUrl", out var bu) && bu != null && bu.ToString().Length > 0)
                                cfg.ApiBase = bu.ToString();
                            // 空 key 不覆盖：保留现有 key，避免把可用配置洗掉
                            if (p.TryGetValue("apiKey", out var ak) && ak != null && ak.ToString().Length > 0)
                                cfg.ApiKey = SanitizeApiKey(ak.ToString());
                            ZLog.Log("ResolveCustomApi " + modelId + " -> " + cfg.ApiBase + " key=" + (cfg.ApiKey.Length > 0 ? "set" : "EMPTY"));
                            return;
                        }
                    }
                }
                // 未匹配到任何自定义供应商：保留当前 cfg（无内置兜底端点），
                // ApiKey 为空时由 ApiClient.RequireKey 抛出可读错误
                ZLog.Log("ResolveCustomApi " + modelId + " -> no provider match, keep endpoint");
            }
            catch { }
        }

        /// <summary>key 只允许 ASCII 可见字符：混入中文/控制符会被网关以 400 拒绝且难以排查。</summary>
        private static string SanitizeApiKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return key ?? "";
            var sb = new System.Text.StringBuilder();
            foreach (var ch in key)
            {
                if (ch >= 33 && ch <= 126) sb.Append(ch);
            }
            var clean = sb.ToString();
            if (clean != key) ZLog.Log("ApiKey sanitized: removed " + (key.Length - clean.Length) + " invalid chars");
            return clean;
        }

                /// <summary>模型设置页视图：仅自定义供应商（config.json customProviders），无任何内置写死条目。</summary>
        private static Dictionary<string, object> ProviderSettingsViewDict()
        {
            var specCfg = new Dictionary<string, object>
            {
                { "optionSpecs", new Dictionary<string, object>
                  {
                      { "reasoningLevel", new Dictionary<string, object>
                        {
                            { "values", new List<object> { "off", "low", "medium", "high", "max" } }
                        }
                      }
                  }
                }
            };
            var allProviders = new List<object>();
            foreach (var cp in LoadCustomProviders())
            {
                if (!(cp.TryGetValue("providerId", out var cpid))) continue;
                string cpname = cp.TryGetValue("providerName", out var pn2) ? (pn2 != null ? pn2.ToString() : "") : "";
                var cpcfg = new Dictionary<string, object>
                {
                    { "group", "standard-personal" },
                    // 官方编辑器按 config.access(api-key 型) 渲染 API Key 输入框（strict schema，
                    // 平铺 apiKey 是非法键会被丢弃）
                    { "access", new Dictionary<string, object>
                      {
                          { "type", "api-key" },
                          { "apiKey", cp.TryGetValue("apiKey", out var ak3) && ak3 != null ? ak3.ToString() : "" }
                      }
                    },
                    // api.type 必须是官方枚举（anthropic-messages/openai-chat-completions/openai-responses），
                    // 非法值会让"API 格式"下拉显示为空
                    { "api", new Dictionary<string, object>
                      {
                          { "type", "anthropic-messages" },
                          { "baseUrl", cp.TryGetValue("baseUrl", out var bu3) ? bu3 : "https://" }
                      }
                    }
                };
                var cmodels = new List<object>();
                if (cp.TryGetValue("models", out var ml2) && ml2 is List<object> cms2)
                {
                    foreach (var m in cms2)
                    {
                        if (!(m is Dictionary<string, object> md)) continue;
                        if (!(md.TryGetValue("modelId", out var mid) && mid != null)) continue;
                        string midStr = mid.ToString();
                        if (midStr.Length == 0) continue;
                        bool menabled = !(md.TryGetValue("enabled", out var en) && en is bool bb && !bb);
                        cmodels.Add(new Dictionary<string, object>
                        {
                            { "kind", "candidate" },
                            { "modelId", midStr },
                            { "builtin", false },
                            { "effectiveBuiltinConfig", specCfg },
                            // 完整度检查读 effectiveConfig/config 两处，都给全
                            { "effectiveConfig", ModelConfigFromStored(md, specCfg) },
                            { "config", ModelConfigFromStored(md, specCfg) },
                            { "enabled", menabled },
                            { "executable", true },
                            { "selectable", true },
                            { "issues", new List<object>() }
                        });
                    }
                }
                allProviders.Add(new Dictionary<string, object>
                {
                    { "providerId", cpid },
                    { "providerName", cpname },
                    { "templateId", "custom" },
                    { "enabled", true },
                    { "executable", true },
                    { "effectiveConfig", cpcfg },
                    { "config", cpcfg },
                    { "accountState", new Dictionary<string, object>
                      {
                          { "availability", "available" },
                          { "entitled", false }
                      }
                    },
                    { "issues", new List<object>() },
                    { "models", cmodels }
                });
            }
            return new Dictionary<string, object>
            {
                { "revision", 1 },
                { "providerTemplates", new List<object>() },
                { "providerOrder", ProviderOrderList() },
                { "providers", allProviders }
            };
        }

        private static string GetPid(object o)
        {
            if (o is Dictionary<string, object> d && d.TryGetValue("providerId", out var pi)) return pi != null ? pi.ToString() : "";
            if (o is string str) return str;
            return "";
        }

        /// <summary>config.json 顶层读取（不存在返回 null）。</summary>
        private static Dictionary<string, object> ReadAppConfig()
        {
            try
            {
                string cfgPath = Path.Combine(Settings.AppDir, "config.json");
                if (File.Exists(cfgPath))
                    return MiniJson.ParseObject(File.ReadAllText(cfgPath, Encoding.UTF8));
            }
            catch { }
            return null;
        }

        private static string ConfigStr(Dictionary<string, object> d, string key)
        {
            if (d != null && d.TryGetValue(key, out var v) && v != null) return v.ToString();
            return "";
        }

        /// <summary>provider-settings 的 providerOrder：仅自定义供应商 id。</summary>
        private static List<object> ProviderOrderList()
        {
            var order = new List<object>();
            foreach (var cp in LoadCustomProviders())
                if (cp.TryGetValue("providerId", out var pid) && pid != null) order.Add(pid.ToString());
            return order;
        }

        /// <summary>
        /// model-selection 的当前选择：模型=配置 "model"，供应商=含该模型的自定义供应商，
        /// 思考档位=配置 "thinkingLevel"。无任何可用配置时返回 null（composer 发送键禁用）。
        /// </summary>
        private static object Selection()
        {
            var appCfg = ReadAppConfig();
            string modelId = ConfigStr(appCfg, "model");
            string thought = ConfigStr(appCfg, "thinkingLevel");
            if (thought.Length == 0) thought = "high";
            if (modelId.Length == 0) return null;
            foreach (var cp in LoadCustomProviders())
            {
                if (!(cp.TryGetValue("models", out var ml) && ml is List<object> models)) continue;
                foreach (var m in models)
                {
                    if (m is Dictionary<string, object> md
                        && md.TryGetValue("modelId", out var mid) && mid != null
                        && mid.ToString() == modelId
                        && cp.TryGetValue("providerId", out var pid) && pid != null)
                    {
                        return new Dictionary<string, object>
                        {
                            { "providerId", pid.ToString() },
                            { "modelId", modelId },
                            { "options", new Dictionary<string, object> { { "reasoningLevel", thought } } }
                        };
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// model-selection.getView 的 providers：仅自定义供应商（ModelSelectionProviderView 形状）。
        /// 不含任何内置写死供应商/模型。
        /// </summary>
        private static List<object> MsProviderViews()
        {
            var list = new List<object>();
            foreach (var cp in LoadCustomProviders())
            {
                if (!(cp.TryGetValue("providerId", out var pid) && pid != null)) continue;
                var apiCfg = new Dictionary<string, object>
                {
                    { "type", "anthropic-messages" },
                    { "baseUrl", cp.TryGetValue("baseUrl", out var bu) && bu != null ? bu.ToString() : "https://" }
                };
                var models = new List<object>();
                if (cp.TryGetValue("models", out var ml) && ml is List<object> cms)
                {
                    foreach (var m in cms)
                    {
                        if (!(m is Dictionary<string, object> md)) continue;
                        if (!(md.TryGetValue("modelId", out var mid) && mid != null)) continue;
                        string midStr = mid.ToString();
                        if (midStr.Length == 0) continue;
                        bool menabled = !(md.TryGetValue("enabled", out var en) && en is bool bb && !bb);
                        if (!menabled) continue;
                        models.Add(ModelEntry(midStr));
                    }
                }
                list.Add(new Dictionary<string, object>
                {
                    { "providerId", pid.ToString() },
                    { "providerName", cp.TryGetValue("providerName", out var pn) && pn != null ? pn.ToString() : "" },
                    { "templateId", "custom" },
                    { "config", new Dictionary<string, object>
                      {
                          { "group", "standard-personal" },
                          { "access", new Dictionary<string, object>
                            {
                                { "type", "api-key" },
                                { "apiKey", cp.TryGetValue("apiKey", out var akM) && akM != null ? akM.ToString() : "" }
                            }
                          },
                          { "api", apiCfg }
                      }
                    },
                    { "models", models }
                });
            }
            return list;
        }

        public static void RegisterAll(RpcChannelServer server, Settings cfg)
        {
            RegisterAll(server, cfg, null);
        }

        public static void RegisterAll(RpcChannelServer server, Settings cfg, Agent agent)
        {
            var workspacePath = WorkspacePath;

            // window-controller 通道先建（V4AgentChannel 的 TaskBroadcast 需要引用它）
            WindowControllerChannel wcLive = null;
            if (agent != null)
            {
                wcLive = new WindowControllerChannel(agent);
                wcLive.SetConfig(cfg);
                server.RegisterChannel("window-controller", wcLive);
            }

            // ---- system ----
            var system = new RpcServiceChannel();
            system.On("info", _ => new Dictionary<string, object>
            {
                { "homedir", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) },
                { "platform", "win32" }
            });
            system.On("listIntegratedTerminalShells", _ => new List<object>
            {
                new Dictionary<string, object>
                {
                    { "dialect", "cmd" }, { "id", "cmd" }, { "label", "命令提示符" },
                    { "path", Environment.GetFolderPath(Environment.SpecialFolder.System) + "\\cmd.exe" },
                    { "source", "system" }
                }
            });
            system.On("probeIntranet", _ => new Dictionary<string, object>
            {
                { "isIntranet", false }, { "reachedTargetCount", 0 }, { "requiredSuccessCount", 0 },
                { "totalTargets", 0 }, { "checkedAt", DateTime.UtcNow.ToString("o") }, { "strategy", "" },
                { "results", new List<object>() }
            });
            server.RegisterChannel("system", system);

            // ---- setting (AppSettings: recentProjects[]/locale are required) ----
            var settingPath = SettingPath;
            var setting = new RpcServiceChannel();
            setting.On("get", _ => ReadAppSettings());
            setting.On("update", arg =>
            {
                try
                {
                    var root = ReadAppSettings() as Dictionary<string, object>;
                    // update(patch) or update(patch, accountSettings)
                    if (arg is List<object> arr && arr.Count > 0 && arr[0] is Dictionary<string, object> patch)
                        foreach (var kv in patch) root[kv.Key] = kv.Value;
                    File.WriteAllText(settingPath, MiniJson.Serialize(root));
                }
                catch { }
                return null;
            });
            setting.On("updateDataBaseDir", _ => null);
            setting.On("ensureDefaultProject", _ => new Dictionary<string, object>
            {
                { "path", WorkspacePath }, { "created", false }
            });
            server.RegisterChannel("setting", setting);

            // ---- client-config ----
            var clientConfig = new RpcServiceChannel();
            clientConfig.On("getSnapshot", _ => new Dictionary<string, object> { { "pluginStoreOrder", null } });
            server.RegisterChannel("client-config", clientConfig);

            // ---- model-selection ----
            // ModelSelectionView: {revision, providers[], preferredSelection?, effectiveSelection?}
            // preferredSelection/effectiveSelection 缺失时 composer 草稿没有可提交配置 → 发送键禁用
            var modelSelection = new RpcServiceChannel();
            modelSelection.On("getView", _ => new Dictionary<string, object>
            {
                { "revision", 1 },
                { "providers", MsProviderViews() },
                { "preferredSelection", Selection() },
                { "effectiveSelection", Selection() },
                { "selectionIssue", null }
            });
            modelSelection.On("onDidChange", _ => null); // event: no-op
            server.RegisterChannel("model-selection", modelSelection);

            // ---- provider-settings ----
            // ProviderSettingsView: {revision, providerTemplates[], providerOrder[], providers[]}
            var providerSettings = new RpcServiceChannel();
            providerSettings.On("getView", _ => ProviderSettingsViewDict());
            providerSettings.On("refresh", _ => ProviderSettingsViewDict());
            // ---- 模型管理页增删改（官方模型设置界面直接操作）----
            providerSettings.On("createPersonalProvider", q =>
            {
                var d = First(q) as Dictionary<string, object> ?? new Dictionary<string, object>();
                string name = "自定义供应商";
                if (d.TryGetValue("providerName", out var pn) && pn != null && pn.ToString().Length > 0)
                    name = pn.ToString();
                var list = LoadCustomProviders();
                var cp = new Dictionary<string, object>
                {
                    { "providerId", "custom:" + Guid.NewGuid().ToString("N").Substring(0, 8) },
                    { "providerName", name },
                    { "baseUrl", "https://" },
                    { "apiKey", "" },
                    { "models", new List<object>() }
                };
                list.Add(cp);
                SaveCustomProviders(list);
                return new Dictionary<string, object> { { "providerId", cp["providerId"] } };
            });
            providerSettings.On("savePersonalProviderOverlay", q =>
            {
                var arr = q as List<object>;
                if (arr != null && arr.Count > 0)
                {
                    string pid = "";
                    if (arr[0] is Dictionary<string, object> d0 && d0.TryGetValue("providerId", out var pi))
                        pid = pi != null ? pi.ToString() : "";
                    else if (arr[0] is string s0) pid = s0;
                    object cfgObj = arr.Count > 1 ? arr[1] : null;
                    var list = LoadCustomProviders();
                    foreach (var cp in list)
                    {
                        if (cp.TryGetValue("providerId", out var pi2) && pi2 != null && pi2.ToString() == pid)
                        {
                            if (cfgObj is Dictionary<string, object> cfgd)
                            {
                                // 官方形状：config.api.baseUrl / config.access.apiKey（strict schema）
                                if (cfgd.TryGetValue("api", out var apiO) && apiO is Dictionary<string, object> apid
                                    && apid.TryGetValue("baseUrl", out var buN) && buN != null && buN.ToString().Length > 0)
                                    cp["baseUrl"] = buN.ToString();
                                if (cfgd.TryGetValue("access", out var accO) && accO is Dictionary<string, object> accd
                                    && accd.TryGetValue("apiKey", out var akN))
                                    cp["apiKey"] = akN != null ? SanitizeApiKey(akN.ToString()) : "";
                                // 兼容平铺兜底
                                if (cfgd.TryGetValue("baseUrl", out var bu)) cp["baseUrl"] = bu != null ? bu.ToString() : "";
                                if (cfgd.TryGetValue("apiKey", out var ak)) cp["apiKey"] = ak != null ? ak.ToString() : "";
                                if (cfgd.TryGetValue("providerName", out var pn2) && pn2 != null) cp["providerName"] = pn2.ToString();
                            }
                            SaveCustomProviders(list);
                            break;
                        }
                    }
                }
                return ProviderSettingsViewDict();
            });
            providerSettings.On("deletePersonalProvider", q =>
            {
                var arr = q as List<object>;
                string pid = "";
                if (arr != null && arr.Count > 0)
                {
                    if (arr[0] is Dictionary<string, object> d0 && d0.TryGetValue("providerId", out var pi))
                        pid = pi != null ? pi.ToString() : "";
                    else if (arr[0] is string s0) pid = s0;
                }
                var list = LoadCustomProviders();
                list.RemoveAll(c => c.TryGetValue("providerId", out var pi2) && pi2 != null && pi2.ToString() == pid);
                SaveCustomProviders(list);
                return ProviderSettingsViewDict();
            });
            providerSettings.On("savePersonalModelDraft", q =>
            {
                var d = First(q) as Dictionary<string, object>;
                if (d != null)
                {
                    string pid = d.TryGetValue("providerId", out var pi) && pi != null ? pi.ToString() : "";
                    string mid = d.TryGetValue("nextModelId", out var mi2) && mi2 != null ? mi2.ToString() : "";
                    if (mid.Length == 0) mid = d.TryGetValue("modelId", out var mi) && mi != null ? mi.ToString() : "";
                    if (pid.Length > 0 && mid.Length > 0)
                    {
                        var list = LoadCustomProviders();
                        foreach (var cp in list)
                        {
                            if (cp.TryGetValue("providerId", out var pi2) && pi2 != null && pi2.ToString() == pid)
                            {
                                if (!(cp.TryGetValue("models", out var ml) && ml is List<object> models))
                                    cp["models"] = models = new List<object>();
                                bool found = false;
                                foreach (var m in models)
                                {
                                    if (m is Dictionary<string, object> md && md.TryGetValue("modelId", out var mid2) && mid2 != null && mid2.ToString() == mid)
                                    {
                                        found = true;
                                        break;
                                    }
                                }
                                if (!found) models.Add(new Dictionary<string, object> { { "modelId", mid }, { "enabled", true } });
                                break;
                            }
                        }
                        SaveCustomProviders(list);
                    }
                }
                return ProviderSettingsViewDict();
            });
            providerSettings.On("setPersonalModelEnabled", q =>
            {
                var arr = q as List<object>;
                if (arr != null && arr.Count >= 3)
                {
                    string pid = GetPid(arr[0]);
                    string mid = arr[1] != null ? arr[1].ToString() : "";
                    bool en = arr[2] is bool bb && bb;
                    var list = LoadCustomProviders();
                    foreach (var cp in list)
                    {
                        if (cp.TryGetValue("providerId", out var pi) && pi != null && pi.ToString() == pid
                            && cp.TryGetValue("models", out var ml) && ml is List<object> models)
                        {
                            foreach (var m in models)
                                if (m is Dictionary<string, object> md && md.TryGetValue("modelId", out var mid2) && mid2 != null && mid2.ToString() == mid)
                                    md["enabled"] = en;
                        }
                    }
                    SaveCustomProviders(list);
                }
                return ProviderSettingsViewDict();
            });
            providerSettings.On("deletePersonalModel", q =>
            {
                var arr = q as List<object>;
                if (arr != null && arr.Count >= 2)
                {
                    string pid = GetPid(arr[0]);
                    string mid = arr[1] != null ? arr[1].ToString() : "";
                    var list = LoadCustomProviders();
                    foreach (var cp in list)
                    {
                        if (cp.TryGetValue("providerId", out var pi) && pi != null && pi.ToString() == pid
                            && cp.TryGetValue("models", out var ml) && ml is List<object> models)
                            models.RemoveAll(m => m is Dictionary<string, object> md && md.TryGetValue("modelId", out var mid2) && mid2 != null && mid2.ToString() == mid);
                    }
                    SaveCustomProviders(list);
                }
                return ProviderSettingsViewDict();
            });
            providerSettings.On("reorderPersonalModels", _ => ProviderSettingsViewDict());
            providerSettings.On("testModelConnectivity", _ => new Dictionary<string, object>
            {
                { "reachable", true },
                { "latencyMs", 0 },
                { "error", null }
            });
            server.RegisterChannel("provider-settings", providerSettings);

            // ---- broadcast (send 无需投递，仅消音) ----
            var bcast = new RpcServiceChannel();
            bcast.On("send", _ => null);
            server.RegisterChannel("broadcast", bcast);

            // ---- file: workspace bootstrap ----
            var file = new RpcServiceChannel();
            file.On("ensureConversationWorkspace", _ => new Dictionary<string, object>
            {
                { "workspacePath", ConversationWorkspacePath }, { "created", true }
            });
            file.On("listWorkspaceFilesLength", _ => 0);
            // @ 文件搜索：遍历工作区（跳过 node_modules/.git），按文件名包含匹配
            file.On("searchWorkspaceFiles", q =>
            {
                var results = new List<object>();
                try
                {
                    var d = First(q) as Dictionary<string, object> ?? new Dictionary<string, object>();
                    string query = d.TryGetValue("query", out var qv) && qv != null ? qv.ToString() : "";
                    int limit = 20;
                    if (d.TryGetValue("limit", out var lv) && lv != null)
                    { try { limit = Convert.ToInt32(lv); } catch { } }
                    if (limit <= 0 || limit > 100) limit = 20;
                    if (query.Length == 0) return results;
                    var root = new DirectoryInfo(WorkspacePath);
                    if (!root.Exists) return results;
                    var dirs = new Stack<DirectoryInfo>();
                    dirs.Push(root);
                    int scanned = 0;
                    while (dirs.Count > 0 && results.Count < limit && scanned < 4000)
                    {
                        var dir = dirs.Pop();
                        try
                        {
                            foreach (var f in dir.EnumerateFiles())
                            {
                                scanned++;
                                if (results.Count >= limit) break;
                                if (f.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    string rel = f.FullName.Substring(root.FullName.Length + 1);
                                    results.Add(new Dictionary<string, object>
                                    {
                                        { "name", f.Name },
                                        { "path", f.FullName },
                                        { "relativePath", rel },
                                        { "type", "file" }
                                    });
                                }
                            }
                            foreach (var sub in dir.EnumerateDirectories())
                            {
                                scanned++;
                                var n = sub.Name;
                                if (n == "node_modules" || n == ".git" || n.StartsWith(".")) continue;
                                if (n.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 && results.Count < limit)
                                {
                                    string rel = sub.FullName.Substring(root.FullName.Length + 1);
                                    results.Add(new Dictionary<string, object>
                                    {
                                        { "name", sub.Name },
                                        { "path", sub.FullName },
                                        { "relativePath", rel },
                                        { "type", "directory" }
                                    });
                                }
                                dirs.Push(sub);
                            }
                        }
                        catch { }
                    }
                }
                catch { }
                return results;
            });
            server.RegisterChannel("file", file);

            // ---- onboarding-record (first-run onboarding: answer "already onboarded") ----
            var onboarding = new RpcServiceChannel();
            onboarding.On("shouldOnboard", _ => false);
            onboarding.On("claimAnonymousRecord", _ => null);
            onboarding.On("getLatestEntry", _ => null);
            onboarding.On("dismissOnboarding", _ => null);
            onboarding.On("appendRecord", _ => null);
            onboarding.On("syncSettingsFromRecord", _ => null);
            onboarding.On("updateRecordPreferences", _ => null);
            onboarding.On("getRecords", _ => null);
            onboarding.On("clearRecords", _ => null);
            server.RegisterChannel("onboarding-record", onboarding);

            // ---- oauth (local no-login mode: C# Agent 直连智谱，本地视为已登录) ----
            // Root.tsx 启动登录门：restoreCachedSessionState 返回 authenticated 才 setUser；
            // getActiveProvider="bigmodel" 与 appSettings.providerFamilyDomain 必须一致。
            var oauth = new RpcServiceChannel();
            oauth.On("restoreCachedSessionState", _ => new Dictionary<string, object>
            {
                { "status", "authenticated" },
                { "userInfo", LocalUser() }
            });
            oauth.On("restoreCachedSession", _ => LocalUser());
            oauth.On("restoreSession", _ => LocalUser());
            oauth.On("getActiveProvider", _ => "bigmodel");
            oauth.On("getProviders", _ => new List<object>
            {
                new Dictionary<string, object>
                {
                    { "id", "zai" }, { "displayName", "Z.ai" }, { "enabled", true }, { "order", 1 }
                },
                new Dictionary<string, object>
                {
                    { "id", "bigmodel" }, { "displayName", "BigModel" }, { "enabled", true }, { "order", 2 }
                }
            });
            oauth.On("getConnection", _ => null);
            oauth.On("startOAuth", _ => null);
            oauth.On("startOAuthWithPolling", _ => null);
            oauth.On("pollPendingOAuth", _ => null);
            oauth.On("handleCallback", _ => null);
            oauth.On("refreshToken", _ => null);
            oauth.On("logout", _ => null);
            oauth.On("logoutAll", _ => null);
            oauth.On("cancelPending", _ => null);
            server.RegisterChannel("oauth", oauth);

            // ---- credential ----
            var cred = new RpcServiceChannel();
            cred.On("load", _ => null);
            cred.On("store", _ => null);
            cred.On("delete", _ => null);
            server.RegisterChannel("credential", cred);

            // ---- coding-plan-subscription (workflow feature disabled) ----
            var codingPlan = new RpcServiceChannel();
            codingPlan.On("getDynamicWorkflowClientConfig", _ => new Dictionary<string, object>
            {
                { "mode", "disabled" }, { "enabled", false }, { "source", "disabled" }
            });
            // empty entitlements: UI treats every plan family as no_plan and degrades gracefully
            codingPlan.On("getCodingPlanSupport", _ => new Dictionary<string, object>
            {
                { "supported", false },
                { "productList", new List<object>() },
                { "authenticated", false }
            });
            codingPlan.On("getEnterprisePricing", _ => new Dictionary<string, object>
            {
                { "productList", new List<object>() }
            });
            codingPlan.On("getCodingPlanUsageSnapshot", _ => new Dictionary<string, object>
            {
                { "plans", new List<object>() }
            });
            codingPlan.On("getCodingPlanCredentialKeys", _ => new Dictionary<string, object>
            {
                { "keys", new List<object>() }
            });
            codingPlan.On("getCodingPlanResetStatus", _ => new Dictionary<string, object>
            {
                { "available", false }
            });
            codingPlan.On("getStaticProducts", _ => new List<object>());
            codingPlan.On("getStaticTeamProducts", _ => new List<object>());
            codingPlan.On("getOffPeakClientConfig", _ => null);
            server.RegisterChannel("coding-plan-subscription", codingPlan);

            // ---- zcode-agent (bootstrap methods; conversation v4 lands in M2) ----
            var agentCh = new RpcServiceChannel();
            agentCh.On("prepareStorage", _ => null);
            agentCh.On("getStorageStartupState", _ => null);            // null = 无迁移进行中
            agentCh.On("initialize", _ => new Dictionary<string, object>
            {
                { "available", true },
                { "workspaceKey", "zcode7-local" },
                { "protocolName", "ZCode Protocol" },
                { "protocolVersion", 1 },
                { "transportKind", "websocket" }
            });
            agentCh.On("syncAppRuntimePreferences", _ => null);
            agentCh.On("getWorkspaceRuntimeIdentity", _ => new Dictionary<string, object>
            {
                { "generation", 1 }, { "identity", "zcode7-local" }, { "workspaceKey", "zcode7-local" }
            });
            agentCh.On("listSessions", _ => ListSessionsJson());
            server.RegisterChannel("zcode-agent", agentCh);

            // 有真实 Agent 实例时用 v4 通道覆盖（含 helloConversationV4 / subscribe /
            // sendConversationCommandV4，对接官方 UI 的会话链路）
            if (agent != null)
            {
                var approvals = new Dictionary<string, Agent.ApprovalRequest>();
                var v4 = new V4AgentChannel(
                    agent,
                    cfg,
                    () => ListSessionsJson(),
                    () => agent.Todos ?? new List<Dictionary<string, object>>(),
                    () => cfg != null ? cfg.Model : "",
                    () => agent.SessionId,
                    approvals);
                v4.TaskBroadcast = (id, title, phase) => wcLive?.UpsertTask(id, title, phase);
                v4.TaskRemoved = id => wcLive?.RemoveTask(id);
                v4.SendTextRequested += t => { var _ = agent.SendAsync(t); };
                v4.StopRequested += () => agent.Cancel();
                v4.NewSessionRequested += () => agent.NewSession();
                v4.ResolveApproval = (interactionId, allow) =>
                {
                    if (approvals.TryGetValue(interactionId, out var req))
                    {
                        approvals.Remove(interactionId);
                        req.Tcs.TrySetResult(allow);
                        return true;
                    }
                    return false;
                };
                server.RegisterChannel("zcode-agent", v4);
            }

            // ---- zcode-task（侧栏"任务"列表的真数据源：任务库 ZCodeTaskMeta[]）----
            var task = new RpcServiceChannel();
            Func<object, List<object>> listTaskMetas = q =>
            {
                // echo 查询的 workspacePath：UI 按 scope 过滤，路径必须一致
                string wsPath = WorkspacePath;
                try
                {
                    var d = q as Dictionary<string, object>;
                    if (d != null && d.TryGetValue("workspacePath", out var w) && w != null)
                        wsPath = w.ToString();
                }
                catch { }
                var items = new List<object>();
                try
                {
                    foreach (var m in SessionStore.List())
                    {
                        if (m.Archived) continue;   // 归档会话不进 timeline
                        items.Add(new Dictionary<string, object>
                        {
                            { "taskId", m.Id },
                            { "traceId", m.Id },
                            { "title", string.IsNullOrEmpty(m.Title) ? m.Id : m.Title },
                            { "workspacePath", wsPath },
                            { "createdAt", (long)(m.Updated.ToUniversalTime() - new DateTime(1970, 1, 1)).TotalMilliseconds },
                            { "updatedAt", (long)(m.Updated.ToUniversalTime() - new DateTime(1970, 1, 1)).TotalMilliseconds },
                            { "mode", "build" },
                            { "model", cfg != null ? cfg.Model : "" }
                        });
                    }
                }
                catch { }
                return items;
            };
            task.On("listTasks", new Func<object, object>(q => listTaskMetas(q)));
            task.On("listPinnedTasks", _ => PinnedTaskMetas());
            task.On("listPinnedTaskIds", _ => (object)LoadPinnedIds());
            task.On("listArchivedTasks", _ =>
            {
                var archived = new List<object>();
                try
                {
                    foreach (var m in SessionStore.List())
                    {
                        if (!m.Archived) continue;
                        archived.Add(new Dictionary<string, object>
                        {
                            { "taskId", m.Id },
                            { "traceId", m.Id },
                            { "title", string.IsNullOrEmpty(m.Title) ? m.Id : m.Title },
                            { "workspacePath", WorkspacePath },
                            { "createdAt", (long)(m.Updated.ToUniversalTime() - new DateTime(1970, 1, 1)).TotalMilliseconds },
                            { "updatedAt", (long)(m.Updated.ToUniversalTime() - new DateTime(1970, 1, 1)).TotalMilliseconds },
                            { "mode", "build" }
                        });
                    }
                }
                catch { }
                return archived;
            });
            task.On("listDeletedTaskIds", _ => new List<object>());
            task.On("getTaskSessionFilePath", q =>
            {
                string id = TaskIdFromQuery(q);
                if (id.Length == 0) return null;
                return new Dictionary<string, object>
                {
                    { "sessionId", id },
                    { "sessionFilePath", SessionStore.PathOf(id) },
                    { "workspacePath", WorkspacePath }
                };
            });
            task.On("getTaskNativeSessionLogFile", q =>
            {
                string id2 = TaskIdFromQuery(q);
                if (id2.Length == 0) return null;
                return new Dictionary<string, object>
                {
                    { "sessionId", id2 },
                    { "logFilePath", Path.Combine(Settings.AppDir, "log.txt") },
                    { "exists", File.Exists(Path.Combine(Settings.AppDir, "log.txt")) }
                };
            });
            // 分组视图结构：本地无分组数据，返回空结构（groups/members/topOrder 空 = 全部任务平铺）
            task.On("listGroupedTaskViewStructure", _ => new Dictionary<string, object>
            {
                { "groups", new List<object>() },
                { "members", new List<object>() },
                { "topOrder", new List<object>() }
            });
            // 任务操作（右键菜单）：归档/取消归档/置顶/未读
            task.On("archiveTask", q =>
            {
                string id = TaskIdFromQuery(q);
                if (id.Length > 0) SessionStore.SetArchived(id, true);
                return null;
            });
            task.On("unarchiveTask", q =>
            {
                string id = TaskIdFromQuery(q);
                if (id.Length > 0) SessionStore.SetArchived(id, false);
                return null;
            });
            // 删除任务（归档区确认后调用）：移除会话文件并清理置顶
            task.On("deleteTask", q =>
            {
                string id = TaskIdFromQuery(q);
                if (id.Length == 0) return null;
                SessionStore.Delete(id);
                var pinned = LoadPinnedIds();
                if (pinned.Remove(id)) SavePinnedIds(pinned);
                return null;
            });
            task.On("pinTask", q =>
            {
                string id = TaskIdFromQuery(q);
                if (id.Length == 0) return null;
                var l = LoadPinnedIds();
                if (!l.Contains(id)) { l.Insert(0, id); SavePinnedIds(l); }
                return null;
            });
            task.On("unpinTask", q =>
            {
                string id = TaskIdFromQuery(q);
                if (id.Length == 0) return null;
                var l = LoadPinnedIds();
                if (l.Remove(id)) SavePinnedIds(l);
                return null;
            });
            task.On("markTaskAsRead", _ => null);
            task.On("markTaskAsUnread", _ => null);
            // 重命名任务（侧栏右键 → 重命名任务 → 确认）
            task.On("renameTask", q =>
            {
                var d = First(q) as Dictionary<string, object>;
                if (d == null) return null;
                string id = d.TryGetValue("taskId", out var t) && t != null ? t.ToString() : "";
                string title = d.TryGetValue("title", out var ti) && ti != null ? ti.ToString() : "";
                string wsPath2 = d.TryGetValue("workspacePath", out var w) && w != null ? w.ToString() : WorkspacePath;
                if (id.Length == 0) return null;
                SessionStore.SetTitle(id, title);
                long ms = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds;
                return new Dictionary<string, object>
                {
                    { "taskId", id },
                    { "traceId", id },
                    { "title", title },
                    { "titleOverridden", true },
                    { "workspacePath", wsPath2 },
                    { "createdAt", ms },
                    { "updatedAt", ms },
                    { "mode", "build" },
                    { "model", cfg != null ? cfg.Model : "" }
                };
            });
            server.RegisterChannel("zcode-task", task);

            // ---- settings-sync（迁移向导：永远无可导入项 → 首启提示直接标记已处理） ----
            // getFirstRunPromptState 必须返回 handled=true：未处理时 UI 会跑 detect，
            // detect 失败/为空的分支都会把迁移弹窗 open=true。
            var sync = new RpcServiceChannel();
            sync.On("markFirstRunPromptHandled", _ => null);
            sync.On("getFirstRunPromptState", _ => new Dictionary<string, object>
            {
                { "handled", true }
            });
            sync.On("getSettingsSyncState", _ => null);
            sync.On("pullSettings", _ => null);
            sync.On("pushSettings", _ => null);
            sync.On("detect", _ => new Dictionary<string, object>
            {
                { "agents", new List<object>() },
                { "categories", new List<object>() }
            });
            server.RegisterChannel("settings-sync", sync);

            // ---- commands（设置页"命令"列表；本地模式无用户自定义命令，返回空集+桌面端限制提示） ----
            var cmds = new RpcServiceChannel();
            cmds.On("list", _ => new Dictionary<string, object>
            {
                { "commands", new List<object>() },
                { "userCommands", new List<object>() },
                { "pluginCommands", new List<object>() },
                { "capability", new Dictionary<string, object>
                  {
                      { "userScopeAvailable", false },
                      { "userScopeReason", "desktop_only" }
                  }
                }
            });
            cmds.On("getPrimaryUserCommandsDirectory", _ => new Dictionary<string, object> { { "path", "" } });
            server.RegisterChannel("commands", cmds);

            // ---- hooks（设置页"钩子"：本地模式无钩子存储，返回空集） ----
            var hooks = new RpcServiceChannel();
            hooks.On("loadHooks", _ => new Dictionary<string, object> { { "hooks", new List<object>() } });
            hooks.On("saveHooks", _ => null);
            server.RegisterChannel("hooks", hooks);

            // ---- mcp-sync（MCP 页健康状态轮询：返回空状态集消音；真实连接在 McpHub） ----
            var mcpSync = new RpcServiceChannel();
            mcpSync.On("listWorkspaceMcpServerStatuses", _ => new Dictionary<string, object>
            {
                { "statuses", new List<object>() }
            });
            mcpSync.On("loadMcpFromUserDirectory", _ => new Dictionary<string, object>
            {
                { "servers", new List<object>() }
            });
            mcpSync.On("saveMcpToUserDirectory", _ => null);
            mcpSync.On("listLocalUserMcpCandidates", _ => new List<object>());
            server.RegisterChannel("mcp-sync", mcpSync);

            // ---- off-peak-task（闲时任务：排除功能，空实现消音） ----
            var offpeak = new RpcServiceChannel();
            offpeak.On("list", _ => new List<object>());
            offpeak.On("getCodingPlanSupport", _ => new Dictionary<string, object> { { "supported", false } });
            offpeak.On("getOffPeakClientConfig", _ => null);
            server.RegisterChannel("off-peak-task", offpeak);

            // ---- window-controller（官方侧栏"任务"列表的数据源）----
            var wc = new RpcServiceChannel();
            wc.On("listTaskList", _ => new List<object>());
            wc.On("subscribeControllerV4", _ => new Dictionary<string, object>
            {
                { "ack", new Dictionary<string, object>
                  { { "subscriptionId", "wc-" + (++_wcSub) }, { "mode", "snapshot" }, { "logEpoch", "wc-e1" } }
                }
            });
            wc.On("unsubscribeControllerV4", _ => new Dictionary<string, object>
            {
                { "ack", new Dictionary<string, object>
                  { { "subscriptionId", "wc-x" }, { "mode", "snapshot" }, { "logEpoch", "wc-e1" } }
                }
            });
            wc.On("resyncControllerV4", _ => new Dictionary<string, object>
            {
                { "ack", new Dictionary<string, object>
                  { { "subscriptionId", "wc-r" }, { "mode", "snapshot" }, { "logEpoch", "wc-e1" } }
                }
            });
            if (wcLive == null) server.RegisterChannel("window-controller", wc);

            // ---- plugin-management ----
            var plugins = new RpcServiceChannel();
            // 真实 MCP 服务器注入插件列表（每个 mcpServers 条目渲染为一个插件，MCP tab 计数正确）
            plugins.On("listPlugins", _ =>
            {
                var list = new List<object>();
                try
                {
                    foreach (var kv in Settings.Load().McpServers)
                    {
                        var conf = kv.Value;
                        var serverNames = new List<object> { kv.Key };
                        list.Add(new Dictionary<string, object>
                        {
                            { "id", "mcp-" + kv.Key },
                            { "name", kv.Key },
                            { "description", "MCP 服务器: " + MiniJson.GetStr(conf, "command", "") },
                            { "enabled", true },
                            { "source", "user" },
                            { "marketplace", "local" },
                            { "skillRootCount", 0 },
                            { "commandRootCount", 0 },
                            { "mcpServerNames", serverNames },
                            { "rootPath", "" }
                        });
                    }
                }
                catch { }
                return new Dictionary<string, object>
                {
                    { "plugins", list },
                    { "diagnostics", new List<object>() }
                };
            });
            // 插件市场/概览为排除功能，返回空集消音（页面本身已有空态渲染）
            plugins.On("getPluginsOverview", _ => new Dictionary<string, object>
            {
                { "plugins", new List<object>() },
                { "marketplaces", new List<object>() }
            });
            plugins.On("getPluginReferenceCatalog", _ => new Dictionary<string, object>
            {
                { "plugins", new List<object>() }
            });
            plugins.On("updatePluginMarketplace", _ => null);
            server.RegisterChannel("plugin-management", plugins);

            // ---- bots ----
            var bots = new RpcServiceChannel();
            bots.On("syncAppRuntimePreferences", _ => null);
            bots.On("listBots", _ => new List<object>());
            server.RegisterChannel("bots", bots);

            // ---- subagents（AgentsListResult：agents/userAgents/pluginAgents/capability）----
            var sub = new RpcServiceChannel();
            sub.On("list", _ => new Dictionary<string, object>
            {
                { "agents", new List<object>() },
                { "userAgents", new List<object>() },
                { "pluginAgents", new List<object>() },
                { "capability", new Dictionary<string, object>
                  {
                      { "userScopeAvailable", true }
                  }
                }
            });
            server.RegisterChannel("subagents", sub);

            // ---- client-scenes ----
            var scenes = new RpcServiceChannel();
            scenes.On("list", _ => new List<object>());
            server.RegisterChannel("client-scenes", scenes);

            // ---- git (git 面板未启用，静默空应答避免报错) ----
            var git = new RpcServiceChannel();
            git.On("refresh", _ => null);
            git.On("getStatus", _ => null);
            git.On("listBranches", _ => new List<object>());
            server.RegisterChannel("git", git);

            // ---- zcode-session ----
            var session = new RpcServiceChannel();
            session.On("readSession", _ => null);
            // workspace presentation：mode + 斜杠命令目录（缺失则 / 命令面板为空）
            session.On("readWorkspacePresentation", q =>
            {
                string wsPath = WorkspacePath;
                try
                {
                    var d = q as Dictionary<string, object>;
                    if (d != null && d.TryGetValue("workspace", out var w) && w is Dictionary<string, object> wd
                        && wd.TryGetValue("workspacePath", out var wp) && wp != null)
                        wsPath = wp.ToString();
                }
                catch { }
                string mode = "build";
                try { if (cfg != null && cfg.PermissionMode.Length > 0) mode = cfg.PermissionMode; } catch { }
                Func<string, string, object> cmd = (name, desc) => new Dictionary<string, object>
                {
                    { "name", name }, { "description", desc }, { "source", "builtin" }
                };
                return new Dictionary<string, object>
                {
                    { "workspace", new Dictionary<string, object> { { "workspacePath", wsPath } } },
                    { "mode", mode },
                    { "slashCommands", new List<object>
                      {
                          cmd("compact", "压缩当前对话上下文，释放窗口空间"),
                          cmd("plan", "切换到规划模式，先制定计划再执行")
                      }
                    }
                };
            });
            server.RegisterChannel("zcode-session", session);

            // ---- skills ----
            var skills = new RpcServiceChannel();
            // 真实技能清单：Skills.LoadAll 已合并 应用本地 + 官方 ~/.zcode/skills 双目录
            skills.On("list", _ =>
            {
                var list = new List<object>();
                try
                {
                    foreach (var sk in Skills.LoadAll())
                    {
                        list.Add(new Dictionary<string, object>
                        {
                            { "id", sk.Folder },
                            { "name", sk.Name },
                            { "description", sk.Description ?? "" },
                            { "body", sk.Body ?? "" },
                            { "path", sk.Path ?? "" },
                            { "scope", "user" },
                            { "enabled", sk.Enabled }
                        });
                    }
                }
                catch { }
                return new Dictionary<string, object>
                {
                    { "skills", list },
                    { "capability", new Dictionary<string, object>
                      {
                          { "userScopeAvailable", true }
                      }
                    },
                    { "diagnostics", new List<object>() }
                };
            });
            // 技能启用/禁用开关（设置页技能行 switch）：持久化到 config.json disabledSkills
            skills.On("setEnabled", q =>
            {
                var d = First(q) as Dictionary<string, object> ?? new Dictionary<string, object>();
                string skillId = d.TryGetValue("skillId", out var sid) && sid != null ? sid.ToString() : "";
                bool enabled = !(d.TryGetValue("enabled", out var en) && en is bool b && !b);
                if (skillId.Length > 0) Skills.SetDisabled(skillId, !enabled);
                return null;
            });
            skills.On("buildPromptContext", _ => new Dictionary<string, object>
            {
                { "prompt", "" }, { "activatedSkillNames", new List<object>() }
            });
            server.RegisterChannel("skills", skills);

            // ---- usage-stats (disabled) ----
            var usage = new RpcServiceChannel();
            usage.On("getAppUsageSnapshot", _ => new Dictionary<string, object>());
            usage.On("getEntitlementSnapshot", _ => new Dictionary<string, object>());
            server.RegisterChannel("usage-stats", usage);
        }

        /// <summary>SessionStore sessions as ZCodeSessionInfo-shaped JSON list.</summary>
        public static List<object> ListSessionsJson()
        {
            var list = new List<object>();
            try
            {
                foreach (var m in SessionStore.List())
                {
                    var ws = new Dictionary<string, object>
                    {
                        { "workspacePath", WorkspacePath },
                        { "workspaceKey", "zcode7-local" }
                    };
                    list.Add(new Dictionary<string, object>
                    {
                        { "sessionId", m.Id },
                        { "workspace", ws },
                        { "sessionKind", "interactive" },
                        { "title", string.IsNullOrEmpty(m.Title) ? m.Id : m.Title },
                        { "titleSource", "first_input" },
                        { "mode", "yolo" },
                        { "status", "idle" },
                        { "createdAt", m.Updated.ToString("yyyy-MM-dd\\THH:mm:ss") },
                        { "updatedAt", m.Updated.ToString("yyyy-MM-dd\\THH:mm:ss") }
                    });
                }
            }
            catch { }
            return list;
        }
    }
}
