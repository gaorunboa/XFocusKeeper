using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace ActiveWindowMonitor
{
    public class Form1 : Form
    {
        // ---------- Windows API 声明 ----------
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags,
            StringBuilder lpExeName, ref uint lpdwSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        private const int SW_MINIMIZE = 6;

        private const uint PROCESS_QUERY_INFORMATION = 0x0400;
        private const uint PROCESS_VM_READ = 0x0010;
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        // 控件
        private System.Windows.Forms.Timer timer;
        private TextBox txtInfo;
        private NotifyIcon trayIcon;
        private ContextMenuStrip trayMenu;
        private ToolStripMenuItem showInTaskbarMenuItem;
        private ToolStripMenuItem fontNameMenuItem;
        private ToolStripMenuItem fontSizeMenuItem;

        // 焦点保护相关
        private IntPtr lastGoodWindow = IntPtr.Zero;
        private DateTime lastSwitchTime = DateTime.MinValue;
        private const int switchCooldownMs = 500;
        private HashSet<string> blacklistProcessNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "msrdc.exe",
            "wsl.exe",
        };

        // 日志相关
        private static readonly object logLock = new object();
        private string logDirectory;
        private IntPtr lastLoggedWindow = IntPtr.Zero;

        // 设置文件路径（改为exe同目录）
        private string settingsFile;
        private string[] commonFonts = { "Consolas", "Courier New", "Lucida Console", "Fixedsys", "Microsoft YaHei" };
        private int[] commonFontSizes = { 9, 10, 11, 12, 14, 16, 18 };

        // 当前进程ID（用于判断自身是否在前台）
        private int currentProcessId;

        public Form1()
        {
            currentProcessId = Process.GetCurrentProcess().Id;

            // 配置文件直接放在exe所在目录
            settingsFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ActiveWindowMonitor.ini");
            TryMigrateOldSettings();

            // 初始化日志目录（放在exe同目录下的logs子文件夹）
            logDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            Directory.CreateDirectory(logDirectory);
            CleanOldLogs();

            SetupUI();
            SetupTray();
            SetupTimer();
            LoadSettings(); // 加载配置，包括窗口位置和尺寸

            // 再次确保位置和尺寸有效（解决DPI缩放等导致的偏移）
            this.Location = EnsureVisibleLocation(this.Location);
        }

        // 迁移旧版配置文件（如果存在且新配置文件不存在）
        private void TryMigrateOldSettings()
        {
            try
            {
                if (!File.Exists(settingsFile))
                {
                    string oldAppDataFolder = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "ActiveWindowMonitor");
                    string oldSettingsFile = Path.Combine(oldAppDataFolder, "settings.ini");
                    if (File.Exists(oldSettingsFile))
                    {
                        File.Copy(oldSettingsFile, settingsFile);
                    }
                }
            }
            catch { /* 迁移失败不影响主程序 */ }
        }

        // 专门用于调试图标加载的日志方法
        private void LogIconDebug(string message)
        {
            try
            {
                string debugLogPath = Path.Combine(logDirectory, "IconDebug.log");
                string entry = string.Format("[{0:yyyy-MM-dd HH:mm:ss}] {1}", DateTime.Now, message);
                File.AppendAllText(debugLogPath, entry + Environment.NewLine, Encoding.UTF8);
            }
            catch { /* 忽略写入错误 */ }
        }

        private void SetupUI()
        {
            // 加载与exe同名的ico文件（例如 XFocusKeeper.exe.ico）
            string exePath = Application.ExecutablePath;
            string iconPath = Path.ChangeExtension(exePath, ".ico");

            LogIconDebug(string.Format("尝试加载窗体图标，exe路径: {0}", exePath));
            LogIconDebug(string.Format("推测的图标路径: {0}", iconPath));
            LogIconDebug(string.Format("图标文件是否存在: {0}", File.Exists(iconPath)));

            try
            {
                if (File.Exists(iconPath))
                {
                    this.Icon = new Icon(iconPath);
                    LogIconDebug("窗体图标加载成功。");
                }
                else
                {
                    LogIconDebug("图标文件不存在，窗体将显示默认图标。");
                }
            }
            catch (Exception ex)
            {
                LogIconDebug(string.Format("窗体图标加载异常: {0}", ex.Message));
            }

            this.TopMost = true;
            this.Text = "活动窗口监视器 (顶层)";
            this.Size = new Size(550, 250);     // 默认初始尺寸
            this.StartPosition = FormStartPosition.Manual;
            this.ShowInTaskbar = false;

            txtInfo = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                WordWrap = true,
                ScrollBars = ScrollBars.Vertical,
                Dock = DockStyle.Fill,
                Font = new Font("Consolas", 11F),
                Text = "等待获取活动窗口信息..."
            };
            this.Controls.Add(txtInfo);

            this.FormClosing += Form1_FormClosing;
            this.Resize += Form1_Resize;
        }

        private void SetupTray()
        {
            trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add("显示窗口", null, ShowWindow_Click);

            showInTaskbarMenuItem = new ToolStripMenuItem("显示在任务栏");
            showInTaskbarMenuItem.CheckOnClick = true;
            showInTaskbarMenuItem.CheckedChanged += ShowInTaskbarMenuItem_CheckedChanged;
            trayMenu.Items.Add(showInTaskbarMenuItem);

            trayMenu.Items.Add("-");

            fontNameMenuItem = new ToolStripMenuItem("字体名称");
            foreach (string fontName in commonFonts)
            {
                var item = new ToolStripMenuItem(fontName, null, FontName_Click) { CheckOnClick = true };
                fontNameMenuItem.DropDownItems.Add(item);
            }
            trayMenu.Items.Add(fontNameMenuItem);

            fontSizeMenuItem = new ToolStripMenuItem("字体大小");
            foreach (int size in commonFontSizes)
            {
                var item = new ToolStripMenuItem(size.ToString(), null, FontSize_Click) { CheckOnClick = true };
                fontSizeMenuItem.DropDownItems.Add(item);
            }
            trayMenu.Items.Add(fontSizeMenuItem);

            trayMenu.Items.Add("-");

            trayMenu.Items.Add("在文件夹中定位EXE", null, LocateExeFile_Click);

            trayMenu.Items.Add("-");
            trayMenu.Items.Add("保存配置", null, SaveConfig_Click);
            trayMenu.Items.Add("应用配置", null, ApplyConfig_Click);

            // ===== 新增：关于 / 联系我 =====
            trayMenu.Items.Add("-");
            trayMenu.Items.Add("关于 / 联系我", null, About_Click);

            trayMenu.Items.Add("-");
            trayMenu.Items.Add("退出", null, Exit_Click);

            trayIcon = new NotifyIcon
            {
                Text = "活动窗口监视器",
                ContextMenuStrip = trayMenu,
                Visible = true
            };

            // 托盘图标同样使用与exe同名的ico
            string exePath = Application.ExecutablePath;
            string iconPath = Path.ChangeExtension(exePath, ".ico");
            LogIconDebug(string.Format("尝试加载托盘图标: {0}，存在: {1}", iconPath, File.Exists(iconPath)));
            try
            {
                if (File.Exists(iconPath))
                {
                    trayIcon.Icon = new Icon(iconPath);
                    LogIconDebug("托盘图标加载成功。");
                }
                else
                {
                    LogIconDebug("托盘图标文件不存在，将显示默认图标。");
                }
            }
            catch (Exception ex)
            {
                LogIconDebug(string.Format("托盘图标加载异常: {0}", ex.Message));
            }

            trayIcon.MouseClick += TrayIcon_MouseClick;
            txtInfo.ContextMenuStrip = trayMenu;
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            // 最终位置调整，确保可见（覆盖DPI变化等情况）
            if (this.StartPosition == FormStartPosition.Manual)
            {
                this.Location = EnsureVisibleLocation(this.Location);
            }
        }

        private void TrayIcon_MouseClick(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                ShowWindow_Click(sender, e);
            }
        }

        private void SetupTimer()
        {
            timer = new System.Windows.Forms.Timer { Interval = 1000, Enabled = true };
            timer.Tick += Timer_Tick;
        }

        private void Timer_Tick(object sender, EventArgs e)
        {
            if (Control.IsKeyLocked(Keys.CapsLock))
                return;

            IntPtr currentHwnd = GetForegroundWindow();
            if (currentHwnd == IntPtr.Zero)
                return;

            uint pid;
            GetWindowThreadProcessId(currentHwnd, out pid);

            // 如果前台窗口是本程序自身，则暂停刷新，方便用户复制
            if ((int)pid == currentProcessId)
            {
                return;
            }

            string procName = GetProcessNameById(pid);

            // 黑名单检测与焦点保护
            if (!string.IsNullOrEmpty(procName) && blacklistProcessNames.Contains(procName))
            {
                if ((DateTime.Now - lastSwitchTime).TotalMilliseconds < switchCooldownMs)
                    return;

                ShowWindow(currentHwnd, SW_MINIMIZE);

                if (lastGoodWindow != IntPtr.Zero && lastGoodWindow != currentHwnd && IsWindow(lastGoodWindow))
                {
                    SetForegroundWindow(lastGoodWindow);
                    lastSwitchTime = DateTime.Now;
                }
                return;
            }

            // 正常窗口：记录并更新信息
            lastGoodWindow = currentHwnd;
            UpdateActiveWindowInfo();
        }

        private string GetProcessNameById(uint pid)
        {
            try
            {
                using (var proc = Process.GetProcessById((int)pid))
                    return proc.ProcessName + ".exe";
            }
            catch
            {
                return null;
            }
        }

        private void UpdateActiveWindowInfo()
        {
            IntPtr hWnd = GetForegroundWindow();
            if (hWnd == IntPtr.Zero)
            {
                txtInfo.Text = "无法获取活动窗口句柄";
                return;
            }

            uint processId;
            GetWindowThreadProcessId(hWnd, out processId);
            if (processId == 0)
            {
                txtInfo.Text = string.Format("句柄: {0}\r\n无法获取进程ID", hWnd);
                return;
            }

            string exePath = GetProcessImageFileName(processId);
            string displayText = string.Format(
                "活动窗口句柄: {0}\r\n进程 ID: {1}\r\nEXE 路径: {2}",
                hWnd, processId, exePath);
            txtInfo.Text = displayText;

            // 仅当窗口句柄发生变化时才记录日志
            if (hWnd != lastLoggedWindow)
            {
                lastLoggedWindow = hWnd;
                string logEntry = string.Format("[{0:yyyy-MM-dd HH:mm:ss}] 句柄: {1} | PID: {2} | EXE: {3}",
                    DateTime.Now, hWnd, processId, exePath);
                WriteLog(logEntry);
            }
        }

        private string GetProcessImageFileName(uint processId)
        {
            try
            {
                using (Process proc = Process.GetProcessById((int)processId))
                    return proc.MainModule != null ? proc.MainModule.FileName : "无法获取路径（可能被保护）";
            }
            catch { }

            IntPtr hProcess = IntPtr.Zero;
            try
            {
                hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ, false, processId);
                if (hProcess == IntPtr.Zero)
                    hProcess = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, processId);

                if (hProcess != IntPtr.Zero)
                {
                    const uint dwFlags = 0;
                    StringBuilder buffer = new StringBuilder(1024);
                    uint size = (uint)buffer.Capacity;

                    if (QueryFullProcessImageName(hProcess, dwFlags, buffer, ref size))
                        return buffer.ToString();
                }
                return "无法获取路径（权限不足）";
            }
            catch
            {
                return "获取路径时出错";
            }
            finally
            {
                if (hProcess != IntPtr.Zero)
                    CloseHandle(hProcess);
            }
        }

        // ---------- 主功能日志写入 ----------
        private void WriteLog(string message)
        {
            lock (logLock)
            {
                try
                {
                    string logFileName = string.Format("ActiveWindow_{0:yyyyMMdd}.log", DateTime.Now);
                    string logFilePath = Path.Combine(logDirectory, logFileName);
                    File.AppendAllText(logFilePath, message + Environment.NewLine, Encoding.UTF8);
                }
                catch { /* 忽略日志写入错误，不影响主功能 */ }
            }
        }

        // ---------- 日志清理 ----------
        private void CleanOldLogs()
        {
            try
            {
                if (!Directory.Exists(logDirectory))
                    return;

                string[] logFiles = Directory.GetFiles(logDirectory, "ActiveWindow_*.log");
                if (logFiles.Length <= 30)
                    return;

                Array.Sort(logFiles);
                Array.Reverse(logFiles);

                for (int i = 30; i < logFiles.Length; i++)
                {
                    try
                    {
                        File.Delete(logFiles[i]);
                    }
                    catch { /* 忽略单个文件删除失败 */ }
                }
            }
            catch { /* 忽略清理异常 */ }
        }

        // ---------- 在文件夹中定位EXE ----------
        private void LocateExeFile_Click(object sender, EventArgs e)
        {
            try
            {
                IntPtr hWnd = GetForegroundWindow();
                if (hWnd == IntPtr.Zero)
                {
                    MessageBox.Show("无法获取当前活动窗口。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                uint pid;
                GetWindowThreadProcessId(hWnd, out pid);
                string exePath = GetProcessImageFileName(pid);

                if (string.IsNullOrEmpty(exePath) || exePath.StartsWith("无法") || exePath.StartsWith("获取"))
                {
                    MessageBox.Show("无法获取可执行文件路径。\n" + exePath, "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                Process.Start("explorer.exe", "/select,\"" + exePath + "\"");
            }
            catch (Exception ex)
            {
                MessageBox.Show("打开资源管理器时出错：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // ---------- 事件处理 ----------
        private void ShowWindow_Click(object sender, EventArgs e)
        {
            this.Show();
            this.WindowState = FormWindowState.Normal;
            this.TopMost = true;
        }

        private void ShowInTaskbarMenuItem_CheckedChanged(object sender, EventArgs e)
        {
            this.ShowInTaskbar = showInTaskbarMenuItem.Checked;
            SaveSettings();
        }

        private void FontName_Click(object sender, EventArgs e)
        {
            var clickedItem = sender as ToolStripMenuItem;
            if (clickedItem == null) return;

            string fontName = clickedItem.Text;
            float currentSize = txtInfo.Font.Size;
            txtInfo.Font = new Font(fontName, currentSize);

            foreach (ToolStripMenuItem item in fontNameMenuItem.DropDownItems)
                item.Checked = (item.Text == fontName);

            SaveSettings();
        }

        private void FontSize_Click(object sender, EventArgs e)
        {
            var clickedItem = sender as ToolStripMenuItem;
            if (clickedItem == null) return;

            float newSize;
            if (float.TryParse(clickedItem.Text, out newSize))
            {
                txtInfo.Font = new Font(txtInfo.Font.FontFamily, newSize);

                foreach (ToolStripMenuItem item in fontSizeMenuItem.DropDownItems)
                    item.Checked = (item.Text == clickedItem.Text);

                SaveSettings();
            }
        }

        private void SaveConfig_Click(object sender, EventArgs e)
        {
            SaveSettings();
            MessageBox.Show("配置已保存。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void ApplyConfig_Click(object sender, EventArgs e)
        {
            LoadSettings();
            MessageBox.Show("配置已应用。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // ===== 新增：关于对话框 =====
        private void About_Click(object sender, EventArgs e)
        {
            using (var about = new AboutForm())
            {
                about.ShowDialog(this);
            }
        }

        private void Exit_Click(object sender, EventArgs e)
        {
            SaveSettings();
            trayIcon.Visible = false;
            Application.Exit();
        }

        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                this.Hide();
            }
        }

        private void Form1_Resize(object sender, EventArgs e)
        {
            if (this.WindowState == FormWindowState.Minimized)
                this.Hide();
        }

        // ---------- 配置读写（增加宽/高支持） ----------
        private void LoadSettings()
        {
            try
            {
                if (!File.Exists(settingsFile))
                {
                    // 无配置文件时居中显示并采用默认尺寸
                    this.StartPosition = FormStartPosition.CenterScreen;
                    this.ShowInTaskbar = false;
                    showInTaskbarMenuItem.Checked = false;
                    this.Size = new Size(550, 250);
                    UpdateFontMenuChecks();
                    return;
                }

                string[] lines = File.ReadAllLines(settingsFile);
                int x = 0, y = 0, width = 550, height = 250;
                bool showInTaskbar = false;
                string fontName = "Consolas";
                float fontSize = 11f;

                foreach (string line in lines)
                {
                    if (line.StartsWith("X="))
                        int.TryParse(line.Substring(2), out x);
                    else if (line.StartsWith("Y="))
                        int.TryParse(line.Substring(2), out y);
                    else if (line.StartsWith("Width="))
                        int.TryParse(line.Substring(6), out width);
                    else if (line.StartsWith("Height="))
                        int.TryParse(line.Substring(7), out height);
                    else if (line.StartsWith("ShowInTaskbar="))
                        bool.TryParse(line.Substring(15), out showInTaskbar);
                    else if (line.StartsWith("FontName="))
                        fontName = line.Substring(9);
                    else if (line.StartsWith("FontSize="))
                        float.TryParse(line.Substring(9), out fontSize);
                }

                // 确保尺寸合理
                if (width < 100) width = 100;
                if (height < 100) height = 100;

                this.StartPosition = FormStartPosition.Manual;
                Point savedLocation = new Point(x, y);
                this.Location = EnsureVisibleLocation(savedLocation);
                this.Size = new Size(width, height);

                this.ShowInTaskbar = showInTaskbar;
                showInTaskbarMenuItem.Checked = showInTaskbar;

                try
                {
                    txtInfo.Font = new Font(fontName, fontSize);
                }
                catch { }

                UpdateFontMenuChecks();
            }
            catch
            {
                // 出错时使用默认值
                this.StartPosition = FormStartPosition.CenterScreen;
                this.ShowInTaskbar = false;
                showInTaskbarMenuItem.Checked = false;
                this.Size = new Size(550, 250);
                UpdateFontMenuChecks();
            }
        }

        private void SaveSettings()
        {
            try
            {
                string content = string.Format(
                    "X={0}\r\nY={1}\r\nWidth={2}\r\nHeight={3}\r\nShowInTaskbar={4}\r\nFontName={5}\r\nFontSize={6}",
                    this.Location.X,
                    this.Location.Y,
                    this.Size.Width,
                    this.Size.Height,
                    this.ShowInTaskbar,
                    txtInfo.Font.Name,
                    txtInfo.Font.Size);
                File.WriteAllText(settingsFile, content);
            }
            catch { }
        }

        private void UpdateFontMenuChecks()
        {
            string currentFontName = txtInfo.Font.Name;
            foreach (ToolStripMenuItem item in fontNameMenuItem.DropDownItems)
                item.Checked = (item.Text == currentFontName);

            float currentSize = txtInfo.Font.Size;
            string sizeStr = currentSize.ToString();
            foreach (ToolStripMenuItem item in fontSizeMenuItem.DropDownItems)
                item.Checked = (item.Text == sizeStr);
        }

        private Point EnsureVisibleLocation(Point location)
        {
            Rectangle bounds = new Rectangle(location, this.Size);
            foreach (Screen screen in Screen.AllScreens)
            {
                // 只要窗口有任意一部分在工作区内即视为有效
                if (screen.WorkingArea.IntersectsWith(bounds))
                    return location;
            }

            // 完全不在任何屏幕内则居中到主屏幕
            Rectangle primaryWorkingArea = Screen.PrimaryScreen.WorkingArea;
            return new Point(
                primaryWorkingArea.Left + (primaryWorkingArea.Width - this.Width) / 2,
                primaryWorkingArea.Top + (primaryWorkingArea.Height - this.Height) / 2);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            SaveSettings();
            base.OnFormClosed(e);
        }

        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new Form1());
        }
    }

    // ========== 新增：关于窗口 ==========
    public class AboutForm : Form
    {
        public AboutForm()
        {
            this.Text = "关于 XFocusKeeper";
            this.Size = new Size(400, 300);
            this.StartPosition = FormStartPosition.CenterParent;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;

            TextBox txt = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                Dock = DockStyle.Fill,
                WordWrap = true,
                ScrollBars = ScrollBars.Vertical,
                Font = new Font("Consolas", 10),
                Text = "加载中..."
            };
            this.Controls.Add(txt);

            // 尝试加载同目录下的 contact.txt
            string contactPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "contact.txt");
            if (File.Exists(contactPath))
            {
                try
                {
                    txt.Text = File.ReadAllText(contactPath, Encoding.UTF8);
                }
                catch
                {
                    txt.Text = "无法读取联系方式文件。";
                }
            }
            else
            {
                // 兜底文本，使用 laogao2026@qq.com 作为主邮箱
                txt.Text =
                    "=== 联系作者 ===\r\n" +
                    "邮箱：laogao2026@qq.com\r\n" +
                    "QQ：1975880301\r\n" +
                    "微信：gaorunbo2020\r\n" +
                    "GitHub：https://github.com/gaorunboa/XFocusKeeper";
            }
        }
    }
}