# HA Tray

A Windows tray app for Home Assistant. Click the icon in the notification area and a panel pops up above the taskbar with the devices of one room, styled after the macOS Control Center. Click anywhere else and it hides.

[中文说明](README.zh-CN.md)

## What it does

- **Rooms are your Home Assistant areas**, in the order you set in Home Assistant. Pick a room from the drop-down at the top. Star a room to open it by default.
- **You choose what the main view shows** by putting a label (default: `Tray`) on entities in Home Assistant, or from the panel itself in edit mode.
- **Controls follow Home Assistant's own rules.** A light gets a brightness slider if it can dim, an air conditioner gets the modes, fan speeds and swing modes it reports, a fan gets speed steps, and so on. Lights, climate, fans, humidifiers, covers, valves, locks, media players, vacuums, lawn mowers, water heaters, alarm panels, selects, numbers, buttons, scenes, scripts and more are supported. Anything else shows its state.
- **Sensors show as chips** at the top of the room. Pin the ones you want to see when the chips are collapsed.
- **All devices** lists every entity in the room, grouped by device, so nothing is hidden from you.
- **Edit mode** (pencil icon): drag tiles, groups and chips to reorder them; remove or add entities; set a tile's size, name and icon. Changes are saved in Home Assistant, so every PC running HA Tray shows the same panel.
- Names, icons and state texts come from Home Assistant, including its translations. The interface language follows Home Assistant (Chinese or English).
- Updates are live over the Home Assistant WebSocket API. The panel follows the Windows light/dark theme and accent color.

## Requirements

- Windows 10 version 2004 (build 19041) or later; Windows 11 recommended
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)
- Home Assistant with a long-lived access token. Editing the panel from the app (labels, order, sizes, names) needs an administrator account.

## Setup

1. Run `HaTray.exe`. The settings window opens on first run.
2. Enter your Home Assistant address, for example `http://homeassistant.local:8123`.
3. Create a long-lived access token in Home Assistant under your profile → Security → Long-lived access tokens, and paste it. The token is stored on this PC only, encrypted for your Windows account (`%LOCALAPPDATA%\HaTray\settings.json`).
4. Save. HA Tray connects and the tray icon is ready.

To fill the main view, either add the label to entities in Home Assistant, or open the panel, click the pencil and press **+** next to the entities you want.

Right-click the tray icon to open the panel, open Home Assistant in your browser, change settings, start with Windows, or exit.

Windows 11 may put a new tray icon in the `^` overflow. To keep it visible, drag it onto the taskbar, or turn it on under Settings → Personalization → Taskbar → Other system tray icons.

## What is stored in Home Assistant

HA Tray keeps your layout in Home Assistant, using its public API:

| What | Where |
|---|---|
| Which entities are on the main view | the label set in HA Tray's settings |
| Order, tile size, pinned chips | entity registry options under `ha_tray` |
| Names and icons you change in the panel | the entity's own name and icon, same as editing them in Home Assistant |
| A room's temperature and humidity sensor | the area's settings in Home Assistant |

## Build

Requires the .NET 10 SDK.

```
dotnet build src\HaTray -c Release -p:Platform=x64
```

The output is in `src\HaTray\bin\x64\Release\…\win-x64\`. Copy the folder anywhere and run `HaTray.exe`.

For development:

- `--demo` runs on built-in sample data without Home Assistant.
- `--debug` turns on a text debug channel. Write a command to `%LOCALAPPDATA%\HaTray\debug-cmd.txt` and read the result from `debug-result.json`.

Design notes are in [docs/design.md](docs/design.md) (Chinese).

## License

MIT. Icons are from [Material Design Icons](https://pictogrammers.com/library/mdi/) (Apache 2.0, see `src/HaTray/Assets/mdi-LICENSE.txt`).

HA Tray is not affiliated with Home Assistant or the Open Home Foundation.
