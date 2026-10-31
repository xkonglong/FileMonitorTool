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
        private const string TrayTextRunning = "文件监控工具 - 运行中";
        private const string TrayTextStopped = "文件监控工具 - 已停止";

        // 与 csproj 中 <EmbeddedResource ... LogicalName="FileMonitorTool.stop.ico" /> 保持一致
        private const string StoppedIconResourceName = "FileMonitorTool.stop.ico";

        private readonly string _configPath;
        private readonly NotifyIcon _notifyIcon;
        private readonly ContextMenuStrip _menu;
        private readonly ToolStripMenuItem _toggleMonitorItem;
        private readonly ToolStripMenuItem _autoStartItem;

        private readonly Icon _runningIcon;
        private readonly bool _ownsRunningIcon;

        private readonly Icon _stoppedIcon;
        private readonly bool _ownsStoppedIcon;

        private Config _config;
        private FileMonitor _monitor;
        private ActionExecutor _executor;
        private bool _suppressAutoStartEvent;

        public TrayApplication()
        {
            _configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.ini");

            _runningIcon = LoadRunningIcon(out _ownsRunningIcon);
            _stoppedIcon = LoadStoppedIcon(out _ownsStoppedIcon);

            _menu = new ContextMenuStrip();
            _menu.Items.Add("打开配置文件", null, (s, e) => OpenConfig());
            _menu.Items.Add("重新加载配置", null, (s, e) => ReloadConfig(true));
            _menu.Items.Add("打开日志", null, (s, e) => OpenLog());
            _menu.Items.Add(new ToolStripSeparator());

            _toggleMonitorItem = new ToolStripMenuItem("停止监控");
            _toggleMonitorItem.Click += (s, e) => ToggleMonitor();
            _menu.Items.Add(_toggleMonitorItem);

            _autoStartItem = new ToolStripMenuItem("开机启动") { CheckOnClick = true };
            _autoStartItem.Checked = IsAutoStartEnabled();
            _autoStartItem.CheckedChanged += OnAutoStartChanged;
            _menu.Items.Add(_autoStartItem);

            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add("退出", null, (s, e) => Exit());

            _notifyIcon = new NotifyIcon
            {
                Icon = _stoppedIcon ?? _runningIcon,
                Text = TrayTextStopped,
                Visible = true,
                ContextMenuStrip = _menu
            };
            _notifyIcon.DoubleClick += (s, e) => OpenConfig();

            ReloadConfig(false);
        }

        // ---------- 图标加载 ----------

        private static Icon LoadRunningIcon(out bool ownsIcon)
        {
            ownsIcon = false;
            try
            {
                string exePath = Application.ExecutablePath;
                string dir = Path.GetDirectoryName(exePath) ?? AppDomain.CurrentDomain.BaseDirectory;
                string baseName = Path.GetFileNameWithoutExtension(exePath);
                string icoPath = Path.Combine(dir, baseName + ".ico");

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
                Logger.Warn("加载运行图标失败，改用系统默认图标: " + ex.Message);
            }
            return SystemIcons.Application;
        }

        /// <summary>
        /// 停止态图标：从程序集嵌入资源（原始 ico 流）读取。
        /// 取一次后缓存，由本类负责释放。资源缺失时返回 null，调用方回退到运行图标。
        /// </summary>
        private static Icon LoadStoppedIcon(out bool ownsIcon)
        {
            ownsIcon = false;
            try
            {
                var asm = typeof(TrayApplication).Assembly;
                using (var stream = asm.GetManifestResourceStream(StoppedIconResourceName))
                {
                    if (stream != null)
                    {
                        var icon = new Icon(stream);   // 构造时读完整流，之后关闭流安全
                        ownsIcon = true;
                        return icon;
                    }
                }
                Logger.Warn("嵌入资源 '" + StoppedIconResourceName + "' 不存在，停止时将沿用运行图标。");
            }
            catch (Exception ex)
            {
                Logger.Warn("加载停止图标失败，停止时将沿用运行图标: " + ex.Message);
            }
            return null;
        }

        // ---------- 监控开关 ----------

        private void ToggleMonitor()
        {
            if (_monitor == null)
            {
                ReloadConfig(true);
                return;
            }

            if (_monitor.IsRunning)
            {
                _monitor.Stop();
                Logger.Info("监控已停止");
                ShowBalloon("监控已停止", _config != null ? _config.WatchPath : "", ToolTipIcon.Info);
            }
            else
            {
                try
                {
                    _monitor.Start();
                    ShowBalloon("监控已启动", _config != null ? _config.WatchPath : "", ToolTipIcon.Info);
                }
                catch (Exception ex)
                {
                    Logger.Error("启动监控失败", ex);
                    ShowBalloon("启动失败", ex.Message, ToolTipIcon.Error);
                }
            }

            UpdateMonitorMenuState();
        }

        private void UpdateMonitorMenuState()
        {
            bool running = _monitor != null && _monitor.IsRunning;

            _toggleMonitorItem.Text = running ? "停止监控" : "开始监控";
            _notifyIcon.Text = running ? TrayTextRunning : TrayTextStopped;

            Icon target = running ? _runningIcon : (_stoppedIcon ?? _runningIcon);
            if (!ReferenceEquals(_notifyIcon.Icon, target) && target != null)
                _notifyIcon.Icon = target;
        }

        // ---------- 开机启动 ----------

        private void OnAutoStartChanged(object sender, EventArgs e)
        {
            if (_suppressAutoStartEvent) return;

            if (!SetAutoStart(_autoStartItem.Checked))
            {
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
            bool shouldRun = (_monitor == null) || _monitor.IsRunning;

            try
            {
                if (_monitor != null) { _monitor.Dispose(); _monitor = null; }

                if (!File.Exists(_configPath))
                    File.WriteAllText(_configPath, Config.DefaultText);

                _config = Config.Load(_configPath);
                Logger.Initialize(ResolveLogPath(_config.LogFile));

                if (!string.IsNullOrEmpty(_config.LegacyWarning))
                    Logger.Warn(_config.LegacyWarning);
                foreach (var w in _config.Warnings)
                    Logger.Warn("[配置校验] " + w);

                if (_executor != null) { _executor.Dispose(); _executor = null; }
                _executor = new ActionExecutor(_config);

                _monitor = new FileMonitor(_config, OnFileReady);

                if (shouldRun)
                    _monitor.Start();
                else
                    Logger.Info("配置已重载，监控保持停止状态");

                bool noRules = _config.Rules.Count == 0;
                if (noRules)
                {
                    Logger.Warn("当前没有任何有效规则，不会监控任何文件。");
                    ShowBalloon("没有有效规则",
                        "请检查 config.ini：每条规则需要同时有 Ext/Name 和 Command。",
                        ToolTipIcon.Warning);
                }
                else if (showBalloon)
                {
                    ShowBalloon("配置已重载",
                        shouldRun ? _config.WatchPath : "监控保持停止",
                        ToolTipIcon.Info);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("加载配置失败", ex);
                ShowBalloon("配置错误", ex.Message, ToolTipIcon.Error);
            }
            finally
            {
                UpdateMonitorMenuState();
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
            if (_executor != null) { _executor.Dispose(); _executor = null; }

            _notifyIcon.Visible = false;
            Application.ExitThread();
        }

        public void Dispose()
        {
            if (_monitor != null) { _monitor.Dispose(); _monitor = null; }
            if (_executor != null) { _executor.Dispose(); _executor = null; }

            try { _notifyIcon.Visible = false; } catch { }
            try { _notifyIcon.Dispose(); } catch { }
            try { _menu.Dispose(); } catch { }

            if (_ownsRunningIcon && _runningIcon != null)
            {
                try { _runningIcon.Dispose(); } catch { }
            }
            if (_ownsStoppedIcon && _stoppedIcon != null)
            {
                try { _stoppedIcon.Dispose(); } catch { }
            }
        }
    }
}