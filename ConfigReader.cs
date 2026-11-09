using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace FileMonitorTool
{
    /// <summary>配置非法时抛出，调用方应停止加载并提示用户。</summary>
    internal sealed class ConfigException : Exception
    {
        public ConfigException(string message) : base(message) { }
    }

    internal sealed class ActionRule
    {
        public string Name { get; set; }
        public int Order { get; set; }
        public string[] Patterns { get; set; }
        public string Command { get; set; }
        public string RawPattern { get; set; }
        public int FolderOrder { get; set; }
    }

    internal sealed class FolderConfig
    {
        public int Order { get; set; }
        public string Name { get; set; }
        public string Path { get; set; }
        public bool IncludeSub { get; set; }
        public bool WatchChanged { get; set; }
        public int DebounceMs { get; set; }
        public List<ActionRule> Rules { get; set; }

        public FolderConfig()
        {
            Rules = new List<ActionRule>();
        }
    }

    internal sealed class Config
    {
        public bool IncludeSub { get; set; }
        public bool WatchChanged { get; set; }
        public int DebounceMs { get; set; }
        public string LogFile { get; set; }
        public int MaxConcurrentActions { get; set; }
        public bool LogUnmatched { get; set; }

        public List<FolderConfig> Folders { get; set; }
        public List<ActionRule> GlobalRules { get; set; }

        private readonly List<string> _warnings = new List<string>();
        public IList<string> Warnings { get { return _warnings; } }

        public Config()
        {
            Folders = new List<FolderConfig>();
            GlobalRules = new List<ActionRule>();
        }

        public ActionRule MatchRule(string filePath, int folderOrder)
        {
            string ext = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();

            if (folderOrder > 0)
            {
                var folder = Folders.FirstOrDefault(f => f.Order == folderOrder);
                if (folder != null)
                {
                    var hit = MatchInList(folder.Rules, ext);
                    if (hit != null) return hit;
                }
            }

            return MatchInList(GlobalRules, ext);
        }

        private static ActionRule MatchInList(List<ActionRule> rules, string ext)
        {
            foreach (var rule in rules)
            {
                foreach (var pattern in rule.Patterns)
                {
                    if (pattern == "*") return rule;
                    if (WildcardMatch(ext, pattern)) return rule;
                }
            }
            return null;
        }

        public bool ShouldTrackFile(string fileName, int folderOrder)
        {
            if (string.IsNullOrEmpty(fileName)) return false;
            string ext = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();

            if (folderOrder > 0)
            {
                var folder = Folders.FirstOrDefault(f => f.Order == folderOrder);
                if (folder != null && AnyPatternMatches(folder.Rules, ext)) return true;
            }

            return AnyPatternMatches(GlobalRules, ext);
        }

        private static bool AnyPatternMatches(List<ActionRule> rules, string ext)
        {
            foreach (var rule in rules)
            {
                foreach (var pattern in rule.Patterns)
                {
                    if (pattern == "*") return true;
                    if (WildcardMatch(ext, pattern)) return true;
                }
            }
            return false;
        }

        // ---- 带行号的配置项 ----
        private sealed class ConfigEntry
        {
            public string Value;
            public int LineNo;
            public ConfigEntry(string v, int n) { Value = v; LineNo = n; }
        }

        public static Config Load(string path)
        {
            if (!File.Exists(path))
                throw new ConfigException("配置文件不存在: " + path);

            var cfg = new Config();

            var sections = new Dictionary<string, Dictionary<string, ConfigEntry>>(StringComparer.OrdinalIgnoreCase);
            var duplicateKeys = new List<string>();

            string currentSection = null;
            int lineNo = 0;

            foreach (string rawLine in File.ReadAllLines(path))
            {
                lineNo++;
                string line = rawLine.Trim();
                if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;

                if (line[0] == '[')
                {
                    if (line[line.Length - 1] != ']')
                        throw new ConfigException(string.Format("第 {0} 行：节声明缺少 ']'。", lineNo));

                    currentSection = line.Substring(1, line.Length - 2).Trim();
                    if (currentSection.Length == 0)
                        throw new ConfigException(string.Format("第 {0} 行：节名不能为空。", lineNo));

                    if (!sections.ContainsKey(currentSection))
                        sections[currentSection] = new Dictionary<string, ConfigEntry>(StringComparer.OrdinalIgnoreCase);
                    continue;
                }

                int idx = line.IndexOf('=');
                if (idx <= 0)
                    throw new ConfigException(string.Format("第 {0} 行：缺少 '=' 或键名为空。", lineNo));

                if (currentSection == null)
                    throw new ConfigException(string.Format("第 {0} 行：键值必须写在某个节内（如 [Global] 或 [Folder:名字]）。", lineNo));

                string key = line.Substring(0, idx).Trim();
                string value = line.Substring(idx + 1).Trim();

                if (key.Length == 0)
                    throw new ConfigException(string.Format("第 {0} 行：键名为空。", lineNo));

                var target = sections[currentSection];

                if (target.ContainsKey(key))
                    duplicateKeys.Add("[" + currentSection + "] " + key);

                target[key] = new ConfigEntry(value, lineNo);
            }

            if (sections.Count == 0)
                throw new ConfigException("配置文件为空，没有任何节。");

            // ---- Global 节 ----
            Dictionary<string, ConfigEntry> global;
            if (!sections.TryGetValue("Global", out global))
                throw new ConfigException("缺少 [Global] 节。");

            cfg.IncludeSub = GetBool(global, "IncludeSub", false);
            cfg.WatchChanged = GetBool(global, "WatchChanged", false);
            cfg.DebounceMs = GetInt(global, "DebounceMs", 300);
            cfg.LogFile = GetStr(global, "LogFile", "monitor.log");
            cfg.MaxConcurrentActions = GetInt(global, "MaxConcurrentActions", 4);
            cfg.LogUnmatched = GetBool(global, "LogUnmatched", true);

            if (cfg.DebounceMs < 10)
                throw new ConfigException("[Global] DebounceMs 必须 >= 10。");
            if (cfg.MaxConcurrentActions < 1)
                throw new ConfigException("[Global] MaxConcurrentActions 必须 >= 1。");

            foreach (var k in duplicateKeys.Distinct(StringComparer.OrdinalIgnoreCase))
                cfg._warnings.Add(string.Format("配置项 {0} 重复出现，只有最后一次的值生效。", k));

            // ---- Global 节内的全局规则 ----
            var globalReserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "IncludeSub", "WatchChanged", "DebounceMs",
                "LogFile", "MaxConcurrentActions", "LogUnmatched"
            };
            ParseRules(global, globalReserved, cfg.GlobalRules, 0, cfg._warnings, null);

            // ---- Folder 节 ----
            int folderOrder = 0;
            foreach (var kv in sections)
            {
                string sectionName = kv.Key;
                if (!sectionName.StartsWith("Folder:", StringComparison.OrdinalIgnoreCase)) continue;

                string folderName = sectionName.Substring("Folder:".Length).Trim();
                if (folderName.Length == 0)
                    throw new ConfigException("存在空的 [Folder:] 节。");

                var sec = kv.Value;
                ConfigEntry pathEntry;
                if (!sec.TryGetValue("Path", out pathEntry) || string.IsNullOrWhiteSpace(pathEntry.Value))
                    throw new ConfigException(string.Format("[Folder:{0}] 缺少 Path 或 Path 为空。", folderName));

                folderOrder++;
                var folder = new FolderConfig
                {
                    Order = folderOrder,
                    Name = folderName,
                    Path = pathEntry.Value.Trim(),
                    IncludeSub = cfg.IncludeSub,
                    WatchChanged = cfg.WatchChanged,
                    DebounceMs = cfg.DebounceMs,
                };

                ConfigEntry ce; bool b; int i;
                if (sec.TryGetValue("IncludeSub", out ce))
                {
                    if (!bool.TryParse(ce.Value, out b))
                        throw new ConfigException(string.Format("[Folder:{0}] IncludeSub 必须是 true 或 false。", folderName));
                    folder.IncludeSub = b;
                }

                if (sec.TryGetValue("WatchChanged", out ce))
                {
                    if (!bool.TryParse(ce.Value, out b))
                        throw new ConfigException(string.Format("[Folder:{0}] WatchChanged 必须是 true 或 false。", folderName));
                    folder.WatchChanged = b;
                }

                if (sec.TryGetValue("DebounceMs", out ce))
                {
                    if (!int.TryParse(ce.Value, out i) || i < 10)
                        throw new ConfigException(string.Format("[Folder:{0}] DebounceMs 必须是 >= 10 的整数。", folderName));
                    folder.DebounceMs = i;
                }

                var folderReserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "Path", "IncludeSub", "WatchChanged", "DebounceMs"
                };
                ParseRules(sec, folderReserved, folder.Rules, folderOrder, cfg._warnings, folderName);

                cfg.Folders.Add(folder);
            }

            if (cfg.Folders.Count == 0)
                throw new ConfigException("没有任何有效的 [Folder:xxx] 节，无法启动监控。");

            return cfg;
        }

        /// <summary>
        /// 解析节内规则：裸键 K=V 为匹配模式，K.Command=V 为命令。
        /// 严格按行号排序，书写顺序即优先级。
        /// </summary>
        private static void ParseRules(
            Dictionary<string, ConfigEntry> sec,
            HashSet<string> reserved,
            List<ActionRule> target,
            int folderOrder,
            List<string> warnings,
            string folderName)
        {
            string prefix = folderName == null ? "[Global]" : "[Folder:" + folderName + "]";

            var ruleEntries = new List<KeyValuePair<string, ConfigEntry>>();

            foreach (var kv in sec)
            {
                string key = kv.Key;
                if (reserved.Contains(key)) continue;
                if (key.EndsWith(".Command", StringComparison.OrdinalIgnoreCase)) continue;

                if (key.IndexOf('.') >= 0)
                {
                    warnings.Add(string.Format(
                        "{0} 键 '{1}' 含 '.'，规则名不能含点，已忽略。", prefix, key));
                    continue;
                }

                ruleEntries.Add(kv);
            }

            ruleEntries.Sort((a, b) => a.Value.LineNo.CompareTo(b.Value.LineNo));

            int order = 0;
            foreach (var kv in ruleEntries)
            {
                string ruleName = kv.Key;
                string rawPattern = kv.Value.Value;

                ConfigEntry cmdEntry;
                string cmdKey = ruleName + ".Command";
                if (!sec.TryGetValue(cmdKey, out cmdEntry) || string.IsNullOrWhiteSpace(cmdEntry.Value))
                {
                    warnings.Add(string.Format("{0} 规则 '{1}' 缺少 .Command，已忽略。", prefix, ruleName));
                    continue;
                }

                var patterns = ParseExtPatterns(rawPattern);
                if (patterns.Length == 0)
                {
                    warnings.Add(string.Format("{0} 规则 '{1}' 的模式为空，已忽略。", prefix, ruleName));
                    continue;
                }

                order++;
                target.Add(new ActionRule
                {
                    Name = ruleName,
                    Order = order,
                    Patterns = patterns,
                    Command = cmdEntry.Value,
                    RawPattern = rawPattern,
                    FolderOrder = folderOrder,
                });
            }

            // 孤立 .Command
            foreach (var kv in sec)
            {
                string key = kv.Key;
                if (reserved.Contains(key)) continue;
                if (!key.EndsWith(".Command", StringComparison.OrdinalIgnoreCase)) continue;
                string baseName = key.Substring(0, key.Length - ".Command".Length);
                if (!sec.ContainsKey(baseName))
                {
                    warnings.Add(string.Format(
                        "{0} 键 '{1}' 没有对应的模式键 '{2}'，已忽略。", prefix, key, baseName));
                }
            }
        }

        private static string[] ParseExtPatterns(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return new string[0];
            var result = new List<string>();

            foreach (var part in raw.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string s = part.Trim();
                if (s.Length == 0) continue;

                if (s == "*" || s == "*.*") { result.Add("*"); continue; }
                if (s.StartsWith("*.")) s = s.Substring(2);
                else if (s.StartsWith("*")) s = s.Substring(1);

                s = s.TrimStart('.').Trim().ToLowerInvariant();
                if (s.Length > 0) result.Add(s);
            }

            return result.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private static bool WildcardMatch(string input, string pattern)
        {
            if (pattern == "*") return true;
            if (pattern.IndexOf('*') < 0 && pattern.IndexOf('?') < 0)
                return string.Equals(input, pattern, StringComparison.OrdinalIgnoreCase);

            string regex = "^" + Regex.Escape(pattern)
                .Replace("\\*", ".*")
                .Replace("\\?", ".") + "$";
            return Regex.IsMatch(input, regex, RegexOptions.IgnoreCase);
        }

        private static string GetStr(Dictionary<string, ConfigEntry> d, string key, string def)
        {
            ConfigEntry e;
            return d.TryGetValue(key, out e) && !string.IsNullOrEmpty(e.Value) ? e.Value : def;
        }

        private static int GetInt(Dictionary<string, ConfigEntry> d, string key, int def)
        {
            ConfigEntry e;
            int v;
            return d.TryGetValue(key, out e) && int.TryParse(e.Value, out v) ? v : def;
        }

        private static bool GetBool(Dictionary<string, ConfigEntry> d, string key, bool def)
        {
            ConfigEntry e;
            bool v;
            return d.TryGetValue(key, out e) && bool.TryParse(e.Value, out v) ? v : def;
        }

        public const string DefaultText =
@"# =====================================================================
#  FileMonitorTool 配置文件
# =====================================================================
# 修改保存后，右键托盘图标 -> ""重新加载配置"" 即可生效，无需重启程序。
#
# 格式约定：
#   - 以 # 或 ; 开头的行是注释，空行忽略。
#   - 所有键值必须写在某个节内，节用 [节名] 声明。
#   - 同一个节内的同一个键只能出现一次，重复写只有最后一行生效。
#   - 节内规则的优先级严格按书写顺序：越靠前越优先，兜底 '*' 规则写在最后。
#   - 任何结构错误（缺少 [Global]、[Folder:x] 缺少 Path、true/false 写错、
#     数字非法等）都会拒绝启动，并在日志和托盘气泡里提示原因。
#   - 单条规则写错（缺 .Command、规则名含点等）只跳过该规则，不影响其他规则。

# =====================================================================
#  [Global] 全局设置节（必需）
# =====================================================================
# 这里的值作为所有文件夹的默认值；[Folder:x] 节里可以单独覆盖其中四项。

[Global]

# 是否递归监控子目录。默认 false。
#   false = 只监控 Path 这一层，子文件夹里的变化不触发。
#   true  = 递归所有层级的子文件夹。
# 可被 [Folder:x] 节里的 IncludeSub  覆盖。
IncludeSub =false

# 是否监听""已有文件内容被修改""。默认 false。
#   false = 只关注新放入、改名进来的文件（大多数场景够用，事件量最小）。
#   true  = 文件内容被改写时也触发（比如编辑器保存已有文件）。
# 注意：开启后事件量明显增加，可能对同一文件重复触发，建议配合较大的 DebounceMs。
# 可被 [Folder:x] 节里的 WatchChanged 覆盖。
WatchChanged=false

# 去抖窗口（毫秒），默认 300，最小 10。
# 含义：文件连续变化时，静默超过这个时长才认为""文件已稳定""，然后触发规则。
# 作用：合并同一文件的连续事件（复制大文件、编辑器多次保存会产生多个事件）。
# 调大：批量放文件时减少重复触发，但动作延迟增加。
# 调小：响应更快，但更容易对同一文件重复执行。
# 可被 [Folder:x] 节里的 DebounceMs 覆盖。
DebounceMs=300

# 同时执行的最大动作数，默认 4，最小 1。
# 一批文件同时触发时，超出上限的动作会排队，避免瞬间拉起大量进程。
# 只在 [Global] 设置，不能被文件夹覆盖。
MaxConcurrentActions=4

# 未匹配任何规则的文件是否记日志，默认 true。
#   true  = 日志里输出""未匹配任何规则，已忽略: 路径""，便于排查配置。
#   false = 静默丢弃，不记日志。
# 只在 [Global] 设置，不能被文件夹覆盖。
LogUnmatched=true

# 日志文件路径，默认 monitor.log。
# 相对路径基于 exe 所在目录；绝对路径按原样使用。
# 日志超过 1MB 自动轮转为 monitor.log.1（只保留一份备份）。
# 只在 [Global] 设置，不能被文件夹覆盖。
LogFile=monitor.log

# ---------------------------------------------------------------------
# 全局规则（可选）：写在 [Global] 节里、上面保留键之外的裸键。
# 所有文件夹都会先查自己的规则，都不中时再查这里。
# 写法与文件夹内规则相同：规则名=扩展名列表 + 规则名.Command=命令。
# ---------------------------------------------------------------------
# Log=log
# Log.Command=copy /Y ""{filepath}"" ""D:\Archive\Logs\{basename}_{timestamp}.{ext}""

# =====================================================================
#  [Folder:名字] 监控文件夹节
# =====================================================================
# 每个要监控的文件夹写一个 [Folder:名字] 节，""名字"" 自取，用于日志和提示。
# 可以写任意多个，按书写顺序编号。
#
# 节内保留键（不能当规则名用）：
#   Path        必填，监控目录的绝对路径。目录不存在时该文件夹会被跳过，
#               其余文件夹继续运行，日志和气泡会提示。
#   IncludeSub      可选，true/false，覆盖全局 IncludeSub 。
#   WatchChanged 可选，true/false，覆盖全局 WatchChanged。
#   DebounceMs  可选，整数 >= 10，覆盖全局 DebounceMs。
#
# 节内其他键都是规则，必须成对出现：
#   规则名=扩展名列表          （规则名不能含点）
#   规则名.Command=命令行
# 规则按书写顺序决定优先级；同一文件命中第一条就停止，后面的不再看。
#
# 可用占位符：
#   {filepath}   完整路径              如 D:\In\报告.docx
#   {filename}   文件名（含扩展名）     如 报告.docx
#   {basename}   文件名（不含扩展名）   如 报告
#   {directory}  所在目录              如 D:\In
#   {ext}        扩展名（不含点）       如 docx
#   {timestamp}  时间戳 yyyyMMdd_HHmmss 如 20261109_143012
#
# 示例：复制到 D:\down 并加时间戳，不覆盖已有文件
#   copy /Y ""{filepath}"" ""D:\down\{basename}_{timestamp}.{ext}""
# =====================================================================

[Folder:WeChat]
# 微信接收目录。把""用户名""替换成你实际的账号目录名。
Path=D:\xwechat_files\用户名\msg\file\2026-11

# 递归子文件夹，因为文件可能落在会话子目录里。
IncludeSub =true

# 去抖 1 秒，给微信写完文件留足时间。
DebounceMs=1000

# 文档类：复制到 D:\down，加时间戳避免覆盖，源文件保留。
# 如需移动而非复制，把 copy 改成 move。
Doc=doc;docx;xls;xlsx;ppt;pptx;pdf;txt;csv
Doc.Command=copy /Y ""{filepath}"" ""D:\down\{basename}_{timestamp}.{ext}""

[Folder:Down]
# 下载目录，只监控当前层，不递归。
Path=D:\down
IncludeSub =false

# zip 文件：调用 UniExtract 解压到同目录子文件夹（/sub 参数）。
Zip=zip
Zip.Command=""D:\Tools\SystemTools\万能解压\UniExtract.exe"" ""{filepath}"" /sub
";
    }
}
