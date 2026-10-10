// ZCode7 - entry point
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace ZCode7
{
    public static class ZLog
    {
        public static void Log(object msg)
        {
            try
            {
                File.AppendAllText(Path.Combine(Settings.AppDir, "log.txt"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + msg + "\r\n", Encoding.UTF8);
            }
            catch { }
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            // 物理像素坐标：截屏/SendInput/UIA 边界在缩放显示器上必须一致（Win7+ 可用）
            ZCode7.ComputerUse.EnableDpiAwareness();
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12
                    | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
            }
            catch { }

            if (args != null && args.Length > 0 && args[0] == "--selftest")
            {
                RunSelfTest();
                return;
            }

            if (args != null && args.Length > 0 && args[0] == "--apitest")
            {
                RunApiTest();
                return;
            }

            if (args != null && args.Length > 0 && args[0] == "--e2e")
            {
                RunE2E();
                return;
            }

            if (args != null && args.Length > 0 && args[0] == "--approval-test")
            {
                RunApprovalTest();
                return;
            }

            if (args != null && args.Length > 0 && args[0] == "--kill-test")
            {
                RunKillTest();
                return;
            }

            if (args != null && args.Length > 0 && args[0] == "--stop-test")
            {
                RunStopTest();
                return;
            }

            if (args != null && args.Length > 0 && args[0] == "--settings-test")
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new SettingsForm(Settings.Load()));
                return;
            }

            if (args != null && args.Length > 0 && args[0] == "--uitest")
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm(true));
                return;
            }

            Application.ThreadException += (s, e) => ZLog.Log("UI-THREAD: " + e.Exception);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => ZLog.Log("UNHANDLED: " + e.ExceptionObject);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }

        private static void RunApprovalTest()
        {
            var log = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "approval_test.txt");
            System.IO.File.WriteAllText(log, "approval-test " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\n");
            var cfg = Settings.Load();
            cfg.PermissionMode = "build";                 // every tool must ask
            var agent = new Agent(cfg);
            var done = new ManualResetEvent(false);
            agent.OnApprovalNeeded += req =>
            {
                System.IO.File.AppendAllText(log, "APPROVAL REQUESTED: " + req.ToolName
                    + " at " + DateTime.Now.ToString("HH:mm:ss.fff") + "\n");
                // approve after 800ms: proves the agent truly blocks on the gate
                new System.Threading.Timer(o => req.Tcs.TrySetResult(true), null, 800, System.Threading.Timeout.Infinite);
            };
            agent.OnToolStart += (n, a) => System.IO.File.AppendAllText(log, "TOOL START: " + n + " at " + DateTime.Now.ToString("HH:mm:ss.fff") + "\n");
            agent.OnToolDone += (n, r) =>
                System.IO.File.AppendAllText(log, "TOOL DONE: " + n + " => " + (r ?? "").Substring(0, Math.Min(60, (r ?? "").Length)) + "\n");
            agent.OnError += s2 => System.IO.File.AppendAllText(log, "ERROR: " + s2 + "\n");
            agent.OnTurnDone += () => { System.IO.File.AppendAllText(log, "TURN DONE\n"); done.Set(); };
            agent.SessionId = SessionStore.NewId();
            var t = agent.SendAsync("Run command: echo zcode7permtest then tell me the output");
            t.ContinueWith(_ => done.Set());
            done.WaitOne(120000);
            System.IO.File.AppendAllText(log, "RESULT FILE END\n");
        }

        private static void RunKillTest()
        {
            var log = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "kill_test.txt");
            System.IO.File.WriteAllText(log, "kill-test " + DateTime.Now.ToString("HH:mm:ss.fff") + "\n");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Tools.CancelRequested = () => sw.ElapsedMilliseconds > 800;
            var args2 = MiniJson.Obj();
            args2["command"] = "ping -n 15 127.0.0.1";
            var t = System.Threading.Tasks.Task.Run(() =>
                Tools.Execute("Bash", args2, Settings.Load()));
            t.ContinueWith(x =>
            {
                string r = x.Result ?? "";
                System.IO.File.AppendAllText(log, "elapsed=" + sw.ElapsedMilliseconds + "ms result=" + r.Substring(0, Math.Min(80, r.Length)) + "\n");
                System.IO.File.AppendAllText(log, (sw.ElapsedMilliseconds < 5000 ? "RESULT: PASS" : "RESULT: FAIL") + "\n");
                Environment.Exit(0);
            });
            System.Threading.Thread.Sleep(60000);
        }

        private static void RunStopTest()
        {
            var log = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "stop_test.txt");
            System.IO.File.WriteAllText(log, "stop-test " + DateTime.Now.ToString("HH:mm:ss.fff") + "\n");
            var cfg = Settings.Load();
            var agent = new Agent(cfg);
            var done = new ManualResetEvent(false);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            agent.OnToolStart += (n, a) => System.IO.File.AppendAllText(log, "TOOL START: " + n + " at " + sw.ElapsedMilliseconds + "ms\n");
            agent.OnToolDone += (n, r) =>
                System.IO.File.AppendAllText(log, "TOOL DONE: " + n + " => " + (r ?? "").Substring(0, Math.Min(60, (r ?? "").Length)) + " at " + sw.ElapsedMilliseconds + "ms\n");
            agent.OnError += s2 => System.IO.File.AppendAllText(log, "ERROR: " + s2 + " at " + sw.ElapsedMilliseconds + "ms\n");
            agent.OnTurnDone += () => { System.IO.File.AppendAllText(log, "TURN DONE at " + sw.ElapsedMilliseconds + "ms\n"); done.Set(); };
            agent.SessionId = SessionStore.NewId();
            var t = agent.SendAsync("Run command: ping -n 30 127.0.0.1 (takes 30 seconds), then report");
            new System.Threading.Timer(o =>
            {
                System.IO.File.AppendAllText(log, "CANCEL at " + sw.ElapsedMilliseconds + "ms\n");
                agent.Cancel();
            }, null, 1500, System.Threading.Timeout.Infinite);
            t.ContinueWith(_ => done.Set());
            done.WaitOne(120000);
            long ms = sw.ElapsedMilliseconds;
            System.IO.File.AppendAllText(log, (ms < 10000 ? "RESULT: PASS (stopped fast, " : "RESULT: FAIL (too slow, ") + ms + "ms)\n");
        }

        private static void RunE2E()
        {
            string report = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "e2e_result.txt");
            var sb = new StringBuilder();
            try
            {
                var cfg = Settings.Load();
                var probe = new ApiClient(cfg);
                sb.Append("model=").Append(cfg.Model)
                  .Append(" anthropic=").Append(probe.IsAnthropic).Append('\n');
                var agent = new Agent(cfg);
                agent.OnToolStart += (n, a) => sb.Append("[tool-call] ").Append(n).Append(' ').Append((a ?? "").Replace('\n', ' ')).Append('\n');
                agent.OnToolDone += (n, r) => sb.Append("[tool-result ").Append(n).Append("] ")
                    .Append(ToolsHelper.Trunc(r, 400).Replace('\n', ' ')).Append('\n');
                agent.OnAssistantText += t => sb.Append("[final] ").Append(t.Replace('\n', ' ')).Append('\n');
                agent.OnError += t => sb.Append("[error] ").Append(t).Append('\n');
                var task = agent.SendAsync("请用 run_command 工具执行 dir C:\\ 查看磁盘信息，然后把结论告诉我。");
                if (!task.Wait(240000))
                    sb.Append("[timeout] turn did not finish in 240s\n");
                sb.Append("messages=").Append(agent.Messages.Count).Append('\n');
                sb.Append("RESULT: ").Append(sb.ToString().Contains("[error]") || sb.ToString().Contains("[exception]") ? "FAIL" : "PASS").Append('\n');
            }
            catch (Exception ex)
            {
                sb.Append("[exception] ").Append(ex).Append('\n');
                sb.Append("RESULT: FAIL\n");
            }
            File.WriteAllText(report, sb.ToString(), Encoding.UTF8);
        }

        private static void RunApiTest()
        {
            string report = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "apitest_result.txt");
            try
            {
                var cfg = Settings.Load();
                var api = new ApiClient(cfg);
                var task = api.CompleteAsync("你是测试助手。", "回复四个字：连接正常", CancellationToken.None);
                task.Wait(120000);
                File.WriteAllText(report,
                    "apitest " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\n" +
                    "endpoint: " + api.Endpoint + "\n" +
                    "model: " + cfg.Model + "\n" +
                    "reply: " + task.Result + "\n" +
                    "RESULT: PASS\n", Encoding.UTF8);
            }
            catch (Exception ex)
            {
                string msg = ex.Message;
                var agg = ex as AggregateException;
                if (agg != null && agg.InnerExceptions.Count > 0) msg = agg.InnerExceptions[0].Message;
                try
                {
                    File.WriteAllText(report,
                        "apitest " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\n" +
                        "RESULT: FAIL\n" + msg + "\n", Encoding.UTF8);
                }
                catch { }
            }
        }

        private static void RunSelfTest()
        {
            string report = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "selftest_result.txt");
            var sb = new StringBuilder();
            sb.Append("ZCode7 selftest ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append('\n');
            sb.Append("CLR: ").Append(Environment.Version).Append('\n');
            int pass = 0, fail = 0;

            // 1. JSON roundtrip
            try
            {
                string src = "{\"a\":[1,2.5,\"x\\u4e2d文\",true,null],\"b\":{\"c\":\"line\\nnext\"}}";
                object o = MiniJson.Parse(src);
                string back = MiniJson.Serialize(o);
                object o2 = MiniJson.Parse(back);
                var d2 = (Dictionary<string, object>)o2;
                var arr = (List<object>)d2["a"];
                var expected = "x\u4e2d文";
                var actual = arr[2] as string;
                if (arr.Count == 5 && expected.Equals(actual) && d2["b"] != null) { sb.Append("json: PASS\n"); pass++; }
                else
                {
                    sb.Append("json: FAIL (mismatch)\n");
                    sb.Append("  back=").Append(back).Append('\n');
                    sb.Append("  expected chars:");
                    foreach (char ch in expected) sb.Append(' ').Append(((int)ch).ToString("X4"));
                    sb.Append("\n  actual chars:");
                    if (actual == null) sb.Append(" (null)");
                    else foreach (char ch in actual) sb.Append(' ').Append(((int)ch).ToString("X4"));
                    sb.Append('\n');
                    fail++;
                }
            }
            catch (Exception ex) { sb.Append("json: FAIL ").Append(ex.Message).Append('\n'); fail++; }

            // 2. system info tool
            try
            {
                string r = Tools.Execute("SystemInfo", MiniJson.Obj(), new Settings());
                if (r.Contains("hostname")) { sb.Append("get_system_info: PASS\n"); pass++; }
                else { sb.Append("get_system_info: FAIL\n").Append(r).Append('\n'); fail++; }
            }
            catch (Exception ex) { sb.Append("get_system_info: FAIL ").Append(ex.Message).Append('\n'); fail++; }

            // 3. write/read/edit roundtrip
            try
            {
                string p = Path.Combine(Path.GetTempPath(), "zcode7_selftest.txt");
                var a1 = MiniJson.Obj(); a1["file_path"] = p; a1["content"] = "hello 世界\nsecond line";
                Tools.Execute("Write", a1, new Settings());
                var a2 = MiniJson.Obj(); a2["file_path"] = p;
                string r = Tools.Execute("Read", a2, new Settings());
                var a3 = MiniJson.Obj(); a3["file_path"] = p; a3["old_string"] = "second"; a3["new_string"] = "SECOND";
                string r2 = Tools.Execute("Edit", a3, new Settings());
                if (r.Contains("hello 世界") && r2.Contains("Edited")) { sb.Append("file roundtrip: PASS\n"); pass++; }
                else { sb.Append("file roundtrip: FAIL\n").Append(r).Append(r2).Append('\n'); fail++; }
            }
            catch (Exception ex) { sb.Append("file roundtrip: FAIL ").Append(ex.Message).Append('\n'); fail++; }

            // 4. run_command
            try
            {
                var a = MiniJson.Obj(); a["command"] = "echo zcode7selftest";
                string r = Tools.Execute("Bash", a, new Settings());
                if (r.Contains("zcode7selftest")) { sb.Append("run_command: PASS\n"); pass++; }
                else { sb.Append("run_command: FAIL\n").Append(r).Append('\n'); fail++; }
            }
            catch (Exception ex) { sb.Append("run_command: FAIL ").Append(ex.Message).Append('\n'); fail++; }

            // 5. skills
            try
            {
                string s = Skills.BuildSection();
                if (s.Contains("Skills") || s.Contains("技能")) { sb.Append("skills: PASS\n"); pass++; }
                else { sb.Append("skills: FAIL\n"); fail++; }
            }
            catch (Exception ex) { sb.Append("skills: FAIL ").Append(ex.Message).Append('\n'); fail++; }

            // 6. tool definitions
            try
            {
                var defs = Tools.Definitions();
                if (defs.Count >= 9) { sb.Append("tool definitions: PASS (").Append(defs.Count).Append(")\n"); pass++; }
                else { sb.Append("tool definitions: FAIL\n"); fail++; }
            }
            catch (Exception ex) { sb.Append("tool definitions: FAIL ").Append(ex.Message).Append('\n'); fail++; }

            sb.Append("RESULT: ").Append(pass).Append(" passed, ").Append(fail).Append(" failed\n");
            File.WriteAllText(report, sb.ToString(), Encoding.UTF8);
        }
    }
}
