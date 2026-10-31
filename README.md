# FileMonitorTool

一个常驻 Windows 系统托盘的文件监控小工具：监视指定文件夹，等新文件“落盘稳定”后，自动执行你配置的任意命令（解压、归档、转码、调用外部程序……）。

基于 `FileSystemWatcher` + 去抖（Debounce）实现，整个程序没有主窗口，双击托盘图标即打开配置。

## 特性

- **只对新文件动手**：默认只关注新放入 / 改名进来的文件，不理会内容修改（可通过 `WatchChanged` 打开）
- **去抖合并事件**：复制大文件时 Windows 会连发几十个事件，静默 `DebounceMs` 毫秒后才触发一次
- **文件就绪检测**：触发前用独占读句柄试打开，仍被占用则自动延迟重试，避免处理半截文件
- **多规则分流**：一张 `Action.<序号>` 规则表，按扩展名（`Ext`）或完整文件名（`Name`）把不同文件分派给不同命令；支持 `*` `?` 通配符，大小写不敏感
- **按序号优先**：序号从小到大匹配，第一条命中的规则生效，所以细则放前面、`*` 兜底放最后
- **并发有上限**：`MaxConcurrentActions` 限制同时执行的动作数，一次落盘几百个文件也不会瞬间炸出一堆进程
- **配置有校验**：重复的键、缺 `Command`、`Ext` 和 `Name` 同时写、`*` 不在最后、两条规则抢同一个模式，都会在日志里点名；一条有效规则都没有时托盘直接弹提醒
- **动作模板**：`Command` 支持 `{filepath}` `{filename}` `{directory}` `{ext}` 占位符
- **执行有兜底**：`.exe` / `.com` 直接启动（避开 cmd 的引号剥离），其余交给 `cmd /c`；单次执行 10 分钟超时会被结束，标准输出、错误输出与退出码都写进日志
- **配置热重载**：改完 `config.ini` 保存，右键托盘 → “重新加载配置”即可生效，无需重启
- **可随时暂停**：托盘菜单有“停止监控 / 开始监控”开关，暂停期间不再处理新文件，程序仍常驻；重载配置会保持当前的运行或停止状态
- **图标随状态切换**：运行中是程序图标，停止监控后托盘换成“已停止”图标，托盘提示文字同步显示“运行中 / 已停止”，一眼就能看出当前状态
- **开机自启**：托盘菜单勾选即可，写入 `HKCU\...\CurrentVersion\Run`，不碰管理员权限
- **日志轮转**：单个日志超过 1 MB 自动转存为 `.1`
- **单实例**：命名互斥体防重复启动

## 使用

1. 运行 `FileMonitorTool.exe`，托盘出现图标即开始监控。
2. 修改配置文件 `config.ini`，设置监控文件夹，并按需增删规则（见下面的“规则表”）。
3. 右键托盘图标：
   - 打开配置文件 / 重新加载配置 / 打开日志 / 停止监控（或开始监控）/ 开机启动 / 退出
   - 点“停止监控”后托盘图标会换成停止样式，再点“开始监控”恢复原图标

### 全局配置项

| 配置项 | 说明 | 默认 |
| --- | --- | --- |
| `WatchPath` | 要监控的文件夹，必须存在 | 空（必填） |
| `IncludeSubdirectories` | 是否递归监控子目录 | `false` |
| `WatchChanged` | 是否响应“已有文件内容被修改” | `false` |
| `DebounceMs` | 去抖窗口（毫秒） | `300` |
| `MaxConcurrentActions` | 同时执行的最大动作数（小于 1 按 1 处理） | `4` |
| `LogUnmatched` | 没有命中任何规则的文件是否记一条日志 | `true` |
| `LogFile` | 日志文件，相对路径基于程序目录 | `monitor.log` |

### 规则表

每条规则由「匹配条件 + 命令」组成，写在同一个序号下，两个字段必须成对出现：

```ini
# 按扩展名匹配
Action.10.Ext=pdf
Action.10.Command="D:\Tools\SumatraPDF.exe" -print-to-default "{filepath}"

# 按完整文件名匹配
Action.1.Name=*月报.xlsx;*月报.csv
Action.1.Command="D:\Tools\MergeTool.exe" --input "{filepath}"
```

- `Ext`：按扩展名匹配，分号分隔，可写 `pdf`、`*.pdf`、`d*`、`?df`；`*` 表示全部
- `Name`：按完整文件名匹配，分号分隔，支持 `*` `?`，整串锚定、大小写不敏感；`*月报.xlsx` 能匹配 `2026年3月报.xlsx`，不会匹配 `月报.xls`
- 同一个序号只写 `Ext` 或 `Name` 其中一个，另一个会被忽略并记日志
- 匹配按序号从小到大，第一条命中的规则生效；`*` 兜底务必放到最后一条
- `Command` 里 `.exe` / `.com` 直接启动，其余（内置命令、`%VAR%`、重定向、`.bat`）交给 `cmd /c`
- 占位符：`{filepath}` 完整路径、`{filename}` 文件名、`{directory}` 所在目录、`{ext}` 扩展名（不带点）

**配置会被检查**：重复的键、某个 `Action.N` 缺 `Command`、`Ext` 和 `Name` 同时写、写了两条一模一样的模式、`*` 规则不在最后，都会在日志里出现 `[配置校验]` 提示；一条有效规则都没有时，托盘会直接弹出“没有有效规则”。

**老配置仍然能用**：如果只写了旧版的 `FileFilter` + `ActionCommand`，程序会自动把它转成一条 `Action.1.Ext` 规则，并在日志里提示尽快迁移。

## 编译

- Visual Studio 2022（17.10 及以上，`.slnx` 解决方案格式）
- 或命令行：`dotnet build -c Release`（需要 .NET Framework 4.8 目标框架包），也可以直接双击仓库里的 `编译.bat`

目标框架 `net48`，使用 WinForms；输出在 `bin\Release\net48\`。

## 目录结构

```
FileMonitorTool.csproj   项目文件（net48 / WinExe）
FileMonitorTool.slnx     解决方案
Program.cs               入口，单实例互斥 + 消息循环
TrayApplication.cs       托盘图标、菜单、开机自启、配置重载
FileMonitor.cs           FileSystemWatcher 封装、过滤、就绪检测
Debouncer.cs             按文件路径去抖
ActionExecutor.cs        命令模板展开与执行
ConfigReader.cs          config.ini 解析与默认配置
Logger.cs                线程安全日志与轮转
Properties\Resources.resx  内嵌图标资源（APP 运行图标 / stop 停止图标）
APP.ico / stop.ico       图标源文件
编译.bat                  一行命令的 Release 构建脚本
```

## 说明

- 只支持 Windows（依赖 WinForms、注册表、`FileSystemWatcher`）。
- `ActionCommand` 最终会变成真实进程，配置即代码，请勿使用来路不明的配置。
- 若动作程序需要 UAC 提权（Win32 错误 740），工具本身不会提权，日志里会给出对应提示。
- 监控网络盘或超大目录时，`FileSystemWatcher` 内部缓冲区可能溢出，日志中会出现相应告警；可缩小监控范围或关闭子目录监控。

## License

[MIT](LICENSE) © 小恐龙
