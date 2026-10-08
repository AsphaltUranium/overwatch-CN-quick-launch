using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Media;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace OWOneClick
{
    // 守望先锋一键启动 —— 完全静默版（无任何窗口，出错只写日志）
    internal static class Program
    {
        // ==================== 常量 ====================
        private const string TaskName = "OWOneClickElevated";
        private const string MutexName = "OWOneClickLauncher_8f3a1c";
        private const string AppTitle = "守望先锋一键启动";
        private const string Version = "1.3.4";

        private static readonly string[] GameProcessNames = { "Overwatch", "NeacLoader", "OWNeacClient" };
        private static readonly string[] ClientProcessNames = { "Battle.net", "Battle.net Launcher" };

        // ==================== 配置 ====================
        private static string ExePath;
        private static string ExeDir;
        private static string ClientPath;
        private static string ProductCode = "Pro";      // 守望先锋产品代码（GitHub 战网命令行文档验证）
        private static bool UseElevation = true;        // 是否用计划任务静默提权
        private static int GameTimeoutSec = 60;         // 发送启动指令后，检测游戏进程的最长时间（每 0.5 秒一次）
        private static int WindowTimeoutSec = 60;       // 循环检测客户端开启的最长时间（每 0.5 秒一次）
        private static string LaunchCommand;            // 自定义启动参数（留空用默认 --exec）

        // ==================== 数据文件 ====================
        private static string DataDir;
        private static string StatusFile;
        private static string ResultFile;
        private static string FlowStartedFile;
        private static string LogFile;
        private static string IniPath;

        // ==================== 运行模式 ====================
        private static bool IsElevatedFlow;
        private static bool IsInstallTask;
        private static bool IsCheck;

        // ==================== Win32 ====================
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder text, int count);
        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(IntPtr token, int cls, out uint info, uint len, out uint ret);
        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

        private const uint MB_OK = 0x00000000;
        private const uint MB_ICONERROR = 0x00000010;
        private const uint MB_ICONWARNING = 0x00000030;
        private const uint MB_SETFOREGROUND = 0x00010000;
        private const uint MB_TOPMOST = 0x00040000;

        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const uint TOKEN_QUERY = 0x0008;
        private const int TokenElevation = 20;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        private sealed class WinInfo
        {
            public IntPtr Hwnd;
            public string Title;
            public string Class;
            public int W, H;
            public bool Visible;
            public bool Mini;
        }

        // ==================== 入口 ====================
        [STAThread]
        private static void Main(string[] args)
        {
            string bootFile = Path.Combine(Path.GetTempPath(), "owlauncher_boot.log");
            try
            {
                File.AppendAllText(bootFile, DateTime.Now.ToString("O") + " main args=" + string.Join(" ", args) + "\r\n");
            }
            catch { }
            try
            {
                ParseArgs(args);

                ExePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                ExeDir = Path.GetDirectoryName(ExePath);
                DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OWOneClick");
                try
                {
                    Directory.CreateDirectory(DataDir);
                    // 可写性探测：目录存在但不可写时回退到临时目录
                    string probe = Path.Combine(DataDir, ".wtest");
                    File.WriteAllText(probe, "ok");
                    File.Delete(probe);
                }
                catch
                {
                    DataDir = Path.Combine(Path.GetTempPath(), "OWOneClick");
                    Directory.CreateDirectory(DataDir);
                }
                StatusFile = Path.Combine(DataDir, "status.txt");
                ResultFile = Path.Combine(DataDir, "result.txt");
                FlowStartedFile = Path.Combine(DataDir, "flowstarted.txt");
                LogFile = Path.Combine(DataDir, "log.txt");
                IniPath = Path.Combine(ExeDir, "config.ini");

                Log("=== " + AppTitle + " v" + Version + "  参数: " + string.Join(" ", args) + " ===");
                LoadConfig();

                if (IsCheck) { RunCheck(); return; }
                if (IsElevatedFlow) { ElevatedFlow(); return; }
                if (IsInstallTask) { CreateTaskViaCom(); ElevatedFlow(); return; }

                // 正常运行：程序必须放在暴雪客户端根目录（与 Battle.net.exe 同目录）
                if (!File.Exists(Path.Combine(ExeDir, "Battle.net.exe")))
                {
                    Log("未找到暴雪客户端，程序不在客户端根目录");
                    ShowMsg(AppTitle, "未找到暴雪客户端！\n\n请把本程序放在暴雪客户端根目录下\n（与 Battle.net.exe 同一个文件夹）再运行。", MessageBoxIcon.Error);
                    return;
                }

                NormalEntry();
            }
            catch (Exception ex)
            {
                Log("致命错误: " + ex);
                try { File.AppendAllText(bootFile, DateTime.Now.ToString("O") + " FATAL: " + ex + "\r\n"); } catch { }
            }
        }

        private static void ParseArgs(string[] args)
        {
            foreach (string a in args)
            {
                switch (a.ToLowerInvariant())
                {
                    case "--elevated-flow": IsElevatedFlow = true; break;
                    case "--install-task": IsInstallTask = true; break;
                    case "--check": IsCheck = true; break;
                }
            }
        }

        // ==================== 配置 ====================
        private static void LoadConfig()
        {
            ClientPath = FindClientPath();
            if (File.Exists(IniPath))
            {
                foreach (string raw in File.ReadAllLines(IniPath, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string v = line.Substring(eq + 1).Trim();
                    switch (k)
                    {
                        case "productcode": if (v.Length > 0) ProductCode = v; break;
                        case "useelevation": UseElevation = ParseBool(v, UseElevation); break;
                        case "gametimeoutseconds": GameTimeoutSec = ParseInt(v, GameTimeoutSec); break;
                        case "windowtimeoutseconds": WindowTimeoutSec = ParseInt(v, WindowTimeoutSec); break;
                        case "launchcommand": if (v.Length > 0) LaunchCommand = v; break;
                    }
                }
                Log("已读取配置: ProductCode=" + ProductCode + " UseElevation=" + UseElevation);
            }
            else
            {
                Log("未找到 config.ini，使用默认配置。ProductCode=" + ProductCode);
            }
        }

        private static bool ParseBool(string v, bool def)
        {
            bool r;
            if (bool.TryParse(v, out r)) return r;
            if (v == "1") return true;
            if (v == "0") return false;
            return def;
        }

        private static int ParseInt(string v, int def)
        {
            int r;
            return int.TryParse(v, out r) ? r : def;
        }

        private static string FindClientPath()
        {
            // 严格要求：程序必须放在暴雪客户端根目录（与 Battle.net.exe 同目录）
            return Path.Combine(ExeDir, "Battle.net.exe");
        }

        // ==================== 日志 ====================
        private static void Log(string msg)
        {
            try
            {
                File.AppendAllText(LogFile, DateTime.Now.ToString("HH:mm:ss.fff") + " " + msg + "\r\n", Encoding.UTF8);
            }
            catch { }
        }

        private static void WriteStatus(string text)
        {
            Log("状态: " + text);
            try { File.WriteAllText(StatusFile, text, Encoding.UTF8); } catch { }
        }

        private static void WriteResult(string code, string msg)
        {
            try { File.WriteAllText(ResultFile, code + "\n" + msg, Encoding.UTF8); } catch { }
            Log("结果: " + code + " - " + msg);
        }

        private static void DeleteFile(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        // 统一弹窗：Windows 原生 MessageBox（user32）+ MB_TOPMOST 置顶。
        // 原生外观/音效/图标全保留；TOPMOST 不依赖抢焦点，
        // 所以在 UAC 拒绝后的前台锁定（约 200 秒）下也能显示在最上层。
        private static void ShowMsg(string title, string message, MessageBoxIcon icon)
        {
            uint type = MB_OK | MB_TOPMOST | MB_SETFOREGROUND |
                        (icon == MessageBoxIcon.Error ? MB_ICONERROR : MB_ICONWARNING);
            MessageBox(IntPtr.Zero, message, title, type);
        }

        // 仅"拒绝 UAC"场景用：自定义弹窗（TopMost 置顶，盖过前台锁定）。
        // 系统感叹号图标 + 系统音效；标题栏不带软件图标，对齐原生。
        private static void ShowMsgTopMost(string title, string message)
        {
            using (Form f = new Form())
            {
                f.Text = title;
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.MaximizeBox = false;
                f.MinimizeBox = false;
                f.StartPosition = FormStartPosition.CenterScreen;
                f.TopMost = true;
                f.ShowInTaskbar = false;
                f.ShowIcon = false;   // 标题栏不带软件图标
                f.Font = SystemFonts.MessageBoxFont;

                PictureBox pb = new PictureBox();
                pb.Image = SystemIcons.Warning.ToBitmap();
                pb.SizeMode = PictureBoxSizeMode.Normal;
                pb.Location = new Point(16, 16);

                Label lbl = new Label();
                lbl.Text = message;
                lbl.Font = SystemFonts.MessageBoxFont;
                lbl.TextAlign = ContentAlignment.TopLeft;
                lbl.AutoSize = false;
                Size ms = TextRenderer.MeasureText(message, lbl.Font,
                    new Size(340, int.MaxValue), TextFormatFlags.WordBreak);
                lbl.Location = new Point(pb.Right + 12, 16);
                lbl.Size = new Size(ms.Width, ms.Height);

                Button ok = new Button();
                ok.Text = "确定";
                ok.DialogResult = DialogResult.OK;
                ok.Font = SystemFonts.MessageBoxFont;
                ok.Size = new Size(84, 28);

                int fw = Math.Max(lbl.Right + 24, pb.Right + 24);
                if (fw < 260) fw = 260;
                ok.Location = new Point(fw - ok.Width - 12, lbl.Bottom + 18);

                f.ClientSize = new Size(fw, ok.Bottom + 12);
                f.Controls.Add(pb);
                f.Controls.Add(lbl);
                f.Controls.Add(ok);
                f.AcceptButton = ok;

                try { SystemSounds.Exclamation.Play(); } catch { }

                f.ShowDialog();
            }
        }

        private static bool WaitForFile(string path, TimeSpan timeout)
        {
            DateTime deadline = DateTime.Now + timeout;
            while (DateTime.Now < deadline)
            {
                if (File.Exists(path)) return true;
                Thread.Sleep(500);
            }
            return false;
        }

        // ==================== 调度入口 ====================
        private static void NormalEntry()
        {
            bool created;
            using (Mutex m = new Mutex(true, MutexName, out created))
            {
                if (!created)
                {
                    Log("已有实例在运行，退出");
                    return;
                }

                if (IsUserAdmin())
                {
                    Log("当前进程已提权，直接执行完整流程");
                    ElevatedFlow();
                    return;
                }

                if (IsGameRunning())
                {
                    Log("游戏已在运行，无需重复启动");
                    return;
                }

                if (!UseElevation)
                {
                    Log("UseElevation=false，普通权限执行（游戏启动时可能弹 UAC）");
                    ElevatedFlow();
                    return;
                }

                // ---- 提权模式 ----
                DeleteFile(ResultFile);
                if (!TaskExistsViaCom())
                {
                    WriteStatus("首次使用需要创建计划任务，正在请求管理员权限…");
                    Log("计划任务不存在，请求管理员权限创建");
                    DeleteFile(FlowStartedFile);
                    bool elevatedStarted = false;
                    try
                    {
                        ProcessStartInfo psi = new ProcessStartInfo(ExePath, "--install-task") { Verb = "runas", UseShellExecute = true };
                        Process.Start(psi);
                        elevatedStarted = true;
                    }
                    catch
                    {
                        Log("启动提权进程失败（用户可能拒绝了 UAC）");
                    }

                    if (elevatedStarted && WaitForFile(FlowStartedFile, TimeSpan.FromSeconds(60)))
                    {
                        WaitForResultSilent(TimeSpan.FromMinutes(5));
                    }
                    else
                    {
                        // UAC 被拒绝或提权流程未能启动：明确提示用户需要一次管理员权限
                        Log("未获得管理员权限（创建计划任务需要），提示用户");
                        ShowMsgTopMost(AppTitle,
                            "首次使用需要一次管理员权限（用于创建计划任务，只需一次）。\n\n" +
                            "请重新运行本程序，在弹出的 UAC 窗口中点击“是”同意；\n" +
                            "或右键本程序选择“以管理员身份运行”。\n\n" +
                            "创建成功后即可全程静默使用，不再需要任何权限。");
                    }
                }
                else
                {
                    Log("计划任务已存在，通过任务计划程序运行");
                    if (RunTaskViaCom())
                        WaitForResultSilent(TimeSpan.FromMinutes(6));
                    else
                        ElevatedFlow(); // 任务运行失败，降级
                }
            }
        }

        private static void WaitForResultSilent(TimeSpan timeout)
        {
            DateTime deadline = DateTime.Now + timeout;
            while (DateTime.Now < deadline)
            {
                if (File.Exists(ResultFile))
                {
                    string txt = File.ReadAllText(ResultFile, Encoding.UTF8);
                    string[] parts = txt.Split(new[] { '\n' }, 2);
                    Log("流程完成: " + parts[0].Trim());
                    return;
                }
                Thread.Sleep(1000);
            }
            Log("等待流程结果超时");
        }

        // ==================== 提权完整流程 ====================
        private static void ElevatedFlow()
        {
            WriteStatus("正在初始化…");
            try
            {
                try { File.WriteAllText(FlowStartedFile, DateTime.Now.ToString("O"), Encoding.UTF8); } catch { }

                if (IsElevatedFlow && UseElevation)
                {
                    CreateTaskViaCom(); // 幂等
                }

                if (!File.Exists(ClientPath))
                {
                    WriteResult("FAIL", "找不到战网客户端（Battle.net.exe）");
                    ShowMsg(AppTitle, "未找到暴雪客户端！\n\n请把本程序放在暴雪客户端根目录下\n（与 Battle.net.exe 同一个文件夹）再运行。", MessageBoxIcon.Error);
                    return;
                }

                if (IsGameRunning())
                {
                    WriteResult("ALREADY", "游戏已经在运行");
                    return;
                }

                bool clientRunning = IsClientRunning();
                bool clientElevated = IsAnyClientElevated();

                if (clientRunning)
                {
                    if (!clientElevated && IsUserAdmin() && UseElevation)
                    {
                        WriteStatus("正在以管理员模式重启战网客户端…");
                        Log("战网正在普通权限运行，重启为管理员模式");
                        CloseClient();
                        Thread.Sleep(1500);
                        clientRunning = false;
                    }
                    else if (!clientElevated)
                    {
                        Log("战网已在普通权限运行（未重启），游戏启动时可能弹 UAC");
                    }
                }

                if (!clientRunning)
                {
                    WriteStatus("正在启动战网客户端…");
                    Process.Start(ClientPath);
                    Log("已启动: " + ClientPath);
                }

                // 等待客户端进程出现（每 0.2 秒，最长 WindowTimeoutSec 秒）
                // 注：不做"等窗口"门槛——客户端可能最小化/收进托盘没有可见窗口；
                // 持续发送方案下，进程一在就开始发指令，窗口检测只用于记录登录窗/主窗口状态
                if (!clientRunning)
                {
                    WriteStatus("等待战网客户端开启…");
                    if (!WaitForClientProcess(TimeSpan.FromSeconds(WindowTimeoutSec)))
                    {
                        WriteResult("FAIL", "等待战网客户端启动超时");
                        ShowMsg(AppTitle, "未检测到战网客户端启动。\n\n请稍后重试。\n详细日志：" + LogFile, MessageBoxIcon.Warning);
                        return;
                    }
                }
                else
                {
                    Log("战网客户端已在运行");
                }

                // 持续发送：每 0.5 秒轮询游戏进程，同时每次都发送启动指令
                // （守望先锋只会开启一个实例，重复发送无害；即使登录验证窗口吞掉前几条指令，后面的也会成功）
                WriteStatus("正在启动守望先锋…");
                bool ok = false;
                DateTime deadline = DateTime.Now.AddSeconds(GameTimeoutSec);
                string lastWin = "";
                while (DateTime.Now < deadline)
                {
                    if (IsGameRunning()) { ok = true; break; }
                    SendLaunchCommand();
                    string ws = WindowStateSummary();
                    if (ws != lastWin)
                    {
                        Log("窗口状态: " + ws);
                        lastWin = ws;
                    }
                    Thread.Sleep(500);
                }
                if (IsGameRunning()) ok = true;

                if (ok)
                {
                    WriteResult("OK", "游戏启动成功");
                }
                else
                {
                    WriteResult("FAIL", "未检测到游戏启动成功");
                    ShowMsg(AppTitle, "未检测到游戏启动成功！\n\n请确认战网已经登录，然后重新运行本程序。\n详细日志：" + LogFile, MessageBoxIcon.Warning);
                }
            }
            catch (Exception ex)
            {
                Log("提权流程异常: " + ex);
                WriteResult("FAIL", "发生错误：" + ex.Message + " 日志：" + LogFile);
            }
        }

        private static bool WaitForClientProcess(TimeSpan timeout)
        {
            DateTime deadline = DateTime.Now + timeout;
            while (DateTime.Now < deadline)
            {
                if (IsClientRunning())
                {
                    Log("检测到战网客户端进程已开启");
                    return true;
                }
                Thread.Sleep(200);
            }
            return false;
        }

        // 汇总当前客户端窗口状态，用于日志判断"登录窗 / 主窗口"
        private static string WindowStateSummary()
        {
            List<WinInfo> vis = EnumClientWindows().Where(x => x.Visible).ToList();
            if (vis.Count == 0) return "(无可见窗口)";
            string label = "其他窗口(可能仍在加载)";
            if (vis.Any(IsMainWindow))
                label = "主窗口(可开始游戏)";
            else if (vis.Any(IsLoginWindow))
                label = "登录窗口(等待验证)";
            var parts = vis.Select(x => "'" + x.Title + "' " + x.Class + " " + x.W + "x" + x.H + (x.Mini ? "(最小化)" : "")).ToList();
            return label + " | " + string.Join(" / ", parts);
        }

        // 主窗口：标题含"战网"/"Battle.net"，类为 Chrome_WidgetWin_0（CEF 主界面）——不依赖尺寸
        private static bool IsMainWindow(WinInfo w)
        {
            return w.Visible && !w.Mini &&
                   (w.Title.Contains("战网") || w.Title.Contains("Battle.net")) &&
                   w.Class.Contains("Chrome_WidgetWin_0");
        }

        // 登录窗口：标题含"登录"/"Login"，或窗口类为 Qt 图标窗口（如 Qt5151QWindowIcon）
        private static bool IsLoginWindow(WinInfo w)
        {
            return w.Visible &&
                   (w.Title.Contains("登录") || w.Title.Contains("Login") || w.Class.Contains("QWindowIcon"));
        }

        // ==================== 窗口检测 ====================
        private static List<WinInfo> EnumClientWindows()
        {
            HashSet<uint> pids = new HashSet<uint>();
            foreach (string name in ClientProcessNames)
            {
                try
                {
                    foreach (Process p in Process.GetProcessesByName(name))
                        pids.Add((uint)p.Id);
                }
                catch { }
            }
            List<WinInfo> list = new List<WinInfo>();
            if (pids.Count == 0) return list;
            EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                uint pid;
                GetWindowThreadProcessId(h, out pid);
                if (pids.Contains(pid))
                {
                    StringBuilder t = new StringBuilder(512);
                    StringBuilder c = new StringBuilder(256);
                    GetWindowText(h, t, 512);
                    GetClassName(h, c, 256);
                    RECT r;
                    GetWindowRect(h, out r);
                    list.Add(new WinInfo
                    {
                        Hwnd = h,
                        Title = t.ToString(),
                        Class = c.ToString(),
                        W = r.Right - r.Left,
                        H = r.Bottom - r.Top,
                        Visible = IsWindowVisible(h),
                        Mini = IsIconic(h)
                    });
                }
                return true;
            }, IntPtr.Zero);
            return list;
        }

        // ==================== 进程/提权检测 ====================
        private static bool IsGameRunning()
        {
            foreach (string name in GameProcessNames)
            {
                try { if (Process.GetProcessesByName(name).Length > 0) return true; } catch { }
            }
            return false;
        }

        private static bool IsClientRunning()
        {
            foreach (string name in ClientProcessNames)
            {
                try { if (Process.GetProcessesByName(name).Length > 0) return true; } catch { }
            }
            return false;
        }

        private static bool IsAnyClientElevated()
        {
            foreach (string name in ClientProcessNames)
            {
                try
                {
                    foreach (Process p in Process.GetProcessesByName(name))
                        if (IsProcessElevated((uint)p.Id)) return true;
                }
                catch { }
            }
            return false;
        }

        private static bool IsProcessElevated(uint pid)
        {
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return false;
            try
            {
                IntPtr token;
                if (!OpenProcessToken(h, TOKEN_QUERY, out token)) return false;
                try
                {
                    uint info, ret;
                    if (!GetTokenInformation(token, TokenElevation, out info, 4, out ret)) return false;
                    return info != 0;
                }
                finally
                {
                    CloseHandle(token);
                }
            }
            finally
            {
                CloseHandle(h);
            }
        }

        private static bool IsUserAdmin()
        {
            try
            {
                WindowsIdentity id = WindowsIdentity.GetCurrent();
                WindowsPrincipal pr = new WindowsPrincipal(id);
                return pr.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        private static void CloseClient()
        {
            try
            {
                foreach (string name in ClientProcessNames)
                {
                    try
                    {
                        foreach (Process p in Process.GetProcessesByName(name))
                        {
                            try { p.CloseMainWindow(); } catch { }
                        }
                    }
                    catch { }
                }
                Thread.Sleep(2000);
                try
                {
                    Process.Start(new ProcessStartInfo("taskkill", "/IM \"Battle.net.exe\" /T /F") { UseShellExecute = false, CreateNoWindow = true });
                    Process.Start(new ProcessStartInfo("taskkill", "/IM \"Battle.net Launcher.exe\" /T /F") { UseShellExecute = false, CreateNoWindow = true });
                }
                catch { }
                DateTime deadline = DateTime.Now.AddSeconds(15);
                while (DateTime.Now < deadline && IsClientRunning())
                    Thread.Sleep(500);
            }
            catch (Exception ex)
            {
                Log("关闭战网客户端异常: " + ex.Message);
            }
        }

        private static void SendLaunchCommand()
        {
            try
            {
                string args = LaunchCommand;
                if (string.IsNullOrEmpty(args))
                    args = "--exec=\"launch " + ProductCode + "\"";
                ProcessStartInfo psi = new ProcessStartInfo(ClientPath, args) { UseShellExecute = false };
                Process.Start(psi);
                Log("发送启动指令: " + args);
            }
            catch (Exception ex)
            {
                Log("发送启动指令失败: " + ex.Message);
            }
        }

        // ==================== 计划任务（COM） ====================
        private static dynamic GetTaskService()
        {
            Type t = Type.GetTypeFromProgID("Schedule.Service");
            if (t == null) return null;
            dynamic svc = Activator.CreateInstance(t);
            svc.Connect();
            return svc;
        }

        private static bool TaskExistsViaCom()
        {
            try
            {
                dynamic svc = GetTaskService();
                if (svc == null) return false;
                dynamic folder = svc.GetFolder("\\");
                dynamic task = folder.GetTask(TaskName);
                return task != null;
            }
            catch
            {
                return false;
            }
        }

        private static bool CreateTaskViaCom()
        {
            try
            {
                dynamic svc = GetTaskService();
                if (svc == null) return false;
                string xml =
                    "<?xml version=\"1.0\" encoding=\"UTF-16\"?>" +
                    "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">" +
                    "<RegistrationInfo><Description>" + AppTitle + " - 无弹窗提权辅助任务</Description></RegistrationInfo>" +
                    "<Triggers />" +
                    "<Principals><Principal id=\"Author\"><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>" +
                    "<Settings>" +
                    "<MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>" +
                    "<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>" +
                    "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>" +
                    "<AllowHardTerminate>true</AllowHardTerminate>" +
                    "<StartWhenAvailable>false</StartWhenAvailable>" +
                    "<RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>" +
                    "<Hidden>true</Hidden>" +
                    "<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>" +
                    "<Priority>7</Priority>" +
                    "</Settings>" +
                    "<Actions Context=\"Author\"><Exec><Command>" + XmlEscape(ExePath) + "</Command><Arguments>--elevated-flow</Arguments></Exec></Actions>" +
                    "</Task>";
                dynamic folder = svc.GetFolder("\\");
                folder.RegisterTask(TaskName, xml, 6, "", "", 3, "");
                Log("计划任务已创建: " + TaskName);
                return true;
            }
            catch (Exception ex)
            {
                Log("创建计划任务失败: " + ex.Message);
                return false;
            }
        }

        private static bool RunTaskViaCom()
        {
            try
            {
                dynamic svc = GetTaskService();
                if (svc == null) return false;
                dynamic folder = svc.GetFolder("\\");
                dynamic task = folder.GetTask(TaskName);
                task.Run("");
                Log("已触发计划任务: " + TaskName);
                return true;
            }
            catch (Exception ex)
            {
                Log("触发计划任务失败: " + ex.Message);
                return false;
            }
        }

        private static bool DeleteTaskViaCom()
        {
            try
            {
                dynamic svc = GetTaskService();
                if (svc == null) return false;
                dynamic folder = svc.GetFolder("\\");
                folder.DeleteTask(TaskName, 0);
                Log("计划任务已删除");
                return true;
            }
            catch (Exception ex)
            {
                Log("删除计划任务失败: " + ex.Message);
                return false;
            }
        }

        private static string XmlEscape(string s)
        {
            return s.Replace("&", "&amp;").Replace("\"", "&quot;").Replace("<", "&lt;").Replace(">", "&gt;");
        }

        // ==================== 其他模式 ====================
        private static void RunCheck()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== " + AppTitle + " 诊断信息 v" + Version + " ===");
            sb.AppendLine("程序路径: " + ExePath);
            sb.AppendLine("战网客户端: " + ClientPath + "  存在=" + File.Exists(ClientPath));
            string regTest = "";
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Battle.net"))
                {
                    if (k != null)
                    {
                        object il = k.GetValue("InstallLocation");
                        regTest = il != null ? il.ToString() : "(无 InstallLocation)";
                    }
                    else regTest = "(子键不存在)";
                }
            }
            catch (Exception ex) { regTest = "错误: " + ex.Message; }
            sb.AppendLine("注册表直读测试(WOW6432Node\\Uninstall\\Battle.net): " + regTest);
            sb.AppendLine("产品代码: " + ProductCode);
            sb.AppendLine("配置: UseElevation=" + UseElevation +
                          " GameTimeoutSec=" + GameTimeoutSec + " WindowTimeoutSec=" + WindowTimeoutSec);
            sb.AppendLine("计划任务存在: " + TaskExistsViaCom());
            sb.AppendLine("当前进程提权: " + IsUserAdmin());
            sb.AppendLine("战网客户端运行中: " + IsClientRunning() + "  已提权: " + IsAnyClientElevated());
            sb.AppendLine("游戏进程运行中: " + IsGameRunning());
            List<WinInfo> wins = EnumClientWindows();
            sb.AppendLine("战网窗口数量: " + wins.Count);
            foreach (WinInfo w in wins.Take(15))
                sb.AppendLine("  窗口: '" + w.Title + "' 类=" + w.Class + " " + w.W + "x" + w.H + " 可见=" + w.Visible + " 最小化=" + w.Mini);
            string path = Path.Combine(DataDir, "check.txt");
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            Log("诊断已写入 " + path);
        }
    }
}
