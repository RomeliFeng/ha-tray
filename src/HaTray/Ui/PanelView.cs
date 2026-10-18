using System.Text.Json.Nodes;
using HaTray.Model;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace HaTray.Ui;

/// <summary>
/// 面板内容：房间主界面、「更多设备」、详情页。布局和操作逻辑见 docs/design.md 第 3、4 节，样子见第 6 节。
/// 每个磁贴、每一行都包在一个「活」的容器里：它关心的实体状态变了，只重画这一块。
/// </summary>
internal sealed partial class PanelView(Home home, Presenter pr, Controller ctl, Action<Entity> openDetail, IPanelEdits edits)
{
    /// <summary>有自己磁贴样式的类型；同一个设备打标签的实体只有这些、而且类型相同时，各自一张磁贴，否则合成设备卡。</summary>
    private static readonly HashSet<string> WideDomains = ["climate", "fan", "humidifier", "water_heater", "cover", "valve", "media_player", "vacuum", "lawn_mower", "number", "input_number", "select", "input_select"];

    private readonly Dictionary<string, List<(Border Host, Func<UIElement> Build)>> _live = [];
    private readonly HashSet<string> _openDevices = [];
    private bool _chipsOpen;
    private string? _chipsRoom;
    private Kit _k = null!;
    private Room? _room;

    /// <summary>调试通道用：这一页画了哪些东西。</summary>
    public List<object> Debug { get; } = [];

    public string? LabelId { get; set; }

    private Palette P => _k.P;

    public void Begin(Kit kit, Room? room)
    {
        _k = kit;
        _room = room;
        _live.Clear();
        Debug.Clear();
        ResetEditing();
        if (room?.Id != _chipsRoom)
        {
            _chipsOpen = false;
            _chipsRoom = room?.Id;
        }
    }

    /// <summary>这些实体变了（状态、等待中、出错）：重画关心它们的那几块。</summary>
    public void Refresh(IEnumerable<string> ids)
    {
        var done = new HashSet<Border>();
        foreach (var id in ids)
        {
            if (_live.TryGetValue(id, out var list))
            {
                foreach (var (host, build) in list)
                {
                    if (done.Add(host))
                    {
                        host.Child = build();
                    }
                }
            }
        }
    }

    private Border Live(IEnumerable<string> ids, Func<UIElement> build)
    {
        var host = new Border { Child = build() };
        foreach (var id in ids.Distinct())
        {
            if (!_live.TryGetValue(id, out var list))
            {
                _live[id] = list = [];
            }

            list.Add((host, build));
        }

        return host;
    }

    // ======================= 主界面 =======================

    public void BuildMain(StackPanel body, Room room)
    {
        if (room.Chips.Count > 0)
        {
            body.Children.Add(Editing ? EditableChips(room) : Chips(room));
        }

        // 同一个设备打了标签的实体：都是同一种「有自己磁贴」的类型时各自一张（比如一条灯带的两段），否则合成设备卡。
        var labeled = room.Groups.SelectMany(g => g.Items).ToList();
        var cards = labeled.Where(e => e.Device is not null)
            .GroupBy(e => e.Device!)
            .Where(g => g.Count() > 1 && !(g.All(e => e.Domain == g.First().Domain) && (WideDomains.Contains(g.First().Domain) || g.First().Domain is "light" or "switch" or "input_boolean")))
            .ToDictionary(g => g.Key, g => g.ToList());
        var siblings = labeled.Where(e => e.Device is not null).GroupBy(e => e.Device!).ToDictionary(g => g.Key, g => g.Count());

        var sections = new List<Section>();
        foreach (var (group, items) in room.Groups)
        {
            var units = new List<Unit>();
            foreach (var e in items)
            {
                if (e.Device is { } d && cards.TryGetValue(d, out var members))
                {
                    if (members[0] == e)
                    {
                        units.Add(new Unit(DeviceCard(d, members), true, members, null));
                    }

                    continue;
                }

                var title = e.Device is { } dev && siblings.GetValueOrDefault(dev) > 1
                    ? Presenter.FullName(e)
                    : Presenter.DeviceName(e) ?? Presenter.EntityName(e) ?? Presenter.FullName(e);
                var isWide = e.Tray.Size switch
                {
                    "wide" => true,
                    "half" => false,
                    _ => WideDomains.Contains(e.Domain),
                };
                units.Add(new Unit(Live(TileIds(e, room), () => Tile(e, title, room, isWide)), isWide, [e], e));
                Debug.Add(new { kind = "tile", e.EntityId, title, wide = isWide });
            }

            if (units.Count > 0)
            {
                sections.Add(new Section(group, units));
            }
        }

        if (Editing)
        {
            if (sections.Count > 0)
            {
                body.Children.Add(EditableSections(sections));
            }

            Addable(body, room);
            return;
        }

        foreach (var s in sections)
        {
            body.Children.Add(_k.Section(RoomBuilder.GroupTitle(s.Group)));
            body.Children.Add(TwoColumns([.. s.Units.Select(u => u.Element)], [.. s.Units.Select(u => u.Wide)]));
        }

        if (room.Chips.Count == 0 && room.Groups.Count == 0)
        {
            body.Children.Add(new TextBlock
            {
                Text = room.Count == 0
                    ? L.T("这个房间还没有设备", "No devices in this room")
                    : L.T("主界面还没有设备", "Nothing on the panel yet"),
                FontSize = 12,
                Foreground = P.Text2,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(24, 140, 24, 0),
            });
        }
    }

    /// <summary>「更多设备」导航卡。</summary>
    public UIElement MoreLink(Room room, Action open)
    {
        var grid = new Grid { Height = 40, Padding = new Thickness(12, 0, 8, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(_k.Text(L.T("更多设备", "All devices")));
        var count = _k.Text(L.T($"{room.Count} 个实体", $"{room.Count} entities"), 13, P.Text2);
        Grid.SetColumn(count, 1);
        var chev = Mdi.Icon("chevron-right", 16, P.Text3);
        chev.Margin = new Thickness(4, 0, 0, 0);
        Grid.SetColumn(chev, 2);
        grid.Children.Add(count);
        grid.Children.Add(chev);
        var b = new Button
        {
            Style = (Style)Application.Current.Resources["MacButtonStyle"],
            Background = P.Module,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 14, 0, 0),
            Content = grid,
        };
        b.Click += (_, _) => open();
        return b;
    }

    private static Grid TwoColumns(List<UIElement> units, List<bool> wide)
    {
        var grid = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        int row = 0, col = 0;
        for (var i = 0; i < units.Count; i++)
        {
            if (wide[i] && col == 1)
            {
                row++;
                col = 0;
            }

            if (grid.RowDefinitions.Count <= row)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }

            var u = (FrameworkElement)units[i];
            Grid.SetRow(u, row);
            Grid.SetColumn(u, col);
            Grid.SetColumnSpan(u, wide[i] ? 2 : 1);
            grid.Children.Add(u);
            if (wide[i] || col == 1)
            {
                row++;
                col = 0;
            }
            else
            {
                col = 1;
            }
        }

        return grid;
    }

    // ---------------- 环境胶囊 ----------------

    /// <summary>
    /// 每行 3 个等宽。收起时：房间里有固定的胶囊，就只显示固定的，其余收进「还有 N 个」；
    /// 一个都没固定时，超过 3 个就先显示前 2 个。
    /// </summary>
    private Grid Chips(Room room)
    {
        var grid = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
        for (var i = 0; i < 3; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        var all = room.Chips;
        var pinned = all.Where(e => e.Tray.Pinned).ToList();
        var folded = pinned.Count > 0 ? all.Count - pinned.Count : all.Count > 3 ? all.Count - 2 : 0;
        var shown = _chipsOpen || folded == 0 ? all.ToList() : pinned.Count > 0 ? pinned : [.. all.Take(2)];
        var cells = new List<UIElement>();
        foreach (var e in shown)
        {
            var caption = ChipCaption(e, all);
            cells.Add(Live([e.EntityId], () => Chip(e, caption)));
            Debug.Add(new { kind = "chip", e.EntityId, caption, pinned = e.Tray.Pinned });
        }

        if (folded > 0)
        {
            var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, HorizontalAlignment = HorizontalAlignment.Center };
            label.Children.Add(_k.Text(_chipsOpen ? L.T("收起", "Less") : L.T($"还有 {folded} 个", $"{folded} more"), 12, P.Text2));
            label.Children.Add(Mdi.Icon(_chipsOpen ? "chevron-up" : "chevron-down", 14, P.Text2));
            // 和胶囊一样高：它单独排到新的一行时没有别的胶囊撑高度，只有一行字高，圆角 12 一配就成了椭圆（2026-10-18 用户发现）。
            var more = new Button
            {
                Style = (Style)Application.Current.Resources["MacButtonStyle"],
                Background = P.Module,
                CornerRadius = new CornerRadius(12),
                Height = ChipHeight,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Content = label,
                Tag = ChipTag,
            };
            more.Click += (_, _) =>
            {
                _chipsOpen = !_chipsOpen;
                var parent = (Panel)grid.Parent;
                parent.Children[parent.Children.IndexOf(grid)] = Chips(room);
            };
            cells.Add(more);
        }

        for (var i = 0; i < cells.Count; i++)
        {
            if (i % 3 == 0)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }

            var c = (FrameworkElement)cells[i];
            Grid.SetRow(c, i / 3);
            Grid.SetColumn(c, i % 3);
            grid.Children.Add(c);
        }

        return grid;
    }

    /// <summary>来源一般写设备名；同一个设备有两个同类读数（比如两个 PM2.5）时改写实体名，免得分不清。</summary>
    private static string ChipCaption(Entity e, IReadOnlyList<Entity> all) =>
        e.Device is not null && all.Count(x => x.Device == e.Device && x.DeviceClass == e.DeviceClass) == 1
            ? Presenter.DeviceName(e)!
            : Presenter.ShortName(e);

    private Border Chip(Entity e, string caption)
    {
        var value = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        value.Children.Add(Mdi.Icon(pr.Icon(e), 15, P.Text2));
        var text = _k.Text(pr.StateText(e), 17, null, strong: true);
        text.CharacterSpacing = -10;
        value.Children.Add(text);
        var stack = new StackPanel { Spacing = 2 };
        stack.Children.Add(value);
        stack.Children.Add(_k.Text(caption, 11, P.Text2));
        var chip = new Border
        {
            Background = P.Module,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10, 0, 10, 0),
            Height = ChipHeight,
            Child = stack,
            Opacity = e.IsUnavailable ? 0.45 : 1,
            Tag = ChipTag,
        };
        stack.VerticalAlignment = VerticalAlignment.Center;
        ToolTipService.SetToolTip(chip, Presenter.FullName(e));
        return chip;
    }

    /// <summary>胶囊和「还有 N 个」统一的高度：读数一行（17 号）、来源一行（11 号）加上下留白。</summary>
    private const double ChipHeight = 56;

    /// <summary>胶囊和「还有 N 个」的 Tag，调试通道用它量尺寸。</summary>
    public const string ChipTag = "chip-cell";

    // ---------------- 磁贴 ----------------

    /// <summary>磁贴要跟着变的实体：自己，空调还有房间的温度传感器。</summary>
    private List<string> TileIds(Entity e, Room room) =>
        e.Domain == "climate" && RoomTemperature(room) is { } t ? [e.EntityId, t.EntityId] : [e.EntityId];

    /// <summary>
    /// 空调报不出室温时显示的室温：只用用户选的那个，即 HA 区域设置里的温度传感器（磁贴设置里也能选，写回同一处）。
    /// 没选就不显示，程序不替用户挑。
    /// </summary>
    private Entity? RoomTemperature(Room room) =>
        room.AreaSensors.ElementAtOrDefault(0) is { } id && home.Entities.TryGetValue(id, out var t) ? t : null;

    /// <summary>一张磁贴。wide 为 false 的复杂类型（用户在磁贴设置里改成了半宽）只留磁贴头，控件进详情页。</summary>
    private UIElement Tile(Entity e, string title, Room room, bool wide)
    {
        var stack = new StackPanel { Spacing = 10 };
        var head = Head(e, title, false, room);
        stack.Children.Add(head);
        var off = e.IsUnavailable;
        switch (wide || e.Domain is "light" ? e.Domain : "")
        {
            case "light" when Caps.LightBrightness(e):
                stack.Children.Add(BrightnessPill(e, head));
                break;
            case "climate":
                AddTrailing(head, ClimateStepper(e, false));
                stack.Children.Add(ClimatePopups(e));
                break;
            case "humidifier":
                AddTrailing(head, NumberStepper(e, "humidity", e.Num("min_humidity") ?? 0, e.Num("max_humidity") ?? 100, e.Num("target_humidity_step") ?? 1, "%", "set_humidity", "humidity", false));
                if (e.Supports(Caps.HumidifierModes) && e.Strings("available_modes").Count > 0)
                {
                    stack.Children.Add(Row(Popup(e, "mode", e.Strings("available_modes"), "set_mode", "mode", L.T("模式", "Mode"))));
                }

                break;
            case "water_heater" when e.Supports(Caps.WaterHeaterTargetTemp):
                AddTrailing(head, NumberStepper(e, "temperature", e.Num("min_temp") ?? 30, e.Num("max_temp") ?? 80, e.Num("target_temp_step") ?? 1, "°", "set_temperature", "temperature", false));
                if (e.Supports(Caps.WaterHeaterOperation) && e.Strings("operation_list").Count > 0)
                {
                    stack.Children.Add(Row(Popup(e, "operation_mode", e.Strings("operation_list"), "set_operation_mode", "operation_mode", L.T("模式", "Mode"))));
                }

                break;
            case "fan":
                if (FanSpeedControl(e, false) is { } speed)
                {
                    stack.Children.Add(speed);
                }

                if (e.Supports(Caps.FanPreset) && e.Strings("preset_modes").Count > 0)
                {
                    stack.Children.Add(Row(Popup(e, "preset_mode", e.Strings("preset_modes"), "set_preset_mode", "preset_mode", null)));
                }

                break;
            case "cover" or "valve":
                if (e.Supports(Caps.CoverSetPosition))
                {
                    stack.Children.Add(PositionPill(e));
                }

                stack.Children.Add(CoverButtons(e));
                break;
            case "media_player":
                if (MediaButtons(e) is { } media)
                {
                    stack.Children.Add(media);
                }

                break;
            case "vacuum" or "lawn_mower":
                stack.Children.Add(RobotButtons(e));
                break;
            case "select" or "input_select":
                stack.Children.Add(Row(SelectPopup(e)));
                break;
            case "number" or "input_number":
                AddTrailing(head, NumberControl(e, compact: true));
                break;
            default:
                AddChevron(head, e);
                break;
        }

        if (e.Domain is "light" or "fan" or "cover" or "valve" or "media_player" or "vacuum" or "lawn_mower" or "select" or "input_select")
        {
            AddChevron(head, e);
        }

        var card = _k.Card(stack);
        if (off)
        {
            card.Opacity = 0.4;
            card.IsHitTestVisible = false;
        }

        return card;
    }

    /// <summary>磁贴头：图标按钮（主动作）、名字和状态。点名字进详情页。</summary>
    private Grid Head(Entity e, string title, bool big, Room? room)
    {
        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var action = PrimaryAction.For(e, home);
        var timestamp = e.Domain is "button" or "input_button" or "scene" or "event";
        var icon = _k.CircleButton(pr.Icon(e), big ? 44 : 34, !timestamp && Presenter.IsActive(e),
            action is { } a ? () => ctl.Call(e, a.Service, a.Data) : null,
            action is null ? null : L.T("开关", "Toggle"));
        if (action is null)
        {
            icon.IsEnabled = true;
            icon.IsHitTestVisible = false;
        }

        if (ctl.IsWaiting(e.EntityId))
        {
            icon.Opacity = 0.45;
        }

        grid.Children.Add(icon);

        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(_k.Text(title, big ? 15 : 13, null, strong: true));
        var problem = ctl.Problem(e.EntityId);
        var state = _k.Text(problem ?? Summary(e, room), 11, problem is null ? P.Text2 : P.Warning);
        state.Tag = "state";
        names.Children.Add(state);
        var nameButton = new Button
        {
            Style = (Style)Application.Current.Resources["MacTextButtonStyle"],
            Padding = new Thickness(0),
            Background = null,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Content = names,
        };
        nameButton.Click += (_, _) => openDetail(e);
        Grid.SetColumn(nameButton, 1);
        grid.Children.Add(nameButton);
        return grid;
    }

    private static void AddTrailing(Grid head, UIElement trailing)
    {
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn((FrameworkElement)trailing, head.ColumnDefinitions.Count - 1);
        head.Children.Add(trailing);
    }

    private void AddChevron(Grid head, Entity e)
    {
        if (head.ColumnDefinitions.Count > 2)
        {
            return;
        }

        var b = _k.GhostButton(Mdi.Icon("chevron-right", 16, P.Text3), () => openDetail(e));
        b.Padding = new Thickness(2, 0, 2, 0);
        AddTrailing(head, b);
    }

    private static Grid Row(UIElement child)
    {
        var g = new Grid();
        g.Children.Add(child);
        return g;
    }

    /// <summary>状态行：比 HA 的状态文字多带一点关键信息（亮度、室温、风量、当前湿度）。</summary>
    private string Summary(Entity e, Room? room)
    {
        var text = pr.StateText(e);
        if (e.IsUnavailable || e.State == "unknown")
        {
            return text;
        }

        switch (e.Domain)
        {
            case "light" when e.State == "on" && Caps.BrightnessPct(e) is { } pct:
                return $"{text} · {pct}%";
            case "climate":
            {
                var action = e.Str("hvac_action") is { } act && e.State != "off" ? pr.AttrText(e, "hvac_action", act) : text;
                var current = e.Num("current_temperature") is { } t
                    ? Presenter.NumberText(t, "°", 0.1)
                    : room is not null && RoomTemperature(room) is { IsUnavailable: false } s ? pr.StateText(s) : null;
                return current is null ? action : action + " · " + L.T("室温 ", "Room ") + current;
            }

            case "fan" when e.State == "on":
            {
                var speed = Caps.FanSpeeds(e) is not null ? Caps.FanSpeedText(Caps.FanSpeedOf(e)) : e.Num("percentage") is { } p ? $"{p:0}%" : text;
                return e.Str("preset_mode") is { } preset ? speed + " · " + pr.AttrText(e, "preset_mode", preset) : speed;
            }

            case "humidifier" when e.State == "on":
            {
                var action = e.Str("action") is { } act ? pr.AttrText(e, "action", act) : text;
                return e.Num("current_humidity") is { } h ? action + " · " + L.T("当前 ", "Now ") + $"{h:0}%" : action;
            }

            case "cover" or "valve" when e.Num("current_position") is { } pos:
                return $"{text} · {pos:0}%";
            case "media_player" when e.Str("media_title") is { } title:
                return $"{text} · {title}";
        }

        return text;
    }

    // ---------------- 各类控件 ----------------

    private PillSlider BrightnessPill(Entity e, Grid? head)
    {
        var pct = e.State == "on" ? Caps.BrightnessPct(e) : null;
        var pill = _k.Pill("brightness-6", 1, 100, 1, pct, v => $"{v:0}%");
        pill.Changing += v =>
        {
            if (head?.Children.OfType<Button>().Select(b => b.Content).OfType<StackPanel>().SelectMany(s => s.Children.OfType<TextBlock>()).FirstOrDefault(t => (string?)t.Tag == "state") is { } state)
            {
                state.Text = (e.State == "on" ? pr.StateText(e) + " · " : "") + $"{v:0}%";
            }
        };
        pill.Committed += v => ctl.Call(e, "turn_on", new JsonObject { ["brightness_pct"] = (int)v });
        return pill;
    }

    private PillSlider PositionPill(Entity e)
    {
        var pill = _k.Pill(e.Domain == "valve" ? "valve" : "window-shutter", 0, 100, 1, e.Num("current_position"), v => $"{v:0}%");
        pill.Committed += v => ctl.Call(e, e.Domain == "valve" ? "set_valve_position" : "set_cover_position", new JsonObject { ["position"] = (int)v });
        return pill;
    }

    private StackPanel ClimateStepper(Entity e, bool big)
    {
        if (!e.Supports(Caps.ClimateTargetTemp) || e.Num("temperature") is not { } target)
        {
            return new StackPanel();
        }

        var step = Caps.TempStep(e);
        var min = e.Num("min_temp") ?? 7;
        var max = e.Num("max_temp") ?? 35;
        var shown = ctl.PendingValue(e.EntityId, "temperature") ?? target;
        void Nudge(double d) => ctl.Nudge(e, "temperature", target, d, min, max, v => ctl.Call(e, "set_temperature", new JsonObject { ["temperature"] = v }, wait: false));
        return _k.Stepper(Presenter.NumberText(shown, "°", step), () => Nudge(-step), () => Nudge(step), big);
    }

    private StackPanel NumberStepper(Entity e, string attr, double min, double max, double step, string unit, string service, string field, bool big)
    {
        var current = e.Num(attr);
        if (current is null)
        {
            return new StackPanel();
        }

        var shown = ctl.PendingValue(e.EntityId, attr) ?? current.Value;
        void Nudge(double d) => ctl.Nudge(e, attr, current.Value, d, min, max, v => ctl.Call(e, service, new JsonObject { [field] = v }, wait: false));
        return _k.Stepper(Presenter.NumberText(shown, unit, step), () => Nudge(-step), () => Nudge(step), big);
    }

    /// <summary>空调磁贴下面一排弹出按钮：模式、风速、摆风（各自按功能位出现）。</summary>
    private Grid ClimatePopups(Entity e)
    {
        var popups = new List<Button>
        {
            _k.Popup(pr.StateText(e), [.. Caps.HvacModes(e).Select(m => (m, pr.StateText(e, m)))], e.State,
                m => ctl.Call(e, "set_hvac_mode", new JsonObject { ["hvac_mode"] = m })),
        };
        if (e.Supports(Caps.ClimateFanMode) && e.Strings("fan_modes").Count > 0)
        {
            popups.Add(Popup(e, "fan_mode", e.Strings("fan_modes"), "set_fan_mode", "fan_mode", L.T("风速", "Fan")));
        }

        if (e.Supports(Caps.ClimateSwing) && e.Strings("swing_modes").Count > 0)
        {
            popups.Add(Popup(e, "swing_mode", e.Strings("swing_modes"), "set_swing_mode", "swing_mode", L.T("摆风", "Swing")));
        }

        var grid = new Grid { ColumnSpacing = 6 };
        for (var i = 0; i < popups.Count; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(popups[i], i);
            grid.Children.Add(popups[i]);
        }

        return grid;
    }

    /// <summary>某个属性的弹出按钮：显示「前缀 当前值」，选了就调服务。</summary>
    private Button Popup(Entity e, string attr, IReadOnlyList<string> options, string service, string field, string? prefix)
    {
        var current = e.Str(attr);
        var label = current is null ? prefix ?? "" : (prefix is null ? "" : prefix + " ") + pr.AttrText(e, attr, current);
        return _k.Popup(label, [.. options.Select(o => (o, pr.AttrText(e, attr, o)))], current,
            v => ctl.Call(e, service, new JsonObject { [field] = v }));
    }

    private Button SelectPopup(Entity e) =>
        _k.Popup(pr.StateText(e), [.. e.Strings("options").Select(o => (o, pr.StateText(e, o)))], e.State,
            o => ctl.Call(e, "select_option", new JsonObject { ["option"] = o }));

    /// <summary>风量：不超过 4 档用分段按钮（关 / 低 / 中 / 高），否则用滑条。</summary>
    private UIElement? FanSpeedControl(Entity e, bool big)
    {
        if (!e.Supports(Caps.FanSetSpeed))
        {
            return null;
        }

        if (Caps.FanSpeeds(e) is { } speeds)
        {
            return _k.Segmented([.. speeds.Select(s => (s, Caps.FanSpeedText(s)))], Caps.FanSpeedOf(e),
                s =>
                {
                    if (s == "off")
                    {
                        ctl.Call(e, "turn_off");
                    }
                    else
                    {
                        ctl.Call(e, "set_percentage", new JsonObject { ["percentage"] = Caps.FanPercentage(e, s) });
                    }
                },
                big ? 30 : 24);
        }

        var pill = _k.Pill("fan", 0, 100, e.Num("percentage_step") ?? 1, e.State == "on" ? e.Num("percentage") : 0, v => $"{v:0}%");
        pill.Committed += v => ctl.Call(e, "set_percentage", new JsonObject { ["percentage"] = (int)Math.Round(v) });
        return pill;
    }

    private Grid CoverButtons(Entity e)
    {
        var valve = e.Domain == "valve";
        var grid = new Grid { ColumnSpacing = 6 };
        var buttons = new List<Button>();
        var closed = e.State == "closed";
        var fullyOpen = e.Num("current_position") is { } p ? p >= 100 : e.State == "open";
        if (e.Supports(Caps.CoverOpen))
        {
            var b = _k.PlainButton(L.T("开", "Open"), valve ? "valve-open" : "arrow-up", () => ctl.Call(e, valve ? "open_valve" : "open_cover"), 26);
            b.IsEnabled = !fullyOpen && e.State != "opening";
            buttons.Add(b);
        }

        if (e.Supports(Caps.CoverStop))
        {
            buttons.Add(_k.PlainButton(L.T("停", "Stop"), "stop", () => ctl.Call(e, valve ? "stop_valve" : "stop_cover", wait: false), 26));
        }

        if (e.Supports(Caps.CoverClose))
        {
            var b = _k.PlainButton(L.T("关", "Close"), valve ? "valve-closed" : "arrow-down", () => ctl.Call(e, valve ? "close_valve" : "close_cover"), 26);
            b.IsEnabled = !closed && e.State != "closing";
            buttons.Add(b);
        }

        for (var i = 0; i < buttons.Count; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            buttons[i].HorizontalAlignment = HorizontalAlignment.Stretch;
            Grid.SetColumn(buttons[i], i);
            grid.Children.Add(buttons[i]);
        }

        return grid;
    }

    /// <summary>播放控制：照 HA 的 computeMediaControls 精简——上一首、播放 / 暂停、下一首。</summary>
    private Grid? MediaButtons(Entity e)
    {
        if (!Presenter.IsActive(e))
        {
            return null;
        }

        var grid = new Grid { ColumnSpacing = 6 };
        var buttons = new List<Button>();
        if (e.Supports(Caps.MediaPrevious))
        {
            buttons.Add(_k.PlainButton("", "skip-previous", () => ctl.Call(e, "media_previous_track", wait: false), 26));
        }

        if (e.Supports(Caps.MediaPause) || e.Supports(Caps.MediaPlay))
        {
            var playing = e.State == "playing";
            buttons.Add(_k.PlainButton("", playing ? "pause" : "play", () => ctl.Call(e, "media_play_pause"), 26));
        }

        if (e.Supports(Caps.MediaNext))
        {
            buttons.Add(_k.PlainButton("", "skip-next", () => ctl.Call(e, "media_next_track", wait: false), 26));
        }

        if (buttons.Count == 0)
        {
            return null;
        }

        for (var i = 0; i < buttons.Count; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            buttons[i].HorizontalAlignment = HorizontalAlignment.Stretch;
            Grid.SetColumn(buttons[i], i);
            grid.Children.Add(buttons[i]);
        }

        return grid;
    }

    private Grid RobotButtons(Entity e)
    {
        var mower = e.Domain == "lawn_mower";
        var running = e.State is "cleaning" or "mowing";
        var grid = new Grid { ColumnSpacing = 6 };
        var buttons = new List<Button>();
        if (running && e.Supports(mower ? Caps.LawnMowerPause : Caps.VacuumPause))
        {
            buttons.Add(_k.PlainButton(L.T("暂停", "Pause"), "pause", () => ctl.Call(e, "pause"), 26));
        }
        else if (!running && e.Supports(mower ? Caps.LawnMowerStart : Caps.VacuumStart))
        {
            buttons.Add(_k.PlainButton(L.T("开始", "Start"), "play", () => ctl.Call(e, mower ? "start_mowing" : "start"), 26));
        }

        if (!mower && e.Supports(Caps.VacuumStop))
        {
            buttons.Add(_k.PlainButton(L.T("停止", "Stop"), "stop", () => ctl.Call(e, "stop"), 26));
        }

        if (e.Supports(mower ? Caps.LawnMowerDock : Caps.VacuumReturnHome))
        {
            buttons.Add(_k.PlainButton(L.T("回充", "Dock"), "home-import-outline", () => ctl.Call(e, mower ? "dock" : "return_to_base"), 26));
        }

        for (var i = 0; i < buttons.Count; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            buttons[i].HorizontalAlignment = HorizontalAlignment.Stretch;
            Grid.SetColumn(buttons[i], i);
            grid.Children.Add(buttons[i]);
        }

        return grid;
    }

    /// <summary>数值实体：照 HA 的 showNumberSlider 选滑条或 ±。</summary>
    private FrameworkElement NumberControl(Entity e, bool compact)
    {
        var min = e.Num("min") ?? 0;
        var max = e.Num("max") ?? 100;
        var step = e.Num("step") ?? 1;
        var unit = e.Str("unit_of_measurement");
        var value = double.TryParse(e.State, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : (double?)null;
        if (Caps.NumberSlider(e))
        {
            var pill = _k.Pill(pr.Icon(e), min, max, step, value, x => Presenter.NumberText(x, unit, step));
            pill.Width = compact ? 140 : double.NaN;
            pill.Committed += x => ctl.Call(e, "set_value", new JsonObject { ["value"] = x });
            return pill;
        }

        if (value is null)
        {
            return _k.Text(pr.StateText(e), 13, P.Text2);
        }

        var shown = ctl.PendingValue(e.EntityId, "value") ?? value.Value;
        void Nudge(double d) => ctl.Nudge(e, "value", value.Value, d, min, max, x => ctl.Call(e, "set_value", new JsonObject { ["value"] = x }, wait: false));
        var s = _k.Stepper(Presenter.NumberText(shown, unit, step), () => Nudge(-step), () => Nudge(step));
        if (compact)
        {
            ((TextBlock)s.Children[1]).FontSize = 13;
            ((TextBlock)s.Children[1]).MinWidth = 56;
        }

        return s;
    }

    // ---------------- 设备卡和行 ----------------

    /// <summary>同一个设备的多个实体：卡头是设备图标和名字，下面每个实体一行。</summary>
    private Border DeviceCard(Device d, List<Entity> members)
    {
        var stack = new StackPanel();
        var head = new Grid { ColumnSpacing = 10, Margin = new Thickness(0, 0, 0, 4) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var icon = _k.CircleButton(pr.Icon(members[0]), 34, false, null);
        icon.IsEnabled = true;
        icon.IsHitTestVisible = false;
        head.Children.Add(icon);
        var name = _k.Text(d.Name, 13, null, strong: true);
        Grid.SetColumn(name, 1);
        head.Children.Add(name);
        stack.Children.Add(head);
        for (var i = 0; i < members.Count; i++)
        {
            var e = members[i];
            if (i > 0)
            {
                stack.Children.Add(_k.Separator());
            }

            stack.Children.Add(Live([e.EntityId], () => EntityRow(e, Presenter.ShortName(e), star: false)));
        }

        Debug.Add(new { kind = "deviceCard", device = d.Name, entities = members.Select(m => m.EntityId) });
        return _k.Card(stack);
    }

    /// <summary>一行：（星标）名字，右边是控件或读数。点名字进详情页。</summary>
    private Grid EntityRow(Entity e, string name, bool star)
    {
        var grid = new Grid { MinHeight = 38, ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (star)
        {
            var on = LabelId is not null && e.Labels.Contains(LabelId);
            var s = _k.GhostButton(Mdi.Icon(on ? "star" : "star-outline", 15, on ? P.Star : P.Text3), () => edits.Pin(e, !on),
                on ? L.T("从主界面移除", "Remove from panel") : L.T("放到主界面", "Add to panel"));
            s.Padding = new Thickness(2, 0, 2, 0);
            grid.Children.Add(s);
        }

        var problem = ctl.Problem(e.EntityId);
        var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        label.Children.Add(_k.Text(name, 13));
        if (problem is not null)
        {
            label.Children.Add(_k.Text(problem, 11, P.Warning));
        }

        var nameButton = new Button
        {
            Style = (Style)Application.Current.Resources["MacTextButtonStyle"],
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Content = label,
        };
        nameButton.Click += (_, _) => openDetail(e);
        Grid.SetColumn(nameButton, 1);
        grid.Children.Add(nameButton);

        var control = RowControl(e);
        control.VerticalAlignment = VerticalAlignment.Center;
        if (ctl.IsWaiting(e.EntityId))
        {
            control.Opacity = 0.45;
        }

        Grid.SetColumn(control, 2);
        grid.Children.Add(control);
        if (e.IsUnavailable)
        {
            grid.Opacity = 0.45;
        }

        return grid;
    }

    /// <summary>行右边的控件：能开关的是开关，按钮类是「按一下」，下拉、数值、窗帘各有控件，其余显示读数。</summary>
    private FrameworkElement RowControl(Entity e)
    {
        if (e.IsUnavailable)
        {
            return _k.Text(pr.StateText(e), 13, P.Text2);
        }

        switch (e.Domain)
        {
            case "button" or "input_button":
                return _k.PlainButton(L.T("按一下", "Press"), "gesture-tap", () => ctl.Call(e, "press", wait: false));
            case "scene":
                return _k.PlainButton(L.T("启用", "Activate"), "play", () => ctl.Call(e, "turn_on", wait: false));
            case "script":
                return _k.PlainButton(e.State == "on" ? L.T("停止", "Stop") : L.T("运行", "Run"), e.State == "on" ? "stop" : "play",
                    () => ctl.Call(e, e.State == "on" ? "turn_off" : "turn_on"));
            case "lock":
                return _k.PlainButton(e.State == "locked" ? L.T("开锁", "Unlock") : L.T("上锁", "Lock"), e.State == "locked" ? "lock-open-variant" : "lock",
                    () => ctl.Call(e, e.State == "locked" ? "unlock" : "lock"));
            case "select" or "input_select":
            {
                var p = SelectPopup(e);
                p.Width = 140;
                return p;
            }

            case "number" or "input_number":
                return NumberControl(e, compact: true);
            case "cover" or "valve":
            {
                var b = CoverButtons(e);
                b.Width = 150;
                return b;
            }
        }

        // 有主动作、而且是开 / 关两态的（含空调、播放器、热水器这类「关」和「各种开」）：用开关。
        if (PrimaryAction.For(e, home) is { } a && (e.State is "on" or "off" || e.Domain is "climate" or "media_player" or "water_heater"))
        {
            return _k.Switch(Presenter.IsActive(e), () => ctl.Call(e, a.Service, a.Data));
        }

        return _k.Text(pr.StateText(e), 13, P.Text2);
    }

    // ======================= 更多设备 =======================

    /// <summary>每个设备一张可折叠的卡片（系统设置那种分组列表），展开后普通实体在前，「设置」「诊断」各一节。</summary>
    public void BuildMore(StackPanel body, Room room)
    {
        var admin = edits.CanEdit;
        foreach (var d in room.Devices)
        {
            var key = room.Id + "|" + (d.Device?.Id ?? "");
            var open = _openDevices.Contains(key);
            var first = d.Normal.Concat(d.Config).Concat(d.Diagnostic).FirstOrDefault();
            var total = d.Normal.Count + d.Config.Count + d.Diagnostic.Count;

            var head = new Grid { Height = 46, ColumnSpacing = 10, Padding = new Thickness(12, 0, 10, 0) };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var icon = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(14), Background = P.Control, Child = Mdi.Icon(first is null ? "devices" : pr.Icon(first), 15) };
            head.Children.Add(icon);
            var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            names.Children.Add(_k.Text(d.Name, 13, null, strong: true));
            names.Children.Add(_k.Text(L.T($"{total} 个实体", $"{total} entities"), 11, P.Text2));
            Grid.SetColumn(names, 1);
            head.Children.Add(names);
            var chev = Mdi.Icon(open ? "chevron-down" : "chevron-right", 16, P.Text3);
            Grid.SetColumn(chev, 2);
            head.Children.Add(chev);

            var card = new StackPanel();
            var headButton = new Button
            {
                Style = (Style)Application.Current.Resources["MacButtonStyle"],
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(12),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Content = head,
            };
            card.Children.Add(headButton);
            var content = new StackPanel { Padding = new Thickness(12, 0, 12, 6), Visibility = open ? Visibility.Visible : Visibility.Collapsed };
            if (open)
            {
                FillDevice(content, d, admin);
            }

            headButton.Click += (_, _) =>
            {
                var nowOpen = !_openDevices.Remove(key);
                if (nowOpen)
                {
                    _openDevices.Add(key);
                    if (content.Children.Count == 0)
                    {
                        FillDevice(content, d, admin);
                    }
                }

                content.Visibility = nowOpen ? Visibility.Visible : Visibility.Collapsed;
                chev.Glyph = Mdi.Glyph(nowOpen ? "chevron-down" : "chevron-right");
            };
            card.Children.Add(content);
            body.Children.Add(new Border { Background = P.Module, CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 0, 0, 8), Child = card });
        }

        Debug.Add(new { kind = "more", devices = room.Devices.Count, open = _openDevices.Count(k => k.StartsWith(room.Id + "|", StringComparison.Ordinal)) });
    }

    private void FillDevice(StackPanel content, DeviceGroup d, bool admin)
    {
        void Add(IReadOnlyList<Entity> list, string? title)
        {
            if (list.Count == 0)
            {
                return;
            }

            if (title is not null)
            {
                content.Children.Add(new TextBlock { Text = title, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = P.Text2, Margin = new Thickness(2, 10, 2, 4) });
            }

            for (var i = 0; i < list.Count; i++)
            {
                var e = list[i];
                if (i > 0 || title is null && content.Children.Count > 0)
                {
                    content.Children.Add(_k.Separator());
                }

                content.Children.Add(Live([e.EntityId], () => EntityRow(e, Presenter.ShortName(e), star: admin)));
            }
        }

        Add(d.Normal, null);
        Add(d.Config, L.T("设置", "Configuration"));
        Add(d.Diagnostic, L.T("诊断", "Diagnostic"));
    }

    // ======================= 详情页 =======================

    public void BuildDetail(StackPanel body, Entity e, Room? room) =>
        body.Children.Add(Live(e.Domain == "climate" && room is not null && RoomTemperature(room) is { } t ? [e.EntityId, t.EntityId] : [e.EntityId], () => Detail(e, room)));

    private UIElement Detail(Entity e, Room? room)
    {
        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(Head(e, Presenter.FullName(e), true, room));
        void Label(string text) => stack.Children.Add(new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = P.Text2, Margin = new Thickness(2, 12, 2, 2) });
        UIElement Choice(Entity x, string attr, IReadOnlyList<string> options, string service, string field, Func<string, string> text) =>
            options.Count <= 5
                ? _k.Segmented([.. options.Select(o => (o, text(o)))], x.Str(attr), v => ctl.Call(x, service, new JsonObject { [field] = v }), 28)
                : Row(_k.Popup(x.Str(attr) is { } c ? text(c) : "", [.. options.Select(o => (o, text(o)))], x.Str(attr), v => ctl.Call(x, service, new JsonObject { [field] = v })));

        switch (e.Domain)
        {
            case "light":
                if (Caps.LightBrightness(e))
                {
                    Label(L.T("亮度", "Brightness"));
                    stack.Children.Add(BrightnessPill(e, null));
                }

                if (Caps.LightColorTemp(e))
                {
                    Label(L.T("色温", "Color temperature"));
                    var min = e.Num("min_color_temp_kelvin") ?? 2000;
                    var max = e.Num("max_color_temp_kelvin") ?? 6500;
                    var ct = _k.Pill("thermometer-lines", min, max, 50, e.Num("color_temp_kelvin"), v => $"{v:0} K");
                    ct.Committed += v => ctl.Call(e, "turn_on", new JsonObject { ["color_temp_kelvin"] = (int)v });
                    stack.Children.Add(ct);
                }

                if (Caps.LightColor(e))
                {
                    Label(L.T("颜色", "Color"));
                    var hue = e.Attributes["hs_color"] is JsonArray hs && hs.Count > 0 && hs[0] is JsonValue h0 ? h0.GetValue<double>() : (double?)null;
                    var pill = _k.Pill("palette", 0, 360, 1, hue, v => $"{v:0}°");
                    pill.Background = HueBrush();
                    pill.Committed += v => ctl.Call(e, "turn_on", new JsonObject { ["hs_color"] = new JsonArray(v, 100) });
                    stack.Children.Add(pill);
                }

                if (e.Supports(Caps.LightEffect) && e.Strings("effect_list").Count > 0)
                {
                    Label(L.T("效果", "Effect"));
                    stack.Children.Add(Row(_k.Popup(e.Str("effect") ?? L.T("无", "None"), [.. e.Strings("effect_list").Select(x => (x, x))], e.Str("effect"),
                        x => ctl.Call(e, "turn_on", new JsonObject { ["effect"] = x }))));
                }

                break;

            case "climate":
                if (e.Supports(Caps.ClimateTargetTemp) && e.Num("temperature") is not null)
                {
                    Label(L.T("目标温度", "Target temperature"));
                    var s = ClimateStepper(e, true);
                    s.HorizontalAlignment = HorizontalAlignment.Center;
                    stack.Children.Add(s);
                }
                else if (e.Supports(Caps.ClimateTargetRange))
                {
                    var step = Caps.TempStep(e);
                    Label(L.T("温度下限", "Low"));
                    stack.Children.Add(NumberStepper(e, "target_temp_low", e.Num("min_temp") ?? 7, e.Num("max_temp") ?? 35, step, "°", "set_temperature", "target_temp_low", false));
                    Label(L.T("温度上限", "High"));
                    stack.Children.Add(NumberStepper(e, "target_temp_high", e.Num("min_temp") ?? 7, e.Num("max_temp") ?? 35, step, "°", "set_temperature", "target_temp_high", false));
                }

                Label(L.T("模式", "Mode"));
                var modes = Caps.HvacModes(e);
                stack.Children.Add(modes.Count <= 6
                    ? _k.Segmented([.. modes.Select(m => (m, pr.StateText(e, m)))], e.State, m => ctl.Call(e, "set_hvac_mode", new JsonObject { ["hvac_mode"] = m }), 28)
                    : Row(_k.Popup(pr.StateText(e), [.. modes.Select(m => (m, pr.StateText(e, m)))], e.State, m => ctl.Call(e, "set_hvac_mode", new JsonObject { ["hvac_mode"] = m }))));
                if (e.Supports(Caps.ClimateFanMode) && e.Strings("fan_modes").Count > 0)
                {
                    Label(L.T("风速", "Fan"));
                    stack.Children.Add(Choice(e, "fan_mode", e.Strings("fan_modes"), "set_fan_mode", "fan_mode", v => pr.AttrText(e, "fan_mode", v)));
                }

                if (e.Supports(Caps.ClimateSwing) && e.Strings("swing_modes").Count > 0)
                {
                    Label(L.T("摆风", "Swing"));
                    stack.Children.Add(Choice(e, "swing_mode", e.Strings("swing_modes"), "set_swing_mode", "swing_mode", v => pr.AttrText(e, "swing_mode", v)));
                }

                if (e.Supports(Caps.ClimateSwingHorizontal) && e.Strings("swing_horizontal_modes").Count > 0)
                {
                    Label(L.T("左右摆风", "Horizontal swing"));
                    stack.Children.Add(Choice(e, "swing_horizontal_mode", e.Strings("swing_horizontal_modes"), "set_swing_horizontal_mode", "swing_horizontal_mode", v => pr.AttrText(e, "swing_horizontal_mode", v)));
                }

                if (e.Supports(Caps.ClimatePreset) && e.Strings("preset_modes").Count > 0)
                {
                    Label(L.T("预设", "Preset"));
                    stack.Children.Add(Choice(e, "preset_mode", e.Strings("preset_modes"), "set_preset_mode", "preset_mode", v => pr.AttrText(e, "preset_mode", v)));
                }

                if (e.Supports(Caps.ClimateTargetHumidity) && e.Num("humidity") is not null)
                {
                    Label(L.T("目标湿度", "Target humidity"));
                    stack.Children.Add(NumberStepper(e, "humidity", e.Num("min_humidity") ?? 0, e.Num("max_humidity") ?? 100, e.Num("target_humidity_step") ?? 1, "%", "set_humidity", "humidity", false));
                }

                break;

            case "fan":
                if (FanSpeedControl(e, true) is { } speed)
                {
                    Label(L.T("风量", "Speed"));
                    stack.Children.Add(speed);
                }

                if (e.Supports(Caps.FanPreset) && e.Strings("preset_modes").Count > 0)
                {
                    Label(L.T("模式", "Mode"));
                    stack.Children.Add(Choice(e, "preset_mode", e.Strings("preset_modes"), "set_preset_mode", "preset_mode", v => pr.AttrText(e, "preset_mode", v)));
                }

                if (e.Supports(Caps.FanOscillate))
                {
                    stack.Children.Add(SwitchRow(L.T("摆头", "Oscillate"), e.Attributes["oscillating"]?.GetValueKind() == System.Text.Json.JsonValueKind.True,
                        on => ctl.Call(e, "oscillate", new JsonObject { ["oscillating"] = on })));
                }

                if (e.Supports(Caps.FanDirection))
                {
                    Label(L.T("方向", "Direction"));
                    stack.Children.Add(_k.Segmented([("forward", L.T("正转", "Forward")), ("reverse", L.T("反转", "Reverse"))], e.Str("direction"),
                        v => ctl.Call(e, "set_direction", new JsonObject { ["direction"] = v }), 28));
                }

                break;

            case "humidifier":
            {
                Label(L.T("目标湿度", "Target humidity"));
                var s = NumberStepper(e, "humidity", e.Num("min_humidity") ?? 0, e.Num("max_humidity") ?? 100, e.Num("target_humidity_step") ?? 1, "%", "set_humidity", "humidity", true);
                s.HorizontalAlignment = HorizontalAlignment.Center;
                stack.Children.Add(s);
                if (e.Supports(Caps.HumidifierModes) && e.Strings("available_modes").Count > 0)
                {
                    Label(L.T("模式", "Mode"));
                    stack.Children.Add(Choice(e, "mode", e.Strings("available_modes"), "set_mode", "mode", v => pr.AttrText(e, "mode", v)));
                }

                break;
            }

            case "water_heater":
                if (e.Supports(Caps.WaterHeaterTargetTemp))
                {
                    Label(L.T("目标温度", "Target temperature"));
                    var s = NumberStepper(e, "temperature", e.Num("min_temp") ?? 30, e.Num("max_temp") ?? 80, e.Num("target_temp_step") ?? 1, "°", "set_temperature", "temperature", true);
                    s.HorizontalAlignment = HorizontalAlignment.Center;
                    stack.Children.Add(s);
                }

                if (e.Supports(Caps.WaterHeaterOperation) && e.Strings("operation_list").Count > 0)
                {
                    Label(L.T("模式", "Mode"));
                    stack.Children.Add(Choice(e, "operation_mode", e.Strings("operation_list"), "set_operation_mode", "operation_mode", v => pr.AttrText(e, "operation_mode", v)));
                }

                break;

            case "cover" or "valve":
                if (e.Supports(Caps.CoverSetPosition))
                {
                    Label(L.T("位置", "Position"));
                    stack.Children.Add(PositionPill(e));
                }

                stack.Children.Add(CoverButtons(e));
                break;

            case "media_player":
                if (MediaButtons(e) is { } media)
                {
                    stack.Children.Add(media);
                }

                if (e.Supports(Caps.MediaVolumeSet))
                {
                    Label(L.T("音量", "Volume"));
                    var vol = _k.Pill("volume-high", 0, 100, 1, e.Num("volume_level") is { } lv ? lv * 100 : null, v => $"{v:0}%");
                    vol.Committed += v => ctl.Call(e, "volume_set", new JsonObject { ["volume_level"] = Math.Round(v / 100, 2) }, wait: false);
                    stack.Children.Add(vol);
                }

                if (e.Strings("source_list").Count > 0)
                {
                    Label(L.T("音源", "Source"));
                    stack.Children.Add(Row(_k.Popup(e.Str("source") ?? "", [.. e.Strings("source_list").Select(x => (x, x))], e.Str("source"),
                        x => ctl.Call(e, "select_source", new JsonObject { ["source"] = x }))));
                }

                break;

            case "vacuum" or "lawn_mower":
                stack.Children.Add(RobotButtons(e));
                if (e.Domain == "vacuum" && e.Strings("fan_speed_list").Count > 0)
                {
                    Label(L.T("吸力", "Suction"));
                    stack.Children.Add(Choice(e, "fan_speed", e.Strings("fan_speed_list"), "set_fan_speed", "fan_speed", v => pr.AttrText(e, "fan_speed", v)));
                }

                break;

            case "lock" when e.Supports(Caps.LockOpen):
                stack.Children.Add(OpenDoorButton(e));
                break;

            default:
            {
                var control = RowControl(e);
                if (control is not TextBlock)
                {
                    control.HorizontalAlignment = HorizontalAlignment.Left;
                    control.Margin = new Thickness(0, 12, 0, 0);
                    stack.Children.Add(control);
                }

                break;
            }
        }

        if (e.LastChanged > 0)
        {
            var when = DateTimeOffset.FromUnixTimeMilliseconds((long)(e.LastChanged * 1000)).ToLocalTime();
            stack.Children.Add(new TextBlock
            {
                Text = L.T("最近变化：", "Last changed: ") + when.ToString("g", L.Culture),
                FontSize = 11,
                Foreground = P.Text3,
                Margin = new Thickness(2, 16, 2, 0),
            });
        }

        return _k.Card(stack, 14);
    }

    private Grid SwitchRow(string text, bool on, Action<bool> toggled)
    {
        var g = new Grid { MinHeight = 38, Margin = new Thickness(0, 8, 0, 0) };
        g.Children.Add(_k.Text(text));
        var s = _k.Switch(on, () => toggled(!on));
        s.HorizontalAlignment = HorizontalAlignment.Right;
        g.Children.Add(s);
        return g;
    }

    /// <summary>门锁的「开门」：和 HA 一样要在 5 秒内点第二次确认。</summary>
    private Button OpenDoorButton(Entity e)
    {
        Button? b = null;
        var armedUntil = 0L;
        b = _k.PlainButton(L.T("开门", "Open door"), "door-open", () =>
        {
            if (Environment.TickCount64 < armedUntil)
            {
                ctl.Call(e, "open");
                return;
            }

            armedUntil = Environment.TickCount64 + 5000;
            ((TextBlock)((StackPanel)b!.Content).Children[1]).Text = L.T("再点一次开门", "Click again to open");
        }, 30);
        b.Margin = new Thickness(0, 12, 0, 0);
        return b;
    }

    private static LinearGradientBrush HueBrush()
    {
        var brush = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(1, 0) };
        for (var i = 0; i <= 6; i++)
        {
            var c = Windows.UI.Color.FromArgb(0x99, 0, 0, 0);
            var h = i * 60 % 360;
            var x = (byte)(255 * (1 - Math.Abs((h / 60.0 % 2) - 1)));
            (c.R, c.G, c.B) = (h / 60) switch
            {
                0 => ((byte)255, x, (byte)0),
                1 => (x, (byte)255, (byte)0),
                2 => ((byte)0, (byte)255, x),
                3 => ((byte)0, x, (byte)255),
                4 => (x, (byte)0, (byte)255),
                _ => ((byte)255, (byte)0, x),
            };
            brush.GradientStops.Add(new GradientStop { Color = c, Offset = i / 6.0 });
        }

        return brush;
    }
}
