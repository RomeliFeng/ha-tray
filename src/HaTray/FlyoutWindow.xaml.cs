using System.Runtime.InteropServices;
using System.Text.Json;
using HaTray.Ha;
using HaTray.Model;
using HaTray.Ui;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;
using static HaTray.Native.Win32;

namespace HaTray;

/// <summary>
/// 点托盘图标弹出的面板：贴着任务栏，失焦就收起，尺寸固定（设置里的宽高），内容超出时在面板里滚动。
/// 这里管窗口、顶栏和页面切换；页面内容由 <see cref="PanelView"/> 画。布局、操作逻辑、样子见 docs/design.md 第 3、6 节。
/// </summary>
internal sealed partial class FlyoutWindow : Window, IPanelEdits
{
    private const double EdgeGap = 12;

    private static readonly JsonSerializerOptions DebugJson = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly Settings _settings;
    private readonly Home _home;
    private readonly Presenter _presenter;
    private readonly Controller _controller;
    private readonly PanelView _view;
    private readonly nint _hwnd;
    private IHomeBackend? _backend;
    private List<Room> _rooms = [];
    private string? _roomId;
    private Page _page;
    private string? _detailId;
    private long _hiddenAt;
    private RECT? _anchor;
    private int _menusOpen;
    private bool _editing;
    private bool _renderPending;
    private Entity? _sheetEntity;
    private string? _lastViewKey;
    private Kit _kit = null!;
    private FileSystemWatcher? _debugWatcher;

    public FlyoutWindow(Settings settings, Home home)
    {
        InitializeComponent();
        _settings = settings;
        _home = home;
        _presenter = new Presenter(home);
        _controller = new Controller(DispatcherQueue, () => _backend);
        _view = new PanelView(home, _presenter, _controller, OpenDetail, this);
        _hwnd = WindowNative.GetWindowHandle(this);

        SystemBackdrop = new DesktopAcrylicBackdrop();
        var presenter = OverlappedPresenter.Create();
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsResizable = false;
        presenter.IsAlwaysOnTop = true;
        presenter.SetBorderAndTitleBar(true, false);
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "HaTray.ico"));
        AppWindow.Closing += (_, e) =>
        {
            e.Cancel = true;
            HideFlyout();
        };
        var round = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(_hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, sizeof(int));

        // 菜单开着时不收：菜单限制在窗口里弹，一般不抢焦点，这里再保险一次。
        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated && !Pinned && _menusOpen == 0)
            {
                HideFlyout();
            }
        };

        // Esc：先关磁贴设置，再退出编辑，二级页回主界面，主界面收起面板。
        Root.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Escape)
            {
                if (SheetHost.Visibility == Visibility.Visible)
                {
                    CloseSheet();
                }
                else if (_editing)
                {
                    SetEditing(false);
                }
                else if (_page != Page.Main)
                {
                    GoMain();
                }
                else
                {
                    HideFlyout();
                }

                e.Handled = true;
            }
        };

        // 深浅色、强调色变了：整个重画。
        Root.ActualThemeChanged += (_, _) => Render();
        Palette.AccentChanged += () => DispatcherQueue.TryEnqueue(Render);

        _home.StructureChanged += RebuildRooms;
        _home.StatesChanged += ids =>
        {
            _controller.OnStatesChanged(ids);
            _view.Refresh(ids);
        };
        _controller.Changed += id => _view.Refresh([id]);
        _view.DragEnded += () =>
        {
            if (_renderPending && !_view.IsDragging)
            {
                _view.LogDrag("补上推迟的重画");
                Render();
            }
        };
        Render();
    }

    private enum Page
    {
        Main,
        More,
        Detail,
    }

    public event Action? SettingsRequested;

    /// <summary>失焦也不收起（调试通道的 show 命令用）。</summary>
    public bool Pinned { get; set; }

    /// <summary>换后端（启动、设置改了、演示模式）。旧的由调用方释放。</summary>
    public void SetBackend(IHomeBackend? backend)
    {
        if (_backend is not null)
        {
            _backend.StatusChanged -= Render;
        }

        _backend = backend;
        if (backend is not null)
        {
            backend.StatusChanged += Render;
        }

        Render();
    }

    /// <summary>启动时先激活一次再藏起来，第一次弹出就是现成的窗口。</summary>
    public void Prime()
    {
        Reposition();
        AppWindow.Move(new PointInt32(-32000, -32000));
        Activate();
        AppWindow.Hide();
    }

    public void Toggle(RECT? anchor)
    {
        if (AppWindow.IsVisible)
        {
            HideFlyout();
            return;
        }

        // 面板开着时再点图标：焦点先跑到任务栏，面板已经因失焦收起，这一下不该又把它弹出来。
        if (Environment.TickCount64 - _hiddenAt < 300)
        {
            return;
        }

        ShowFlyout(anchor);
    }

    public void ShowFlyout(RECT? anchor)
    {
        _anchor = anchor;
        Reposition();
        AppWindow.Show(true);
        Activate();
        SetForegroundWindow(_hwnd);
        Root.Focus(FocusState.Programmatic);
    }

    /// <summary>收起后就回到默认房间的主界面，下次弹出时它已经画好。</summary>
    public void HideFlyout()
    {
        if (AppWindow.IsVisible)
        {
            AppWindow.Hide();
            _hiddenAt = Environment.TickCount64;
        }

        if (!Pinned)
        {
            CloseSheet();
            if (_editing)
            {
                _editing = false;
                Render();
            }

            GoToDefaultRoom();
        }
    }

    /// <summary>设置改了：尺寸重新算，标签名可能变了要重排房间。</summary>
    public void ApplySettings()
    {
        RebuildRooms();
        if (AppWindow.IsVisible)
        {
            Reposition();
        }
    }

    // ---------------- 房间和页面 ----------------

    private Room? CurrentRoom => _rooms.FirstOrDefault(r => r.Id == _roomId);

    private void RebuildRooms()
    {
        _rooms = RoomBuilder.Build(_home, _settings.LabelName);
        if (CurrentRoom is null)
        {
            _roomId = DefaultRoomId();
            _page = Page.Main;
        }

        if (_page == Page.Detail && (_detailId is null || !_home.Entities.ContainsKey(_detailId)))
        {
            _page = Page.Main;
        }

        Render();
    }

    private string? DefaultRoomId() =>
        _settings.DefaultRoom is { } d && _rooms.Any(r => r.Id == d) ? d : _rooms.FirstOrDefault()?.Id;

    private void GoToDefaultRoom()
    {
        var target = DefaultRoomId();
        if (target != _roomId || _page != Page.Main)
        {
            _roomId = target;
            _page = Page.Main;
            Render();
        }
    }

    private void SelectRoom(string id)
    {
        CloseSheet();
        _roomId = id;
        _page = Page.Main;
        Render();
    }

    private void GoMain()
    {
        _page = Page.Main;
        Render();
    }

    private void OpenDetail(Entity e)
    {
        _page = Page.Detail;
        _detailId = e.EntityId;
        Render();
    }

    private void OpenMore()
    {
        _page = Page.More;
        Render();
    }

    // ---------------- 编辑主界面（写回 HA，见 docs/design.md 第 5 节）----------------

    public bool CanEdit => _backend?.IsAdmin == true;

    private void SetEditing(bool on)
    {
        _editing = on;
        CloseSheet();
        Render();
    }

    public void Pin(Entity e, bool on) => Write(b => b.SetLabelAsync(e.EntityId, _settings.LabelName, on), L.T("改标签失败：", "Couldn't change the label: "));

    /// <summary>按顺序写 10、20、30……；只写变了的。先改本地再重画，不等 HA 推回来，拖完马上就是新顺序。</summary>
    public void SaveOrder(IReadOnlyList<Entity> ordered)
    {
        for (var i = 0; i < ordered.Count; i++)
        {
            var e = ordered[i];
            var order = (i + 1) * 10;
            if (e.InRegistry && e.Tray.Order != order)
            {
                e.Tray = e.Tray with { Order = order };
                var options = e.Tray;
                Write(b => b.SetTrayOptionsAsync(e.EntityId, options), L.T("保存顺序失败：", "Couldn't save the order: "));
            }
        }

        _home.NotifyStructureChanged();
    }

    public void SetSize(Entity e, string? size)
    {
        if (!e.InRegistry)
        {
            return;
        }

        e.Tray = e.Tray with { Size = size };
        var options = e.Tray;
        Write(b => b.SetTrayOptionsAsync(e.EntityId, options), L.T("保存大小失败：", "Couldn't save the size: "));
        _home.NotifyStructureChanged();
        OpenSettings(e);
    }

    /// <summary>固定胶囊：同样先改本地再重画。</summary>
    public void SetPinned(Entity e, bool pinned)
    {
        if (!e.InRegistry)
        {
            return;
        }

        e.Tray = e.Tray with { Pinned = pinned };
        var options = e.Tray;
        Write(b => b.SetTrayOptionsAsync(e.EntityId, options), L.T("保存固定失败：", "Couldn't save the pin: "));
        _home.NotifyStructureChanged();
    }

    public void UpdateEntity(Entity e, string field, string? value) =>
        Write(b => b.UpdateEntityAsync(e.EntityId, field, value), L.T("修改失败：", "Couldn't save: "));

    /// <summary>写 HA 区域设置；同样先改本地再重画。磁贴设置开着时按新值重开一次。</summary>
    public void SetAreaSensor(Room room, string field, string? entityId)
    {
        Write(b => b.SetAreaSensorAsync(room.Id, field, entityId), L.T("修改区域失败：", "Couldn't change the area: "));
        _home.PatchArea(room.Id, a => field == "temperature_entity_id" ? a with { TemperatureEntityId = entityId } : a with { HumidityEntityId = entityId });
        _home.NotifyStructureChanged();
        if (SheetHost.Visibility == Visibility.Visible && _sheetEntity is { } open)
        {
            OpenSettings(open);
        }
    }

    public void OpenSettings(Entity e)
    {
        _sheetEntity = e;
        SheetHost.Child = _view.SettingsSheet(e, CloseSheet);
        SheetHost.Visibility = Visibility.Visible;
    }

    private void CloseSheet()
    {
        _sheetEntity = null;
        SheetHost.Child = null;
        SheetHost.Visibility = Visibility.Collapsed;
    }

    /// <summary>写回 HA；失败时在顶栏下面的提示条里说。</summary>
    private void Write(Func<IHomeBackend, Task> write, string failure)
    {
        if (_backend is not { } b)
        {
            return;
        }

        _ = Run();
        async Task Run()
        {
            try
            {
                await write(b);
            }
            catch (Exception ex) when (ex is HaCommandException or IOException or TimeoutException or InvalidOperationException)
            {
                StatusText.Text = failure + ex.Message;
                StatusText.Visibility = Visibility.Visible;
            }
        }
    }

    private void SetDefaultRoom()
    {
        if (_roomId is not null)
        {
            _settings.DefaultRoom = _roomId;
            _settings.Save();
            Render();
        }
    }

    // ---------------- 画界面 ----------------

    private void Render()
    {
        // 拖动中不重画：重画会把按着的那块换掉，拖动就断了（HA 推回上一次拖动写的注册表更新时最容易撞上）。松手后补画。
        if (_view.IsDragging)
        {
            if (!_renderPending)
            {
                _view.LogDrag("拖动中要重画，推迟到松手后");
            }

            _renderPending = true;
            return;
        }

        _renderPending = false;
        _kit = new Kit(Palette.For(Root.ActualTheme), open =>
        {
            _menusOpen = Math.Max(0, _menusOpen + (open ? 1 : -1));
        });
        RenderHeader();
        if (_backend is null || !_backend.Ready)
        {
            ShowPlaceholderForState();
            return;
        }

        Placeholder.Visibility = Visibility.Collapsed;
        Scroller.Visibility = Visibility.Visible;
        var connected = _backend.Status == ConnectionStatus.Connected;
        StatusText.Foreground = _kit.P.Warning;
        StatusText.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
        StatusText.Text = _backend.Status == ConnectionStatus.AuthFailed
            ? L.T("令牌失效了，请在设置里重新填。", "The token stopped working. Update it in Settings.")
            : L.T("和 Home Assistant 的连接断了，正在重连…", "Lost connection to Home Assistant. Reconnecting…");

        // 断线时控件都不能点，数据保留最后一次的值。
        Body.IsHitTestVisible = connected;
        Body.Opacity = connected ? 1 : 0.6;

        var room = CurrentRoom;
        _view.LabelId = _home.Labels.GetValueOrDefault(_settings.LabelName);
        _view.Editing = _editing && _page == Page.Main && CanEdit;
        _view.Begin(_kit, room);

        // 同一页重画（状态、注册表变了，编辑时的每一步）保持滚动位置；换了页才回到顶部。
        var viewKey = $"{_roomId}|{_page}|{_detailId}|{_view.Editing}";
        var offset = viewKey == _lastViewKey ? Scroller.VerticalOffset : 0;
        _lastViewKey = viewKey;
        Body.Children.Clear();

        // 面板内容的留白在 Body 里面（滚动区本身不留边，内容就裁在滚动区的边上）：
        // 左右 12，编辑时顶部 10，这样伸出内容 7 像素的 − 和图钉都落在可见范围里。
        Body.Padding = new Thickness(12, _view.Editing ? 10 : 2, 12, 14);
        if (room is not null)
        {
            switch (_page)
            {
                case Page.More:
                    _view.BuildMore(Body, room);
                    break;
                case Page.Detail when _detailId is not null && _home.Entities.TryGetValue(_detailId, out var e):
                    _view.BuildDetail(Body, e, room);
                    break;
                default:
                    _view.BuildMain(Body, room);
                    if (room.Count > 0 && !_view.Editing)
                    {
                        Body.Children.Add(_view.MoreLink(room, OpenMore));
                    }

                    break;
            }
        }

        Body.UpdateLayout();
        Scroller.ChangeView(null, offset, null, disableAnimation: true);
    }

    /// <summary>顶栏：主界面是「房间名 ⇕」（点了弹出房间菜单）和星标；二级页是返回和页面标题。</summary>
    private void RenderHeader()
    {
        Header.Children.Clear();
        Header.ColumnDefinitions.Clear();
        Header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var room = CurrentRoom;
        if (_backend?.Ready != true || room is null)
        {
            var title = _kit.Text("HA Tray", 15, null, strong: true);
            title.Margin = new Thickness(6, 0, 0, 0);
            Header.Children.Add(title);
            return;
        }

        if (_page != Page.Main)
        {
            var back = _kit.GhostButton(Mdi.Icon("chevron-left", 20), GoMain, L.T("返回", "Back"));
            back.Padding = new Thickness(2, 0, 2, 0);
            Header.Children.Add(back);
            var text = _page == Page.More
                ? room.Name + " · " + L.T("更多设备", "All devices")
                : _detailId is not null && _home.Entities.TryGetValue(_detailId, out var e) ? Presenter.FullName(e) : "";
            var title = _kit.Text(text, 15, null, strong: true);
            title.Margin = new Thickness(2, 0, 0, 0);
            Grid.SetColumn(title, 1);
            Header.Children.Add(title);
            return;
        }

        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        content.Children.Add(_kit.Text(room.Name, 15, null, strong: true));
        content.Children.Add(Mdi.Icon("unfold-more-horizontal", 15, _kit.P.Text2));
        var roomButton = _kit.GhostButton(content, () => { });
        roomButton.Flyout = RoomMenu();

        // 房间按钮放在第二列靠左，第一列空着（和二级页的返回按钮同一位置）。
        Grid.SetColumn(roomButton, 1);
        roomButton.HorizontalAlignment = HorizontalAlignment.Left;
        Header.Children.Add(roomButton);

        // 右边：平时是铅笔（编辑主界面，管理员才有）和星标；编辑时换成强调色的「完成」。
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        if (_editing && CanEdit)
        {
            var done = _kit.PlainButton(L.T("完成", "Done"), null, () => SetEditing(false), 26);
            done.Background = _kit.P.Accent;
            done.Foreground = _kit.P.OnAccent;
            done.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            right.Children.Add(done);
        }
        else
        {
            if (CanEdit)
            {
                right.Children.Add(_kit.GhostButton(Mdi.Icon("pencil-outline", 17, _kit.P.Text2), () => SetEditing(true), L.T("编辑主界面", "Edit panel")));
            }

            var isDefault = _settings.DefaultRoom == room.Id;
            right.Children.Add(_kit.GhostButton(Mdi.Icon(isDefault ? "star" : "star-outline", 18, isDefault ? _kit.P.Star : _kit.P.Text3), SetDefaultRoom,
                isDefault ? L.T("默认房间", "Default room") : L.T("设为默认房间", "Set as default room")));
        }

        Grid.SetColumn(right, 2);
        Header.Children.Add(right);
    }

    /// <summary>房间菜单：HA 的区域顺序；有楼层时按楼层分段；最后单独一段是「未分配区域」。当前房间打勾，默认房间带星。</summary>
    private MenuFlyout RoomMenu()
    {
        var menu = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft, ShouldConstrainToRootBounds = true, MenuFlyoutPresenterStyle = _kit.P.MenuStyle };
        menu.Opened += (_, _) => _menusOpen++;
        menu.Closed += (_, _) => _menusOpen = Math.Max(0, _menusOpen - 1);
        var areaRooms = _rooms.Where(r => !r.IsUnassigned).ToList();
        var floors = _home.Floors.Values.ToList();
        var sections = floors.Count > 0
            ? floors.Select(f => (Title: (string?)f.Name, Rooms: areaRooms.Where(r => r.FloorId == f.Id).ToList()))
                .Append((Title: L.T("其他区域", "Other areas"), Rooms: areaRooms.Where(r => r.FloorId is null || !_home.Floors.ContainsKey(r.FloorId)).ToList()))
                .Where(g => g.Rooms.Count > 0)
                .ToList()
            : [(Title: null, Rooms: areaRooms)];

        foreach (var (title, rooms) in sections)
        {
            if (title is not null)
            {
                if (menu.Items.Count > 0)
                {
                    menu.Items.Add(new MenuFlyoutSeparator());
                }

                menu.Items.Add(new MenuFlyoutItem { Text = title, IsEnabled = false });
            }

            foreach (var r in rooms)
            {
                menu.Items.Add(RoomItem(r));
            }
        }

        if (_rooms.FirstOrDefault(r => r.IsUnassigned) is { } unassigned)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(RoomItem(unassigned));
        }

        return menu;
    }

    private ToggleMenuFlyoutItem RoomItem(Room r)
    {
        var item = new ToggleMenuFlyoutItem { Text = r.Name, IsChecked = r.Id == _roomId };
        if (_settings.DefaultRoom == r.Id)
        {
            item.KeyboardAcceleratorTextOverride = "★";
        }

        item.Click += (_, _) => SelectRoom(r.Id);
        return item;
    }

    private void ShowPlaceholderForState()
    {
        Scroller.Visibility = Visibility.Collapsed;
        StatusText.Visibility = Visibility.Collapsed;
        Placeholder.Visibility = Visibility.Visible;
        var (text, loading, button) = _backend?.Status switch
        {
            null => (L.T("还没设置 Home Assistant 的地址和令牌。", "Home Assistant isn't set up yet."), false, true),
            ConnectionStatus.AuthFailed => (L.T("令牌无效或已被撤销，请在设置里重新填。", "The token is invalid or has been revoked. Update it in Settings."), false, true),
            ConnectionStatus.Disconnected => (L.T("连不上 Home Assistant，正在重试…", "Can't reach Home Assistant. Retrying…") + (_backend.Error is { } err ? "\n" + err : ""), true, true),
            _ => (L.T("正在连接 Home Assistant…", "Connecting to Home Assistant…"), true, false),
        };
        PlaceholderText.Text = text;
        PlaceholderText.Foreground = _kit.P.Text2;
        PlaceholderText.FontSize = 12;
        Loading.IsActive = loading;
        Loading.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        PlaceholderButton.Visibility = button ? Visibility.Visible : Visibility.Collapsed;
        PlaceholderButton.Style = (Style)Application.Current.Resources["MacButtonStyle"];
        PlaceholderButton.Background = _kit.P.Control;
        PlaceholderButton.Height = 28;
        PlaceholderButton.Content = L.T("打开设置", "Open settings");
    }

    private void PlaceholderButton_Click(object sender, RoutedEventArgs e)
    {
        HideFlyout();
        SettingsRequested?.Invoke();
    }

    private void Reposition()
    {
        POINT pt;
        if (_anchor is { } r)
        {
            pt = new POINT { X = (r.Left + r.Right) / 2, Y = (r.Top + r.Bottom) / 2 };
        }
        else
        {
            GetCursorPos(out pt);
        }

        var monitor = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(monitor, ref info);
        GetDpiForMonitor(monitor, 0, out var dpi, out _);
        var scale = dpi / 96.0;

        var work = info.rcWork;
        var gap = (int)Math.Round(EdgeGap * scale);
        // 尺寸固定，就是设置里的宽高；屏幕放不下时缩到工作区里。
        var width = Math.Min((int)Math.Round(_settings.Width * scale), work.Right - work.Left - (2 * gap));
        var height = Math.Min((int)Math.Round(_settings.Height * scale), work.Bottom - work.Top - (2 * gap));
        // 按整个窗口定尺寸。标题栏藏起来以后，ResizeClient 仍然把原来标题栏那一截算在客户区外面，实际会高出约 29。
        AppWindow.Resize(new SizeInt32(width, height));

        // 默认贴右下角；任务栏在左边或上边时贴那一边。
        var size = AppWindow.Size;
        var x = work.Left > info.rcMonitor.Left ? work.Left + gap : work.Right - size.Width - gap;
        var y = work.Top > info.rcMonitor.Top ? work.Top + gap : work.Bottom - size.Height - gap;
        AppWindow.Move(new PointInt32(x, y));
    }

    // ---------------- 调试通道 ----------------

    /// <summary>
    /// --debug：往 %LOCALAPPDATA%\HaTray\debug-cmd.txt 写一行命令，结果写进 debug-result.json。不截图，一律看文字。
    /// 命令：dump（加 all 时连「更多设备」一起展开）、show（弹出并保持打开）、hide、room &lt;区域 id&gt;（空着是「未分配区域」）、
    /// more（进「更多设备」）、detail &lt;实体 id&gt;、back。
    /// </summary>
    public void EnableDebug()
    {
        Directory.CreateDirectory(Settings.DataDir);
        var input = Path.Combine(Settings.DataDir, "debug-cmd.txt");
        var output = Path.Combine(Settings.DataDir, "debug-result.json");
        var watcher = new FileSystemWatcher(Settings.DataDir, "debug-cmd.txt") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName, EnableRaisingEvents = true };
        void Run() => DispatcherQueue.TryEnqueue(async () =>
        {
            string cmd;
            try
            {
                await Task.Delay(100);
                cmd = (await File.ReadAllTextAsync(input)).Trim();
            }
            catch (IOException)
            {
                return;
            }

            var verb = cmd.Split(' ', 2)[0];
            var arg = cmd.Length > verb.Length ? cmd[(verb.Length + 1)..].Trim() : "";
            switch (verb)
            {
                case "show":
                    Pinned = true;
                    ShowFlyout(null);
                    break;
                case "hide":
                    Pinned = false;
                    HideFlyout();
                    break;
                case "room":
                    SelectRoom(arg);
                    break;
                case "more":
                    OpenMore();
                    break;
                case "detail" when _home.Entities.TryGetValue(arg, out var e):
                    OpenDetail(e);
                    break;
                case "back":
                    GoMain();
                    break;
                case "edit":
                    SetEditing(true);
                    break;
                case "done":
                    SetEditing(false);
                    break;
                case "settings" when _home.Entities.TryGetValue(arg, out var s):
                    OpenSettings(s);
                    break;
                // 写一次命令文件常常触发两次变化事件，所以命令都要重复执行也不出错：pin / unpin 分开，不做切换。
                case "pin" or "unpin" when _home.Entities.TryGetValue(arg, out var p):
                    SetPinned(p, verb == "pin");
                    break;
                case "badges":
                    cmd += " → " + BadgeReport();
                    break;
                case "chips":
                    cmd += " → " + ChipReport();
                    break;
                case "texts":
                    cmd += " → " + TextReport();
                    break;
                case "order":
                    SaveOrder([.. arg.Split(',', StringSplitOptions.TrimEntries).Where(_home.Entities.ContainsKey).Select(id => _home.Entities[id])]);
                    break;
            }

            try
            {
                await File.WriteAllTextAsync(output, JsonSerializer.Serialize(DebugDump(cmd), DebugJson));
            }
            catch (IOException)
            {
            }
        });
        watcher.Changed += (_, _) => Run();
        watcher.Created += (_, _) => Run();
        _debugWatcher = watcher;
    }

    /// <summary>
    /// 调试用：编辑模式下每个角上按钮（−、图钉）在面板里的实际位置，查三件事：
    /// 按钮之间有没有重叠；按钮有没有压到别的块（不是它自己那块）；滚动区里的按钮有没有被滚动区的边裁掉。
    /// </summary>
    private string BadgeReport()
    {
        Root.UpdateLayout();
        static IEnumerable<DependencyObject> Walk(DependencyObject d)
        {
            yield return d;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
            {
                foreach (var x in Walk(VisualTreeHelper.GetChild(d, i)))
                {
                    yield return x;
                }
            }
        }

        Windows.Foundation.Rect R(FrameworkElement f) => f.TransformToVisual(Root).TransformBounds(new Windows.Foundation.Rect(0, 0, f.ActualWidth, f.ActualHeight));
        static bool Hit(Windows.Foundation.Rect a, Windows.Foundation.Rect b) =>
            Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left) > 0.5 && Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top) > 0.5;

        var badges = Walk(Body).OfType<Button>().Where(b => (string?)b.Tag == PanelView.BadgeTag).ToList();
        var frames = badges.Select(b => (FrameworkElement)VisualTreeHelper.GetParent(b)).Distinct().ToList();

        // 可见范围是滚动区模板里的 ScrollContentPresenter（内容就裁在它的边上），不是滚动区外框：
        // 滚动区的内边距在外框和它之间，按外框算会把被裁的按钮当成没被裁（2026-10-18 就这样漏报过）。
        var presenter = Walk(Scroller).OfType<ScrollContentPresenter>().FirstOrDefault();
        var viewport = presenter is not null ? R(presenter) : R(Scroller);
        var problems = new List<string>();
        for (var i = 0; i < badges.Count; i++)
        {
            var a = R(badges[i]);
            for (var j = i + 1; j < badges.Count; j++)
            {
                if (Hit(a, R(badges[j])))
                {
                    problems.Add($"按钮重叠 #{i} 和 #{j} @({a.X:0},{a.Y:0})");
                }
            }

            var own = VisualTreeHelper.GetParent(badges[i]);
            foreach (var f in frames.Where(f => f != own))
            {
                // 别的块只算它的内容（第一个子元素），不算它伸出去的按钮。
                if (VisualTreeHelper.GetChildrenCount(f) > 0 && VisualTreeHelper.GetChild(f, 0) is FrameworkElement content && Hit(a, R(content)))
                {
                    problems.Add($"按钮 #{i} 压到别的块 @({a.X:0},{a.Y:0})");
                }
            }

            if (a.Bottom > viewport.Top && a.Top < viewport.Bottom && (a.Top < viewport.Top - 0.5 || a.Left < viewport.Left - 0.5 || a.Right > viewport.Right + 0.5))
            {
                problems.Add($"按钮 #{i} 被滚动区裁掉 @({a.X:0},{a.Y:0})");
            }
        }

        return $"按钮 {badges.Count} 个，块 {frames.Count} 个；" + (problems.Count == 0 ? "没有重叠、没有被裁" : string.Join("；", problems.Take(12)));
    }

    /// <summary>
    /// 调试用：当前页面（顶栏和内容）上所有文字，去重。带英文单词（两个以上字母）的列在前面，用来查中英混杂。
    /// 下拉菜单的选项不在页面上，另外列出每个弹出按钮的全部选项。
    /// </summary>
    private string TextReport()
    {
        Root.UpdateLayout();
        static IEnumerable<DependencyObject> Walk(DependencyObject d)
        {
            yield return d;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
            {
                foreach (var x in Walk(VisualTreeHelper.GetChild(d, i)))
                {
                    yield return x;
                }
            }
        }

        var all = Walk(Header).Concat(Walk(Body)).Concat(Walk(SheetHost)).ToList();
        var texts = all.OfType<TextBlock>().Select(t => t.Text)
            .Concat(all.OfType<Button>().Select(b => b.Flyout).OfType<MenuFlyout>()
                .SelectMany(m => m.Items.OfType<MenuFlyoutItem>().Select(i => "菜单项：" + i.Text)))
            .Concat(all.Select(ToolTipService.GetToolTip).OfType<string>().Select(t => "提示：" + t))
            .Where(t => !string.IsNullOrWhiteSpace(t) && t.Length < 60 && !t.Any(c => c >= '' && c <= '' || char.IsSurrogate(c)))
            .Distinct()
            .ToList();
        var english = texts.Where(t => System.Text.RegularExpressions.Regex.IsMatch(t, "[A-Za-z]{2,}")).ToList();
        return $"共 {texts.Count} 条，带英文 {english.Count} 条：" + string.Join(" | ", english) + " ‖ 其余：" + string.Join(" | ", texts.Except(english).Take(60));
    }

    /// <summary>调试用：主界面上每个胶囊格子（含「还有 N 个」）的实际宽高，和里面的字有没有装下。</summary>
    private string ChipReport()
    {
        Root.UpdateLayout();
        static IEnumerable<DependencyObject> Walk(DependencyObject d)
        {
            yield return d;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
            {
                foreach (var x in Walk(VisualTreeHelper.GetChild(d, i)))
                {
                    yield return x;
                }
            }
        }

        var cells = Walk(Body).OfType<FrameworkElement>().Where(f => (string?)f.Tag == PanelView.ChipTag).ToList();
        var parts = cells.Select(c =>
        {
            var kind = c is Button ? "更多" : "胶囊";
            var inner = c is Border { Child: FrameworkElement child } ? child : null;
            var fits = inner is null || inner.DesiredSize.Height <= c.ActualHeight + 0.5;
            return $"{kind} {c.ActualWidth:0}×{c.ActualHeight:0}{(fits ? "" : $"（字高 {inner!.DesiredSize.Height:0}，装不下）")}";
        });
        return cells.Count == 0 ? "没有胶囊" : string.Join("，", parts);
    }

    private object DebugDump(string cmd)
    {
        object Item(Entity e) => new
        {
            id = e.EntityId,
            name = Presenter.FullName(e),
            shortName = Presenter.ShortName(e),
            state = e.State,
            text = _presenter.StateText(e),
            icon = _presenter.Icon(e),
            glyph = Mdi.Glyph(_presenter.Icon(e)) is not null,
            active = Presenter.IsActive(e),
            primary = PrimaryAction.For(e, _home)?.Service,
            tray = e.Tray.ToJson()?.ToJsonString(),
        };

        var all = cmd.StartsWith("dump all", StringComparison.Ordinal);
        return new
        {
            cmd,
            status = _backend?.Status.ToString(),
            ready = _backend?.Ready,
            admin = _backend?.IsAdmin,
            error = _backend?.Error,
            window = new { visible = AppWindow.IsVisible, x = AppWindow.Position.X, y = AppWindow.Position.Y, w = AppWindow.Size.Width, h = AppWindow.Size.Height, theme = Root.ActualTheme.ToString() },
            page = new { room = _roomId, page = _page.ToString(), detail = _detailId, editing = _view.Editing, sheet = SheetHost.Visibility == Visibility.Visible, rows = Body.Children.Count, drawn = _view.Debug },
            dragLog = _view.DragLog,
            counts = new
            {
                entities = _home.Entities.Count, areas = _home.Areas.Count, floors = _home.Floors.Count, devices = _home.Devices.Count,
                labels = _home.Labels.Count, translations = _home.Translations.Count, iconRules = _home.Icons.Count, mdiGlyphs = Mdi.Count,
                label = _settings.LabelName, labelFound = _home.Labels.ContainsKey(_settings.LabelName),
            },
            rooms = _rooms.Select(r => new
            {
                id = r.Id,
                name = r.Name,
                floor = r.FloorId,
                total = r.Count,
                chips = r.Chips.Select(Item),
                groups = r.Groups.Select(g => new { group = g.Group.ToString(), items = g.Items.Select(Item) }),
                devices = all
                    ? r.Devices.Select(d => (object)new { name = d.Name, normal = d.Normal.Select(Item), config = d.Config.Select(Item), diagnostic = d.Diagnostic.Select(Item) })
                    : r.Devices.Select(d => (object)new { name = d.Name, normal = d.Normal.Count, config = d.Config.Count, diagnostic = d.Diagnostic.Count }),
            }),
        };
    }
}
