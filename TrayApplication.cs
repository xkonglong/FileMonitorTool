using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace FileMonitorTool
{
    internal sealed class TrayApplication : IDisposable
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "FileMonitorTool";

        private readonly string _configPath;
        private readonly NotifyIcon _notifyIcon;
        private readonly ContextMenuStrip _menu;
        private readonly ToolStripMenuItem _autoStartItem;
        private readonly Icon _trayIcon;
        private readonly bool _ownsIcon;

        private Config _config;
        private FileMonitor _monitor;
        private ActionExecutor _executor;
        private bool _suppressAutoStartEvent;

        public TrayApplication()
        {
            _configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.ini");

            // 先加载图标（同名 .ico 优先，否则用 exe 自身图标）
            _trayIcon = LoadTrayIcon(out _ownsIcon);

            _menu = new ContextMenuStrip();
            _menu.Items.Add("打开配置文件", null, (s, e) => OpenConfig());
            _menu.Items.Add("重新加载配置", null, (s, e) => ReloadConfig(true));
            _menu.Items.Add("打开日志", null, (s, e) => OpenLog());
            _menu.Items.Add(new ToolStripSeparator());

            _autoStartItem = new ToolStripMenuItem("开机启动") { CheckOnClick = true };
            // 先设初始状态，再挂事件，避免初始化时触发写注册表
            _autoStartItem.Checked = IsAutoStartEnabled();
            _autoStartItem.CheckedChanged += OnAutoStartChanged;
            _menu.Items.Add(_autoStartItem);

            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add("退出", null, (s, e) => Exit());

            _notifyIcon = new NotifyIcon
            {
                Icon = _trayIcon,
                Text = "文件监控工具",
                Visible = true,
                ContextMenuStrip = _menu
            };
            _notifyIcon.DoubleClick += (s, e) => OpenConfig();

            ReloadConfig(false);
        }

        // ---------- 托盘图标加载 ----------

        private static Icon LoadTrayIcon(out bool ownsIcon)
        {
            ownsIcon = false;
            try
            {
                string exePath = Application.ExecutablePath;
                string icoPath = Path.ChangeExtension(exePath, ".ico");
                Debug.WriteLine(icoPath);

                if (File.Exists(icoPath))
                {
                    var icon = new Icon(icoPath);
                    ownsIcon = true;
                    Logger.Info("使用同名图标: " + icoPath);
                    return icon;
                }

                var exeIcon = Icon.ExtractAssociatedIcon(exePath);
                if (exeIcon != null)
                {
                    ownsIcon = true;
                    return exeIcon;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("加载托盘图标失败，改用系统默认图标: " + ex.Message);
            }
            return SystemIcons.Application;
        }

        // ---------- 开机启动 ----------

        private void OnAutoStartChanged(object sender, EventArgs e)
        {
            if (_suppressAutoStartEvent) return;

            if (!SetAutoStart(_autoStartItem.Checked))
            {
                // 写注册表失败：回滚勾选状态
                _suppressAutoStartEvent = true;
                _autoStartItem.Checked = !_autoStartItem.Checked;
                _suppressAutoStartEvent = false;
            }
        }

        private static bool IsAutoStartEnabled()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                {
                    if (key == null) return false;
                    var val = key.GetValue(RunValueName) as string;
                    if (string.IsNullOrEmpty(val)) return false;

                    string normalized = val.Trim().Trim('"');
                    return string.Equals(normalized, Application.ExecutablePath,
                        StringComparison.OrdinalIgnoreCase);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("读取开机启动状态失败: " + ex.Message);
                return false;
            }
        }

        private bool SetAutoStart(bool enable)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true)
                                 ?? Registry.CurrentUser.CreateSubKey(RunKeyPath))
                {
                    if (key == null)
                    {
                        Logger.Error("无法打开注册表 Run 键");
                        return false;
                    }

                    if (enable)
                        key.SetValue(RunValueName, "\"" + Application.ExecutablePath + "\"");
                    else
                        key.DeleteValue(RunValueName, false);
                }

                Logger.Info("开机启动已" + (enable ? "启用" : "禁用"));
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error("设置开机启动失败", ex);
                ShowBalloon("设置失败", ex.Message, ToolTipIcon.Error);
                return false;
            }
        }

        // ---------- 配置与监控 ----------

        private void ReloadConfig(bool showBalloon)
        {
            try
            {
                if (_monitor != null) { _monitor.Dispose(); _monitor = null; }

                if (!File.Exists(_configPath))
                    File.WriteAllText(_configPath, Config.DefaultText);

                _config = Config.Load(_configPath);
                Logger.Initialize(ResolveLogPath(_config.LogFile));
                _executor = new ActionExecutor(_config.ActionCommand);

                _monitor = new FileMonitor(_config, OnFileReady);
                _monitor.Start();

                _notifyIcon.Text = "文件监控工具 - 运行中";
                if (showBalloon)
                    ShowBalloon("配置已重载", _config.WatchPath, ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                Logger.Error("加载配置失败", ex);
                ShowBalloon("配置错误", ex.Message, ToolTipIcon.Error);
            }
        }

        private void OnFileReady(string filePath)
        {
            Logger.Info("检测到文件: " + filePath);
            try { _executor.Execute(filePath); }
            catch (Exception ex) { Logger.Error("提交动作失败: " + filePath, ex); }

            ShowBalloon("已处理", Path.GetFileName(filePath), ToolTipIcon.Info);
        }

        private void ShowBalloon(string title, string text, ToolTipIcon icon)
        {
            Task.Run(() =>
            {
                try { _notifyIcon.ShowBalloonTip(1500, title, text, icon); }
                catch { }
            });
        }

        private string ResolveLogPath(string log)
        {
            if (string.IsNullOrWhiteSpace(log)) log = "monitor.log";
            return Path.IsPathRooted(log)
                ? log
                : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, log);
        }

        // ---------- 菜单动作 ----------

        private void OpenConfig()
        {
            if (!File.Exists(_configPath))
                File.WriteAllText(_configPath, Config.DefaultText);

            Process.Start(new ProcessStartInfo("notepad.exe", "\"" + _configPath + "\"")
            {
                UseShellExecute = true
            });
        }

        private void OpenLog()
        {
            string p = ResolveLogPath(_config != null ? _config.LogFile : "monitor.log");
            if (File.Exists(p))
                Process.Start(new ProcessStartInfo("notepad.exe", "\"" + p + "\"") { UseShellExecute = true });
            else
                MessageBox.Show("日志文件尚未生成。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void Exit()
        {
            if (_monitor != null) { _monitor.Dispose(); _monitor = null; }

            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();

            if (_ownsIcon && _trayIcon != null)
            {
                try { _trayIcon.Dispose(); } catch { }
            }

            Application.ExitThread();
        }

        public void Dispose()
        {
            if (_monitor != null) { _monitor.Dispose(); _monitor = null; }
            try { _notifyIcon.Visible = false; } catch { }
            _notifyIcon.Dispose();
            _menu.Dispose();

            if (_ownsIcon && _trayIcon != null)
            {
                try { _trayIcon.Dispose(); } catch { }
            }
        }
    }
}