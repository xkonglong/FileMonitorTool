using System;
using System.Collections.Generic;
using System.IO;

namespace FileMonitorTool
{
    internal sealed class Config
    {
        public string WatchPath { get; set; }
        public string FileFilter { get; set; }
        public bool IncludeSubdirectories { get; set; }
        public bool WatchChanged { get; set; }
        public string ActionCommand { get; set; }
        public int DebounceMs { get; set; }
        public string LogFile { get; set; }

        public string[] Filters
        {
            get
            {
                string raw = string.IsNullOrWhiteSpace(FileFilter) ? "*.*" : FileFilter;
                return raw.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries);
            }
        }

        public static Config Load(string path)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (File.Exists(path))
            {
                foreach (string rawLine in File.ReadAllLines(path))
                {
                    string line = rawLine.Trim();
                    if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                    int idx = line.IndexOf('=');
                    if (idx <= 0) continue;
                    dict[line.Substring(0, idx).Trim()] = line.Substring(idx + 1).Trim();
                }
            }

            return new Config
            {
                WatchPath = Get(dict, "WatchPath", ""),
                FileFilter = Get(dict, "FileFilter", "*.*"),
                IncludeSubdirectories = GetBool(dict, "IncludeSubdirectories", false),
                WatchChanged = GetBool(dict, "WatchChanged", false),
                ActionCommand = Get(dict, "ActionCommand", ""),
                DebounceMs = GetInt(dict, "DebounceMs", 300),
                LogFile = Get(dict, "LogFile", "monitor.log"),
            };
        }

        private static string Get(Dictionary<string, string> d, string key, string def)
        {
            string v;
            return d.TryGetValue(key, out v) && !string.IsNullOrEmpty(v) ? v : def;
        }

        private static int GetInt(Dictionary<string, string> d, string key, int def)
        {
            int v;
            return d.TryGetValue(key, out var s) && int.TryParse(s, out v) ? v : def;
        }

        private static bool GetBool(Dictionary<string, string> d, string key, bool def)
        {
            bool v;
            return d.TryGetValue(key, out var s) && bool.TryParse(s, out v) ? v : def;
        }

        public const string DefaultText =
@"# ===== 文件监控工具配置 =====
# 修改保存后，右键托盘图标 -> ""重新加载配置"" 即可生效

# 要监控的文件夹（必须存在）
WatchPath=C:\DropFolder

# 文件类型过滤，分号分隔，支持 * 和 ? 通配符；*.* 表示全部
FileFilter=*.pdf;*.docx;*.xlsx

# 是否监控子目录
IncludeSubdirectories=false

# 是否监听""已有文件内容被修改""（默认只关注新放入/改名进来的文件）
WatchChanged=false

# 文件稳定后（去抖窗口结束）执行的动作，由 cmd /c 执行，支持占位符：
#   {filepath} 完整路径   {filename} 文件名   {directory} 所在目录   {ext} 扩展名
# 示例1：记录到临时文件
ActionCommand=echo 检测到文件: ""{filepath}"" >> ""%TEMP%\filemonitor_hits.txt""
# 示例2：复制到归档目录
#ActionCommand=xcopy /Y ""{filepath}"" ""D:\Archive\""
# 示例3：调用外部程序
#ActionCommand=""C:\Tools\MyTool.exe"" --input ""{filepath}""

# 去抖窗口（毫秒）：文件连续变化时，静默超过该时长才触发动作
DebounceMs=300

# 日志文件（相对路径基于程序目录）
LogFile=monitor.log
";
    }
}