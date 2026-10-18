# HA Tray

Home Assistant 的 Windows 托盘小工具。点通知区域的图标，任务栏上方弹出一个面板，显示一个房间里的设备，样子接近 macOS 的控制中心；点别处就收起。

[English](README.md)

## 能做什么

- **房间就是 Home Assistant 的区域**，顺序和你在 HA 里排的一样。顶栏下拉切换房间，点星标设为默认房间。
- **主界面显示什么由你定**：在 HA 里给实体打上标签（默认叫「托盘」），或者直接在面板的编辑模式里添加。
- **控件按 Home Assistant 自己的规则来**：能调光的灯有亮度条；空调有它报的模式、风速、摆风；风扇有风量档位。支持灯、空调、风扇、加湿 / 除湿、窗帘、阀门、门锁、播放器、扫地机、割草机、热水器、警报、下拉选择、数值、按钮、场景、脚本等，其他类型显示状态。
- **传感器显示成房间顶部的胶囊**。固定的胶囊在收起时也显示，其余的收进「还有 N 个」。
- **更多设备**：按设备分组列出房间里的全部实体，什么都不藏。
- **编辑模式**（铅笔图标）：
  - 拖动磁贴、分组、胶囊来排序；
  - 添加、拿掉实体；
  - 改磁贴的大小、名字、图标。

  改动存在 Home Assistant 里，每台电脑上的面板都一样。
- 名字、图标、状态文字都来自 Home Assistant，包括它的翻译。界面语言跟随 Home Assistant（中文或英文）。
- 通过 Home Assistant 的 WebSocket 接口实时更新。面板跟随 Windows 的深浅色和强调色。

## 要求

- Windows 10 2004（19041）及以上，推荐 Windows 11
- [.NET 10 桌面运行时](https://dotnet.microsoft.com/download/dotnet/10.0)
- Home Assistant 的长期访问令牌。要在面板里编辑主界面（标签、顺序、大小、名字），令牌对应的用户需要是管理员。

## 使用

1. 运行 `HaTray.exe`，第一次会弹出设置窗口。
2. 填 Home Assistant 地址，比如 `http://homeassistant.local:8123`。
3. 在 Home Assistant 的个人资料 →「安全」→「长期访问令牌」里创建一个令牌，粘贴进来。令牌只存在这台电脑上，按当前 Windows 用户加密（`%LOCALAPPDATA%\HaTray\settings.json`）。
4. 保存，连上以后托盘图标就能用了。

要往主界面上放东西，可以在 HA 里给实体打标签；也可以打开面板，点铅笔，在「可以添加」里点实体旁边的 **+**。

右键托盘图标可以：打开面板、在浏览器中打开 Home Assistant、打开设置、设置开机启动、退出。

Win11 可能把新出现的托盘图标收进「^」里。想让它一直显示，可以把图标拖到任务栏上，或者在「设置 → 个性化 → 任务栏 → 其他系统托盘图标」里把它打开。

## 在 Home Assistant 里存了什么

HA Tray 用 Home Assistant 的公开接口保存你的布局：

| 内容 | 存在哪 |
|---|---|
| 哪些实体在主界面上 | 设置里指定的那个标签 |
| 顺序、磁贴大小、固定的胶囊 | 实体注册表里 `ha_tray` 命名空间的选项 |
| 在面板里改的名字和图标 | 实体自己的名字和图标，和在 HA 里改一样 |
| 房间的温度、湿度传感器 | HA 的区域设置 |

## 构建

需要 .NET 10 SDK：

```
dotnet build src\HaTray -c Release -p:Platform=x64
```

输出在 `src\HaTray\bin\x64\Release\…\win-x64\`，整个文件夹拷走就能运行 `HaTray.exe`。

开发时可以加参数：

- `--demo`：用内置的演示数据，不连 Home Assistant。
- `--debug`：打开文字调试通道。往 `%LOCALAPPDATA%\HaTray\debug-cmd.txt` 写一行命令，从 `debug-result.json` 读结果。

设计说明见 [docs/design.md](docs/design.md)。

## 许可

MIT。图标来自 [Material Design Icons](https://pictogrammers.com/library/mdi/)（Apache 2.0，见 `src/HaTray/Assets/mdi-LICENSE.txt`）。

HA Tray 与 Home Assistant、Open Home Foundation 没有隶属关系。
