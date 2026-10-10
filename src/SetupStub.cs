using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace ZCode7Setup
{
    // ZCode Classic 自解压安装器：嵌入 payload.zip（= ZCodeClassic_Setup 包），
    // 双击 → 释放到 %TEMP%\ZCodeClassicSetup → 启动 install.ps1（自提权、装文件、建快捷方式）。
    // 依赖 .NET 4.8（主程序同样依赖，Win7 需先装 4.8 + WebView2）。
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            string tmp = Path.Combine(Path.GetTempPath(), "ZCodeClassicSetup");
            try
            {
                Console.Title = "ZCode Classic 安装程序";
                Console.WriteLine("=== ZCode Classic " + Application.ProductVersion + " 安装程序 ===");
                Console.WriteLine();
                if (Directory.Exists(tmp))
                {
                    try { Directory.Delete(tmp, true); } catch { }
                }
                Directory.CreateDirectory(tmp);

                Console.Write("正在释放文件 ... ");
                var asm = Assembly.GetExecutingAssembly();
                using (var rs = asm.GetManifestResourceStream("payload.zip"))
                {
                    if (rs == null) { Console.WriteLine("失败：安装包资源缺失"); return 2; }
                    using (var zip = new System.IO.Compression.ZipArchive(rs, System.IO.Compression.ZipArchiveMode.Read))
                    {
                        foreach (var e in zip.Entries)
                        {
                            string dest = Path.Combine(tmp, e.FullName.Replace('/', Path.DirectorySeparatorChar));
                            if (string.IsNullOrEmpty(e.Name)) { Directory.CreateDirectory(dest); continue; }
                            Directory.CreateDirectory(Path.GetDirectoryName(dest));
                            System.IO.Compression.ZipFileExtensions.ExtractToFile(e, dest, true);
                        }
                    }
                }
                Console.WriteLine("完成");
                Console.WriteLine();
                Console.WriteLine("即将开始安装（如弹出用户账户控制提示请点“是”）...");
                Console.WriteLine();

                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + Path.Combine(tmp, "install.ps1") + "\" -Batch"
                        + (args != null && args.Length > 0 ? " -InstallDir \"" + args[0] + "\"" : ""),
                    UseShellExecute = true,
                    CreateNoWindow = false,
                };
                var p = Process.Start(psi);
                if (p != null) p.WaitForExit();
                int code = p != null ? p.ExitCode : 1;

                Console.WriteLine();
                if (code == 0)
                {
                    try { Directory.Delete(tmp, true); } catch { }
                    return 0;
                }
                Console.WriteLine("安装未完成（退出码 " + code + "）。释放的文件保留在 " + tmp);
                return code;
            }
            catch (Exception ex)
            {
                MessageBox.Show("安装器异常：" + ex.Message, "ZCode Classic", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }
    }
}
