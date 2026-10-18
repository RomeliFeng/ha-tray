# 进度和待办

设计见 `design.md`。

## 现在

原生面板做完了，界面是接近 macOS 的风格，用户看过、确认了效果。已经接用户的真实 HA 核对过：
- 主界面：环境胶囊、各类磁贴、设备卡；
- 「更多设备」：按设备分组，带星标；
- 详情页。

## 待做

- 用户实际操作一遍：开关、滑条、±、弹出菜单、星标放到主界面、Esc 返回。
- `design.md` 4.1 表里有的类型还没用真设备试过：窗帘、门锁、播放器、扫地机、热水器、警报。演示数据里有样例。
- 传感器 24 小时曲线（`design.md` 4.1 sensor 一行，第二期）。
- 已发布 v0.1.0（2026-10-18）：https://github.com/RomeliFeng/ha-tray/releases/tag/v0.1.0，x64 和 ARM64 两个 zip。ARM64 包没在实机上试过。
- 发版步骤：`dotnet publish src\HaTray -c Release -p:Platform=x64 -r win-x64 --self-contained false -p:DebugType=none -p:DebugSymbols=false -o dist\build-win-x64`（ARM64 同理），实测能启动，扫一遍发布文件里的个人资料，打成「HaTray 文件夹 + LICENSE」的 zip。`DebugType=none` 不能省：调试信息会把本机路径写进 exe。每次推送、发布前都先跑 `tools\check-privacy.ps1`，并先问用户。
