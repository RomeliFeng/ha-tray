# ha-tray

Home Assistant 的 Windows 托盘弹出面板（WinUI 3 + .NET 10，不打包）。HA 只当数据源（WebSocket 接口），界面全部由程序用原生控件画，样子接近 macOS 控制中心。准备在 GitHub 开源。项目说明见 `README.md`，设计见 `docs/design.md`，进度见 `docs/plan.md`，决策见 `docs/decisions.md`。

## 规则

- **开源仓库：不放任何某一家的数据。** 内网地址、令牌、邮箱、房间名、区域和实体 id、设备名、仪表盘路径都不进仓库，文档里的例子也用编的（演示数据 `Ha/DemoBackend.cs` 同样）。
  - **推送到 GitHub 前先跑 `pwsh tools\check-privacy.ps1`**，查当前文件、全部提交、提交说明和提交者邮箱。查到就先处理，不推。
  - 关键词清单放在本机 `%LOCALAPPDATA%\HaTray\privacy-patterns.txt`，不进仓库（清单本身就是个人资料）。用户家里加了新房间、新设备时，顺手补进清单。
- **分界线不越过**：显示什么由 HA 决定（区域、标签、名字、图标、翻译），程序只管怎么画。不按集成或型号写特例；控件规则照抄 HA 前端源码，并在注释里写明出处。
- **程序不替用户做选择**（比如替空调挑温度传感器）：给出选项让用户选，结果存在 HA 里（区域设置、实体注册表的 `options.ha_tray`）。
- **界面文字不是草稿板**：只写名字、读数、状态、动作和要用户知道的错误，不写解释程序做法的说明，提示框也一样（见 `docs/design.md` 第 10 节）。界面文案用 `L.T("中文", "English")`，写在调用处。
- 小项目，文档只做引导：`README.md`（英文）、`README.zh-CN.md`、本文件、`docs/design.md`、`docs/plan.md`、`docs/decisions.md`。代码里读得出来的不写。`plan.md` 做完的条目直接删；`decisions.md` 只增不改，被推翻的标「已取代」（例外：为了去掉个人资料可以改字）。
- 推到 GitHub、发 Release 之前先问用户。

## 代码在哪

| 目录 / 文件 | 管什么 |
|---|---|
| `Ha/HaConnection.cs` | WebSocket：认证、命令按 id 配对、订阅事件、断线重连 |
| `Ha/HaSession.cs` | 连上后取全部数据（语言、注册表、翻译、图标规则），订阅状态和注册表变化；写回 HA（标签、`options.ha_tray`、名字图标、区域传感器） |
| `Ha/DemoBackend.cs` | `--demo` 的假数据，接口和 `HaSession` 一样 |
| `Model/` | `Home` 存 HA 原样给的数据；`Presenter` 照 HA 前端算名字、状态文字、图标；`Caps` 功能位和控件判断；`Rooms` 排房间、胶囊、分组 |
| `Ui/Kit.cs`、`Ui/Palette.cs` | 控件套件和颜色（按钮、胶囊滑条、开关、分段、弹出菜单） |
| `Ui/PanelView.cs` | 主界面、更多设备、详情页 |
| `Ui/PanelView.Edit.cs` | 编辑模式：拖动排序、− / + / 图钉、磁贴设置 |
| `Ui/Controller.cs` | 发控制命令、等待中、出错、± 防抖、图标按钮的主动作 |
| `FlyoutWindow.xaml(.cs)` | 窗口、顶栏、页面切换、写回 HA 的入口、调试通道 |
| `App.xaml(.cs)` | 托盘、单实例、后端切换；`App.xaml` 里有两套按钮模板 |
| `tools/` | `make-icons.ps1` 生成图标，`fetch-mdi.ps1` 下载图标字体，`check-privacy.ps1` 推送前查个人资料，`make-screenshots.ps1` 用演示数据截 README 的中英两套截图（docs/images） |

## 开发

- 构建：`dotnet build src\HaTray -c Debug -p:Platform=x64`。改代码前先关掉正在跑的 HaTray，否则 exe 被占用，构建失败。
- `HaTray.exe --demo`：用内置假数据跑，不连 HA。`--lang=zh|en` 只在这次运行里固定界面语言。界面改了以后重跑 `tools\make-screenshots.ps1` 更新 README 截图。
- `HaTray.exe --debug`：文字调试通道。往 `%LOCALAPPDATA%\HaTray\debug-cmd.txt` 写一行命令，读 `debug-result.json`。写一次命令文件常常触发两次，命令都要能重复执行，不做切换。
  - 页面：`dump`（加 `all` 时连「更多设备」一起展开）、`show`（弹出并保持打开）、`hide`、`room <区域 id>`（不带 id 是「未分配区域」）、`more`、`detail <实体 id>`、`back`。结果里 `page.drawn` 列出这一页画了哪些胶囊、磁贴、设备卡。
  - 编辑模式：`edit`、`done`、`settings <实体 id>`、`pin` / `unpin <实体 id>`、`order <实体 id,…>`。结果里 `dragLog` 是拖动日志。
  - 检查：`badges`（−、图钉有没有重叠、被裁）、`chips`（胶囊格子的实际尺寸）、`texts`（页面上全部文字，查中英混杂和注释性质的文字）。**改了布局或文字，每个房间都跑一遍再交给用户看。**
  - **不截图**（太费 token），一律用文字结果排查；界面效果请用户看。
- 令牌由用户自己在设置窗口里填，程序用 DPAPI 加密存在 `%LOCALAPPDATA%\HaTray\settings.json`。不要去读、解密或打印它。
- 图标由 `tools\make-icons.ps1` 生成，产物提交进仓库。
- 环境：Windows 11 IoT Enterprise LTSC（没有应用商店），屏幕 3840x2160 @200%，Windows 显示语言是英文；界面语言「自动」跟随 HA，所以连上 HA 后是中文。
