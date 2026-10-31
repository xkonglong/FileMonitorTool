using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace FileMonitorTool
{
    internal enum RuleMatchType
    {
        Ext,
        Name
    }

    internal sealed class ActionRule
    {
        public int Order { get; set; }
        public RuleMatchType MatchType { get; set; }
        public string[] Patterns { get; set; }
        public string Command { get; set; }
        public string RawPattern { get; set; }
    }

    internal sealed class Config
    {
        public string WatchPath { get; set; }
        public bool IncludeSubdirectories { get; set; }
        public bool WatchChanged { get; set; }
        public int DebounceMs { get; set; }
        public string LogFile { get; set; }
        public int MaxConcurrentActions { get; set; }
        public bool LogUnmatched { get; set; }
        public List<ActionRule> Rules { get; set; }

        private string _legacyWarning;
        private readonly List<string> _warnings = new List<string>();
        private string[] _extPatterns = new string[0];
        private string[] _namePatterns = new string[0];

        public string LegacyWarning { get { return _legacyWarning; } }
        public IList<string> Warnings { get { return _warnings; } }

        public Config()
        {
            Rules = new List<ActionRule>();
        }

        public static Config Load(string path)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var duplicateKeys = new List<string>();

            if (File.Exists(path))
            {
                foreach (string rawLine in File.ReadAllLines(path))
                {
                    string line = rawLine.Trim();
                    if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                    int idx = line.IndexOf('=');
                    if (idx <= 0) continue;

                    string key = line.Substring(0, idx).Trim();
                    string value = line.Substring(idx + 1).Trim();

                    if (dict.ContainsKey(key))
                        duplicateKeys.Add(key);

                    dict[key] = value;
                }
            }

            var cfg = new Config
            {
                WatchPath = Get(dict, "WatchPath", ""),
                IncludeSubdirectories = GetBool(dict, "IncludeSubdirectories", false),
                WatchChanged = GetBool(dict, "WatchChanged", false),
                DebounceMs = GetInt(dict, "DebounceMs", 300),
                LogFile = Get(dict, "LogFile", "monitor.log"),
                MaxConcurrentActions = GetInt(dict, "MaxConcurrentActions", 4),
                LogUnmatched = GetBool(dict, "LogUnmatched", true),
            };

            if (cfg.MaxConcurrentActions < 1) cfg.MaxConcurrentActions = 1;

            // ---- 重复 key 警告 ----
            foreach (var k in duplicateKeys.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                cfg._warnings.Add(string.Format(
                    "配置项 '{0}' 重复出现，只有最后一次的值生效，请删除多余的行。", k));
            }

            // ---- 先按 N 收集原始值 ----
            var rawExt  = new Dictionary<int, string>();
            var rawName = new Dictionary<int, string>();
            var rawCmd  = new Dictionary<int, string>();

            foreach (var kv in dict)
            {
                if (!kv.Key.StartsWith("Action.", StringComparison.OrdinalIgnoreCase)) continue;
                string rest = kv.Key.Substring("Action.".Length);
                int dot = rest.IndexOf('.');
                if (dot <= 0) continue;

                int n;
                if (!int.TryParse(rest.Substring(0, dot), out n)) continue;
                string field = rest.Substring(dot + 1);

                if (string.Equals(field, "Ext", StringComparison.OrdinalIgnoreCase))
                    rawExt[n] = kv.Value;
                else if (string.Equals(field, "Name", StringComparison.OrdinalIgnoreCase))
                    rawName[n] = kv.Value;
                else if (string.Equals(field, "Command", StringComparison.OrdinalIgnoreCase))
                    rawCmd[n] = kv.Value;
            }

            // ---- 按 N 升序构造规则 ----
            var allOrders = new HashSet<int>(rawExt.Keys);
            allOrders.UnionWith(rawName.Keys);
            allOrders.UnionWith(rawCmd.Keys);   // ← 修复：只有 Command 的 Action 也纳入遍历

            foreach (int n in allOrders.OrderBy(x => x))
            {
                string ext, name, cmd;
                rawExt.TryGetValue(n, out ext);
                rawName.TryGetValue(n, out name);
                rawCmd.TryGetValue(n, out cmd);

                if (string.IsNullOrWhiteSpace(cmd))
                {
                    cfg._warnings.Add(string.Format("Action.{0} 缺少 Command，已忽略。", n));
                    continue;
                }

                ActionRule rule;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    if (!string.IsNullOrWhiteSpace(ext))
                        cfg._warnings.Add(string.Format(
                            "Action.{0} 同时包含 Ext 和 Name，将只使用 Name。", n));

                    var patterns = ParseNamePatterns(name);
                    if (patterns.Length == 0)
                    {
                        cfg._warnings.Add(string.Format("Action.{0} 的 Name 为空，已忽略。", n));
                        continue;
                    }

                    rule = new ActionRule
                    {
                        Order = n,
                        MatchType = RuleMatchType.Name,
                        Patterns = patterns,
                        Command = cmd,
                        RawPattern = name,
                    };
                }
                else if (!string.IsNullOrWhiteSpace(ext))
                {
                    var patterns = ParseExtPatterns(ext);
                    if (patterns.Length == 0)
                    {
                        cfg._warnings.Add(string.Format("Action.{0} 的 Ext 为空，已忽略。", n));
                        continue;
                    }

                    rule = new ActionRule
                    {
                        Order = n,
                        MatchType = RuleMatchType.Ext,
                        Patterns = patterns,
                        Command = cmd,
                        RawPattern = ext,
                    };
                }
                else
                {
                    cfg._warnings.Add(string.Format("Action.{0} 缺少 Ext 或 Name，已忽略。", n));
                    continue;
                }

                cfg.Rules.Add(rule);
            }

            // ---- 旧配置自动兼容 ----
            string oldAction = Get(dict, "ActionCommand", "");
            if (cfg.Rules.Count == 0 && !string.IsNullOrEmpty(oldAction))
            {
                string oldFilter = Get(dict, "FileFilter", "*.*");
                cfg.Rules.Add(new ActionRule
                {
                    Order = 1,
                    MatchType = RuleMatchType.Ext,
                    RawPattern = oldFilter,
                    Patterns = ParseLegacyFilter(oldFilter),
                    Command = oldAction,
                });
                cfg._legacyWarning =
                    "检测到旧版 FileFilter/ActionCommand，已自动转换为 Action.1（Ext）规则；请尽快改用 Action.N.Ext / Action.N.Name 格式。";
            }

            // ---- 预筛集合 ----
            cfg._extPatterns = cfg.Rules
                .Where(r => r.MatchType == RuleMatchType.Ext)
                .SelectMany(r => r.Patterns)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            cfg._namePatterns = cfg.Rules
                .Where(r => r.MatchType == RuleMatchType.Name)
                .SelectMany(r => r.Patterns)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            // ---- 重复模式校验 ----
            var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in cfg.Rules)
            {
                foreach (var p in r.Patterns)
                {
                    string key = r.MatchType + ":" + p;
                    int firstOrder;
                    if (seen.TryGetValue(key, out firstOrder))
                    {
                        cfg._warnings.Add(string.Format(
                            "{0} '{1}' 同时出现在 Action.{2} 和 Action.{3}，将使用 Action.{2}。",
                            r.MatchType == RuleMatchType.Ext ? "扩展名" : "文件名模式",
                            p, firstOrder, r.Order));
                    }
                    else
                    {
                        seen[key] = r.Order;
                    }
                }
            }

            // ---- '*' 必须最后一条 ----
            for (int i = 0; i < cfg.Rules.Count - 1; i++)
            {
                if (cfg.Rules[i].Patterns.Contains("*"))
                {
                    cfg._warnings.Add(string.Format(
                        "Action.{0} 使用了通配符 '*'，但它不是最后一条规则，后续规则永远不会命中。",
                        cfg.Rules[i].Order));
                }
            }

            return cfg;
        }

        public ActionRule MatchRule(string filePath)
        {
            string fileName = Path.GetFileName(filePath);
            string ext = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();

            foreach (var rule in Rules)
            {
                string target = rule.MatchType == RuleMatchType.Name ? fileName : ext;
                foreach (var pattern in rule.Patterns)
                {
                    if (pattern == "*") return rule;
                    if (WildcardMatch(target, pattern)) return rule;
                }
            }
            return null;
        }

        public bool ShouldTrackFile(string fileName)
        {
            if (_extPatterns.Length == 0 && _namePatterns.Length == 0) return false;
            if (string.IsNullOrEmpty(fileName)) return false;

            foreach (var pattern in _namePatterns)
            {
                if (pattern == "*") return true;
                if (WildcardMatch(fileName, pattern)) return true;
            }

            string ext = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
            foreach (var pattern in _extPatterns)
            {
                if (pattern == "*") return true;
                if (WildcardMatch(ext, pattern)) return true;
            }

            return false;
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

        private static string[] ParseNamePatterns(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return new string[0];
            var result = new List<string>();

            foreach (var part in raw.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string s = part.Trim();
                if (s.Length > 0) result.Add(s);
            }

            return result.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private static string[] ParseLegacyFilter(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return new[] { "*" };

            var result = new List<string>();
            foreach (var part in raw.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string s = part.Trim();
                if (s.Length == 0) continue;
                if (s == "*.*" || s == "*") { result.Add("*"); continue; }
                if (s.StartsWith("*.")) s = s.Substring(2);
                else if (s.StartsWith("*")) s = s.Substring(1);
                s = s.TrimStart('.').ToLowerInvariant();
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
@"# ===== 文件监控工具配置 v1.3 =====
# 修改保存后，右键托盘图标 -> ""重新加载配置"" 即可生效

# 要监控的文件夹（必须存在）
WatchPath=C:\DropFolder

# 是否监控子目录
IncludeSubdirectories=false

# 是否监听""已有文件内容被修改""（默认只关注新放入/改名进来的文件）
WatchChanged=false

# 去抖窗口（毫秒）：文件连续变化时，静默超过该时长才触发动作
DebounceMs=300

# 同时执行的最大动作数
MaxConcurrentActions=4

# 未匹配任何规则的文件是否记日志
LogUnmatched=true

# 日志文件（相对路径基于程序目录）
LogFile=monitor.log

# ===== 规则表 =====
# 格式（每条规则二选一）：
#   Action.<序号>.Ext=<扩展名模式>   + Action.<序号>.Command=<命令>
#   Action.<序号>.Name=<文件名模式>  + Action.<序号>.Command=<命令>
#
# 注意：
#   1. 同一个 Action.<序号>.<字段> 只能出现一次，重复写只有最后一行生效。
#   2. Ext/Name 和 Command 必须在同一个序号下成对出现，
#      写成 Action.1.Ext + Action.2.Command 会导致两条规则都失效。

# Ext  ：按扩展名匹配，分号分隔；支持 pdf / *.pdf / d* / ?df
# Name ：按完整文件名匹配，分号分隔；支持 * 和 ?，整串锚定、大小写不敏感
#        例：*月报.xlsx 匹配 ""2026年3月报.xlsx""，不匹配 ""月报.xls""
#
# Command：命令行模板，支持占位符：
#   {filepath} 完整路径   {filename} 文件名
#   {directory} 所在目录  {ext} 扩展名
#
# 匹配规则：按序号从小到大，第一个命中的规则生效
# 通配符 '*' 兜底，必须放在最后一条

# --- 具体规则优先 ---
Action.1.Name=*月报.pdf
Action.1.Command=""D:\Tools\SpecialPrinter.exe"" ""{filepath}""

Action.2.Name=*月报.xls;*月报.xlsx;*月报.csv
Action.2.Command=""D:\Tools\MergeTool.exe"" --input ""{filepath}""

# --- 通用规则 ---
Action.10.Ext=pdf
Action.10.Command=""D:\Tools\SumatraPDF.exe"" -print-to-default ""{filepath}""

Action.11.Ext=jpg;jpeg;png
Action.11.Command=move /Y ""{filepath}"" ""D:\Archive\Images\""

# --- 兜底（可选） ---
#Action.99.Ext=*
#Action.99.Command=echo 未分类文件: ""{filepath}"" >> ""%TEMP%\unmatched.txt""
";
    }
}