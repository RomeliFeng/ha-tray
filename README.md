# HA Tray

A small Windows tray app for Home Assistant. Click the icon in the notification area and your Home Assistant dashboard pops up above the taskbar, like the Windows 11 quick settings, and hides again when you click elsewhere.

[中文说明](README.zh-CN.md)

- The panel shows the real Home Assistant frontend, so every card works, including custom cards from HACS. Edit what the panel shows in Home Assistant's own dashboard editor.
- Build a dashboard for the panel with one view per room, and the view tabs become your room switcher.
- Click the star at the right of the panel's top bar to make the current room the default. The panel goes back to it every time it closes, so it opens on that room.
- The panel has a fixed size, set in settings. The top bar keeps only the room tabs; the Home Assistant menu buttons are hidden.
- Accent color follows Windows.
- The page stays loaded in the background, so the panel opens instantly.
- Links to other sites open in your default browser.

## Requirements

- Windows 10 19041 or later (Windows 11 recommended)
- Microsoft Edge WebView2 Runtime (included with Windows 11)
- .NET 10 Desktop Runtime

## Setup

On first run the settings window opens. Enter the Home Assistant address (for example `http://homeassistant.local:8123`) and press Save. Then open the panel and sign in to Home Assistant once. The sign-in is remembered in `%LOCALAPPDATA%\HaTray\WebView2`.

Right-click the tray icon to:

- edit the panel: opens the current page in your browser, already in dashboard edit mode,
- set the page you're looking at as the default,
- reload the page or open it in your browser,
- change settings (address, default page, panel size), start with Windows, or exit.

Windows 11 may put a new tray icon in the `^` overflow. To keep it visible, drag it onto the taskbar, or turn it on under Settings → Personalization → Taskbar → Other system tray icons.

## Build

Requires the .NET 10 SDK.

```
dotnet build src\HaTray -c Release -p:Platform=x64
```

Copy the output folder from `bin\x64\Release\…\win-x64\` and run `HaTray.exe`.

For development, `--show` opens the panel at startup and keeps it open when it loses focus. `--debug` watches `%LOCALAPPDATA%\HaTray\debug-eval.js`: whenever the file changes, its JavaScript runs in the panel page and the result is written to `debug-result.json` along with the window state.

## License

MIT
