# WinDndToggle

托盘小程序 + 全局热键，一键开关 Windows 的「勿扰」。不模拟 Win+N，不模拟鼠标点击。

两套产物，随便挑：

| 文件 | 大小 | 运行环境 |
|---|---|---|
| `dist\WinDndToggle.exe` | 31 KB | 只要 Windows 自带的 .NET Framework 4.8，**拷过去双击就能跑** |
| `dist\net8\WinDndToggle.exe` | 183 KB | 需要装 .NET 8 Desktop Runtime (x64) |

推荐第一个。Win10 1903 之后和 Win11 都自带 .NET Framework 4.8，等于零依赖。

## 运行环境到底依赖什么

分两层，都跟「装什么软件」无关：

程序运行时：`.NET Framework 4.8`（net48 版）或 `.NET 8 Desktop Runtime`（net8 版）。net48 版反编译看引用，只有 `mscorlib` / `System` / `System.Core` / `System.Drawing` / `System.Windows.Forms`，全是 4.8 自带的，没有任何第三方库、没有 NuGet 依赖。

系统能力：`QuietHoursSettings` 这个 COM 服务（`wpnapps.dll`，Win10 就有）+ 通知服务在跑（正常桌面会话都在跑）。这两样是 Windows 的一部分，不需要额外安装。

不需要：管理员权限、MSIX 打包、.NET 8、VC++ 运行库、任何安装程序。

## 怎么保证别人能直接跑

1. 直接把 `dist\WinDndToggle.exe`（31 KB）发过去，双击。就这一步。
2. **全程只有一个 exe**：不生成配置文件、不写注册表、不装服务、不留缓存。仅有的例外都是你在托盘菜单里主动操作的产物，取消操作就还原：勾选「开机自启」写一个 HKCU Run 键；在菜单里换热键写一个 `HKCU\Software\WinDndToggle` 值。跑起来和退出后目录里都只有那一个 exe（实测验证过）。
3. 我做的验证：把这个 exe 单独拷到一个空目录，里面什么都没有，`--probe` 正常运行；反射检查引用的程序集全部是 .NET Framework 自带的。
4. 位数：AnyCPU，32 位和 64 位 Windows 都能跑，ARM64 的 Windows 走 x64 兼容层也能跑。
5. 开机自启：托盘右键菜单勾选「开机自启」（写的是 HKCU 的 Run 注册表键，不用管理员权限），或者手动把 exe 的快捷方式丢进 `shell:startup`。
6. 唯一的例外：Win10 1809 及更早的机器可能没装 4.8（1809 自带 4.7.2）。这种情况让他装一次 .NET Framework 4.8 离线包，或者改用 `dist\net8\` 那个版本 + .NET 8 Desktop Runtime。
7. 不放心对方的机器环境？那就发 self-contained 版，约 60 MB，连 .NET 都不用装：

```bat
dotnet publish src\WinDndToggle\WinDndToggle.csproj -c Release -f net8.0-windows -r win-x64 ^
  -p:SelfContained=true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o dist\standalone
```

## 结论先说

你这台 23H2 是可以做到的，而且比 `FocusSessionManager` 那条路更合适。

用的是 Windows 的 **QuietHoursSettings COM 服务**——就是任务栏那个铃铛背后用的东西：

```
CLSID {F53321FA-34F8-4B7F-B9A3-361877CB94CF}   CLSID_QuietHoursSettings
IID   {6BFF4732-81EC-4FFB-AE67-B6C1BC29631F}   IQuietHoursSettings
槽 3  get_UserSelectedProfile   -> LPWSTR（CoTaskMemAlloc，要 CoTaskMemFree）
槽 4  put_UserSelectedProfile   <- LPCWSTR
取值  Microsoft.QuietHoursProfile.Unrestricted / PriorityOnly / AlarmsOnly
```

它是本地服务（out-of-proc），普通用户权限就能用，不需要管理员、不需要打包成 MSIX、不需要模拟输入。

## 两条路的对比

| | FocusSessionManager | QuietHoursSettings |
|---|---|---|
| 是否公开 | 官方文档有 | 未公开（社区逆向，YASB 等在用） |
| 最低版本 | 23504（实际 24H2 / 26100） | Win10 就存在，23H2 实测可用 |
| 23H2 能不能用 | 不能，功能开关没打开 | 能 |
| 副作用 | 开的是「专注会话」，带计时、会调时钟 App；关掉后勿扰可能还留着 | 就是直接拨铃铛，没有额外状态 |
| 代码依赖 | WinRT 投影（exe 27MB，还要 .NET 8） | 手工 COM 互操作（exe 22KB，.NET Framework 4.8 自带） |

`FocusSessionManager` 在 23H2 上的真实情况（我在这台机器上验证过）：类型元数据在、`GetDefault()` 能拿到对象、`IsFocusActive` 能读、`IsSupported` 甚至返回 True，但 `TryStartFocusSession()` 抛 `UnauthorizedAccessException 0x80070005`，消息是 `Feature com.microsoft.windows.focussessionmanager.1 is not available`。属于「代码在、功能开关没开」，提权或 MSIX 打包都救不了。

顺带一提：`IsSupported` 会撒谎，别拿它做能力判断。

## 怎么确认这条路真的生效了

不是「调用返回 S_OK」就算数。我用 shell 自己的实时状态交叉验证：

`WNF_SHEL_QUIETHOURS_ACTIVE_PROFILE_CHANGED`（`0x0D83063EA3BF1C75`）通过 `NtQueryWnfStateData` 读出来是 4 字节，值是「勿扰是否生效」，还有 change stamp：

```
toggle 开 -> payload 01 00 00 00, stamp 0xE2F0
toggle 关 -> payload 00 00 00 00, stamp 0xE2F1
```

每次写入，payload 翻转、stamp +1，说明 shell 那边真的处理了，不是只写进某个缓存。

没做到的：这台机器的会话截屏返回全白（远程/锁屏环境），所以我没法贴一张「通知中心铃铛亮起」的截图。你自己按一下 Win+N 就能看到。

## 用法

双击 exe，没有主窗口，也不会在磁盘上生成任何文件。

- 托盘图标 **左键** = 切换；**右键** = 状态 / 开关 / 重新检测 / 开机自启 / 更换热键 / 退出
- 全局热键 **Ctrl+Alt+D**
- 界面语言跟随系统：中文系统显示中文，其他语言显示英文
- 图标：灰色月亮 = 关，紫色月亮 + 红点 = 开
- 图标每 3 秒同步一次，你用 Win+N 或设置页改了状态，托盘也会跟上

命令行模式（不开界面，适合挂到别的工具、快捷方式、AHK 里）：

```bat
WinDndToggle.exe --toggle   切一次
WinDndToggle.exe --on       开（仅优先事项）
WinDndToggle.exe --off      关
WinDndToggle.exe --probe    打印当前状态，可读返回 0，不可读返回 1
```

换热键：默认 `Ctrl+Alt+D`。三种办法，按优先级从高到低：

1. 托盘右键菜单点「更换热键...」，直接按下新组合键（支持 Ctrl/Alt/Shift/Win + 字母/数字/F1-F24）。对话框打开期间当前热键会临时释放（不会误触发），保存到 `HKCU\Software\WinDndToggle`；如果组合键被别的程序占用会明确提示，并保持原热键不变
2. 自己往 exe 同目录放一个 `WinDndToggle.config.json`，内容 `{ "hotkey": "Ctrl+Shift+F9" }`。程序**只读不写**，放了这个文件就会盖过菜单里保存的热键
3. 或者启动时带参数：`WinDndToggle.exe --hotkey "Ctrl+Alt+Q"`（改快捷方式的命令行即可），优先级最高

也就是说：命令行参数 > config.json > 菜单里保存的 > 默认值。exe 挪了位置也不怕，开机自启的注册表路径会在下次启动时自动修正。

开机自启：托盘图标右键菜单里勾选「开机自启」即可。写的是 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`，普通用户权限、任务管理器的「启动应用」里能看到也能关掉。也可以手动把 exe 的快捷方式丢进 `shell:startup`，两种方式互不影响。

## 有没有现成的开源项目

查了一圈（GitHub 仓库搜索 + 逐个看源码），没有能直接拿来用的：

| 项目 | 星 | 做法 | 能不能用 |
|---|---|---|---|
| `amnweb/yasb` | 5.7k | Python 写的整套 Windows 状态栏，它的 DND 组件用的就是本文这个 COM 接口 | 能切换，但要装 Python + 配一整套状态栏，为一个小功能太重。**本项目的技术路线就是从它源码里挖出来的** |
| `bitdisaster/windows-focus-assist` | 12 | C++ 写的 node 原生模块，读 Win10 Focus Assist 状态 | 只读，不能切 |
| `ajkessel/ps-detect-dnd` | 2 | PowerShell 脚本，读 Win11 DND 状态 | 只读，不能切 |
| `DanBennettUK/Toggle-DND-Raycast-Windows` | 0 | Raycast 扩展，内部 exec 一个 AutoHotkey 编译的 exe 去点通知中心 | 就是模拟点击，正是你不想要的那种 |

其余的搜索结果基本都是 macOS / Linux 的同名功能（`streamdeck-dnd`、`gnome-dnd-switch` 之类），跟 Windows 无关。

所以结论是：要么用 YASB 这种重家伙，要么自己写。这个项目 31 KB、一个 exe、一个热键，反而是最省事的形态。

## 风险

- 这个 COM 接口是未公开的，微软随时可能在新的 Windows 里改掉或删掉。真改了就回到「不支持」，程序会明确告诉你，不会静默失效。
- 依赖 shell 的通知服务在跑（正常桌面会话都在跑）。
- `AlarmsOnly`（仅闹钟）也支持，但托盘切换只用 `Unrestricted ↔ PriorityOnly`，和系统铃铛的行为一致。

## 编译

```bat
build.cmd
```

一次出两套（`net48` 免安装版 + `net8` 单文件版）。项目是双目标 `<TargetFrameworks>net8.0-windows;net48</TargetFrameworks>`，共用一份源码。

net48 那边没有 `System.Text.Json`、没有 `string[..n]`、没有 `StringSplitOptions.TrimEntries`、没有 `Application.SetHighDpiMode`，都改成了兼容写法（配置用几行字符串查找解析，高 DPI 靠 `app.manifest` 声明）。

单独编某一套：

```bat
dotnet build src\WinDndToggle\WinDndToggle.csproj -c Release -f net48
dotnet publish src\WinDndToggle\WinDndToggle.csproj -c Release -f net8.0-windows -r win-x64 ^
  -p:SelfContained=false -p:PublishSingleFile=true -o dist\net8
```

## 代码结构

- `src/WinDndToggle/QuietHours.cs` —— COM 互操作：读 / 写 / 切换。vtable 槽位、字符串所有权、踩过的坑都写在文件头注释里
- `src/WinDndToggle/TrayContext.cs` —— 托盘、菜单、热键、3 秒轮询、提示框；开机自启（Autostart）和界面文案（Res，中英双语）也在这
- `src/WinDndToggle/HotkeyDialog.cs` —— 换热键对话框：捕获组合键、实时显示，冲突检测交给注册那一步
- `src/WinDndToggle/Program.cs` —— 入口、单实例、命令行模式
- `src/WinDndToggle/app.manifest` —— 高 DPI（PerMonitorV2）声明，net48 靠它
- `tools/QhProbe/` —— 单独的探针，能直接读 profile 和 WNF 状态，排查用
- `tools/verify_gui.py` —— GUI 冒烟测试：启动、枚举窗口、验证热键占用
- `tools/make_icon.py` —— 生成 app.ico（纯 Python，不依赖 PIL）

## 踩过的坑（留给以后的自己）

- 这个对象 QI 得到 `IInspectable`，但方法在槽 3/4，不是 6/7。按 WinRT 接口调会崩。
- 返回值是裸 `LPWSTR`（CoTaskMemAlloc），不是 BSTR 也不是 HSTRING。按 BSTR 释放会 `0xC0000374` 堆损坏。
- `put_` 的参数是 `LPCWSTR`，直接传 UTF-16 缓冲区，不要封成 BSTR。
- 用 Python 抓窗口截图前必须 `SetProcessDpiAwareness(2)`，否则坐标被 DPI 虚拟化，会误判成「文字被切掉」。
- `build.cmd` 里不要写中文：cmd 按控制台代码页解析批处理，UTF-8 中文会把后续命令行拆坏（第一次就栽在这，`net48` 被当成命令执行）。
- `RegisterHotKey` 在**同一个 (窗口句柄， id)** 上注册**不同**组合会「追加」而不是替换或失败——实测两个组合会同时生效、都能触发。所以注册前必须先 `UnregisterHotKey` 清一次同 id 条目，并且每次都要检查返回值，否则会静默双绑。
- 换热键的对话框打开期间必须先把当前热键 `UnregisterHotKey` 放掉，关掉再恢复。不然用户在对话框里按下的组合如果正好是当前热键，会被全局热键截走直接触发切换，按键永远凑不齐，表现为「一按就触发、改不回去」。

