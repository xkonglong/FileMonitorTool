# FileMonitorTool

一个常驻 Windows 系统托盘的文件监控小工具：监视指定文件夹，等新文件“落盘稳定”后，自动执行你配置的任意命令（解压、归档、转码、调用外部程序……）。

基于 `FileSystemWatcher` + 去抖（Debounce）实现，整个程序没有主窗口，双击托盘图标即打开配置。

## 特性

- **只对新文件动手**：默认只关注新放入 / 改名进来的文件，不理会内容修改（可通过 `WatchChanged` 打开）
- **去抖合并事件**：复制大文件时 Windows 会连发几十个事件，静默 `DebounceMs` 毫秒后才触发一次
- **文件就绪检测**：触发前用独占读句柄试打开，仍被占用则自动延迟重试，避免处理半截文件
- **通配符过滤**：`*.zip;*.rar;*.7z`，支持 `*` 与 `?`，大小写不敏感
- **动作模板**：`ActionCommand` 支持 `{filepath}` `{filename}` `{directory}` `{ext}` 占位符
- **执行有兜底**：`.exe` / `.com` 直接启动（避开 cmd 的引号剥离），其余交给 `cmd /c`；单次执行 10 分钟超时会被结束，标准输出、错误输出与退出码都写进日志
- **配置热重载**：改完 `config.ini` 保存，右键托盘 → “重新加载配置”即可生效，无需重启
- **可随时暂停**：托盘菜单有“停止监控 / 开始监控”开关，暂停期间不再处理新文件，程序仍常驻；重载配置会保持当前的运行或停止状态
- **图标随状态切换**：运行中是程序图标，停止监控后托盘换成“已停止”图标，托盘提示文字同步显示“运行中 / 已停止”，一眼就能看出当前状态
- **开机自启**：托盘菜单勾选即可，写入 `HKCU\...\CurrentVersion\Run`，不碰管理员权限
- **日志轮转**：单个日志超过 1 MB 自动转存为 `.1`
- **单实例**：命名互斥体防重复启动

## 使用

1. 运行 `FileMonitorTool.exe`，托盘出现图标即开始监控。
2. 修改配置文件 `config.ini`，指定监控文件夹路径、文件类型等。
3. 右键托盘图标：
   - 打开配置文件 / 重新加载配置 / 打开日志 / 停止监控（或开始监控）/ 开机启动 / 退出
   - 点“停止监控”后托盘图标会换成停止样式，再点“开始监控”恢复原图标

### 配置项

| 配置项 | 说明 | 默认 |
| --- | --- | --- |
| `WatchPath` | 要监控的文件夹，必须存在 | 空（必填） |
| `FileFilter` | 扩展名过滤，分号或逗号分隔，支持 `*` `?`；`*.*` 表示全部 | `*.*` |
| `IncludeSubdirectories` | 是否递归监控子目录 | `false` |
| `WatchChanged` | 是否响应“已有文件内容被修改” | `false` |
| `ActionCommand` | 文件稳定后执行的命令；`.exe`/`.com` 直接启动，其余（内置命令、`%VAR%`、重定向、`.bat`）走 `cmd /c` | 空（只记日志） |
| `DebounceMs` | 去抖窗口（毫秒） | `300` |
| `LogFile` | 日志文件，相对路径基于程序目录 | `monitor.log` |

### 动作示例

```ini
# 记录命中文件
ActionCommand=echo 检测到文件: "{filepath}" >> "%TEMP%\filemonitor_hits.txt"

# 自动复制到归档目录
ActionCommand=xcopy /Y "{filepath}" "D:\Archive\"

# 调用外部程序
ActionCommand="C:\Tools\MyTool.exe" --input "{filepath}"
```

占位符：`{filepath}` 完整路径、`{filename}` 文件名、`{directory}` 所在目录、`{ext}` 扩展名（不带点）。

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
