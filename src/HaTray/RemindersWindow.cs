using HaTray.Ha;
using HaTray.Model;
using HaTray.Native;
using HaTray.Ui;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;

namespace HaTray;

/// <summary>
/// 提醒窗口。三页在同一个窗口里切换：已订阅列表、选来源、设条件。
/// 选来源按区域、设备逐层折叠；条件用徽章选值，符号统一成 = / &lt; / &gt;。
/// 条件保存在 HA 的 options.ha_tray，保存成功后才更新本地模型。
/// </summary>
internal sealed class RemindersWindow : Window
{
    private sealed record Choice(string Value, string Text)
    {
        public override string ToString() => Text;
    }

    private enum Page
    {
        List,
        Source,
        Condition,
    }

    private static readonly Brush Clear = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

    private readonly Settings _settings;
    private readonly Home _home;
    private readonly Func<IHomeBackend?> _backend;
    private readonly ReminderService _service;
    private readonly ContentControl _page = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(24, 0, 24, 12) };
    private readonly HashSet<string> _selected = new(StringComparer.Ordinal);
    private readonly List<string> _debugRows = [];
    private Page _pageKind = Page.List;
    private Entity? _entity;
    private string _kindValue = "state";
    private string? _error;
    private bool _saving;

    // 条件页控件，每次进入重画，不复用（复用会让已经挂在别处的控件再次添加时崩溃）。
    private WrapPanel? _values;
    private TextBox? _other;
    private NumberBox? _threshold;
    private NumberBox? _reset;
    private AutoSuggestBox? _attribute;
    private TextBox? _attributeValue;
    private ToggleSwitch? _enabled;
    private Button? _save;
    private Button? _remove;
    private TextBlock? _current;

    public RemindersWindow(Settings settings, Home home, Func<IHomeBackend?> backend, ReminderService service)
    {
        _settings = settings;
        _home = home;
        _backend = backend;
        _service = service;
        Title = L.T("提醒", "Notifications");
        SystemBackdrop = new MicaBackdrop();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "HaTray.ico"));
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.IsMaximizable = false;
            p.IsMinimizable = false;
        }

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(_page);
        Grid.SetRow(_status, 1);
        root.Children.Add(_status);
        Content = root;

        home.StructureChanged += OnStructure;
        home.StatesChanged += OnStates;
        service.Changed += UpdateStatus;
        Closed += (_, _) =>
        {
            home.StructureChanged -= OnStructure;
            home.StatesChanged -= OnStates;
            service.Changed -= UpdateStatus;
        };
        ShowList();
        var hwnd = WindowNative.GetWindowHandle(this);
        var scale = Win32.GetDpiForWindow(hwnd) / 96.0;
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        var w = Math.Min((int)(600 * scale), work.Width);
        var h = Math.Min((int)(800 * scale), work.Height);
        AppWindow.MoveAndResize(new RectInt32(work.X + (work.Width - w) / 2, work.Y + (work.Height - h) / 2, w, h));
    }

    // ---------------- 第一页：已订阅 ----------------

    /// <summary>回到已订阅列表（保存完成、调试用）。</summary>
    public void ShowListPage() => ShowList();

    /// <summary>直接打开选来源页（调试用）。</summary>
    public void ShowSourcePage() => ShowSource();

    private void ShowList()
    {
        _pageKind = Page.List;
        _entity = null;
        _kindValue = "state";
        _selected.Clear();
        _values = null;
        _save = _remove = null;
        _current = null;
        _debugRows.Clear();

        var body = new StackPanel { Spacing = 18, Padding = new Thickness(24, 20, 24, 8) };
        body.Children.Add(new TextBlock
        {
            Text = Title,
            FontSize = 22,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });

        var receive = new ToggleSwitch
        {
            Header = L.T("接收 Windows 提醒", "Receive Windows notifications"),
            IsOn = _settings.RemindersEnabled,
        };
        receive.Toggled += (_, _) =>
        {
            var old = _settings.RemindersEnabled;
            try
            {
                _settings.RemindersEnabled = receive.IsOn;
                _settings.Save();
                _error = null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _settings.RemindersEnabled = old;
                _error = L.T("保存失败：", "Couldn't save: ") + ex.Message;
            }

            UpdateStatus();
        };
        body.Children.Add(receive);

        var test = new Button { Content = L.T("发送测试提醒", "Send test notification") };
        test.Click += (_, _) =>
        {
            _service.Test();
            UpdateStatus();
        };
        body.Children.Add(test);

        var subscribed = new StackPanel { Spacing = 2 };
        var rows = _home.Entities.Values.Where(e => e.Tray.Reminder is not null)
            .OrderBy(e => Presenter.FullName(e), StringComparer.Create(L.Culture, true)).ToArray();
        foreach (var e in rows)
        {
            var name = Presenter.FullName(e);
            var summary = Summary(e, new Presenter(_home));
            subscribed.Children.Add(Row(name, summary, null, () => Select(e)));
            _debugRows.Add(name + " " + summary);
        }

        if (rows.Length == 0)
        {
            subscribed.Children.Add(new TextBlock
            {
                Text = L.T("还没有提醒。选一个设备，告诉它什么时候提醒你。", "No notifications yet. Pick a device and say when to notify you."),
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.65,
            });
        }

        body.Children.Add(Label(L.T("已订阅", "Subscriptions")));
        body.Children.Add(subscribed);

        var add = new Button
        {
            Content = L.T("添加提醒", "Add notification"),
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        add.Click += (_, _) => ShowSource();
        body.Children.Add(add);

        _page.Content = new ScrollViewer { Content = body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        UpdateStatus();
    }

    /// <summary>列表行：名字（可换行）和右侧读数，整行可点，不用按钮外形。</summary>
    private static Border Row(string title, string? subtitle, string? trailing, Action click)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap });
        if (subtitle is { Length: > 0 })
        {
            text.Children.Add(new TextBlock { Text = subtitle, FontSize = 12, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });
        }

        grid.Children.Add(text);
        if (trailing is { Length: > 0 })
        {
            var value = new TextBlock
            {
                Text = trailing,
                FontSize = 12,
                Opacity = 0.6,
                MaxWidth = 150,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(value, 1);
            grid.Children.Add(value);
        }

        var row = new Border
        {
            Child = grid,
            Padding = new Thickness(10, 7, 10, 7),
            Margin = new Thickness(0, 1, 0, 1),
            CornerRadius = new CornerRadius(6),
            Background = Clear,
        };
        row.Tapped += (_, _) => click();
        row.PointerEntered += (_, _) => row.Background = Hover();
        row.PointerExited += (_, _) => row.Background = Clear;
        return row;
    }

    private static Brush Hover() =>
        Application.Current.Resources.TryGetValue("SubtleFillColorSecondaryBrush", out var value) && value is Brush brush
            ? brush
            : Clear;

    private static TextBlock Label(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Opacity = 0.7,
        TextWrapping = TextWrapping.Wrap,
    };

    // ---------------- 第二页：选来源 ----------------

    private void ShowSource()
    {
        _pageKind = Page.Source;
        _entity = null;
        _save = _remove = null;
        _current = null;

        var body = new StackPanel { Spacing = 12, Padding = new Thickness(24, 20, 24, 8) };
        var back = new Button { Content = L.T("返回", "Back"), HorizontalAlignment = HorizontalAlignment.Left };
        back.Click += (_, _) => ShowList();
        body.Children.Add(back);
        body.Children.Add(new TextBlock
        {
            Text = L.T("选择设备", "Choose a device"),
            FontSize = 22,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });

        var list = new StackPanel { Spacing = 2 };
        var search = new TextBox { PlaceholderText = L.T("搜索设备或实体", "Search devices or entities") };
        search.TextChanged += (_, _) => FillSource(list, search.Text.Trim());
        body.Children.Add(search);
        body.Children.Add(list);
        FillSource(list, "");
        _page.Content = new ScrollViewer { Content = body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        UpdateStatus();
        search.Focus(FocusState.Programmatic);
    }

    private IEnumerable<Entity> VisibleEntities() =>
        _home.Entities.Values.Where(e => e.InRegistry && !e.Hidden && (!e.Disabled || e.Tray.Reminder is not null));

    /// <summary>
    /// 三层折叠：区域 → 设备 → 实体。只有多个实体的设备才需要再展开一次；
    /// 单实体设备直接一行，免得为了看一个设备多点两下。
    /// </summary>
    private void FillSource(StackPanel into, string filter)
    {
        into.Children.Clear();
        _debugRows.Clear();
        var areas = _home.Areas.ToDictionary(a => a.Id);
        var presenter = new Presenter(_home);
        var searching = filter.Length > 0;
        var matching = VisibleEntities()
            .Select(e => (Entity: e, Area: AreaName(e.AreaId, areas)))
            .Where(x => Matches(x.Entity, x.Area, filter))
            .ToArray();

        foreach (var area in matching.GroupBy(x => x.Area).OrderBy(g => g.Key, StringComparer.Create(L.Culture, true)))
        {
            var content = new StackPanel { Spacing = 2, Margin = new Thickness(0, 2, 0, 10) };
            foreach (var device in area.GroupBy(x => x.Entity.Device)
                         .OrderBy(g => g.Key is null ? 1 : 0)
                         .ThenBy(g => g.Key?.Name ?? "", StringComparer.Create(L.Culture, true)))
            {
                var entities = device.Select(x => x.Entity)
                    .OrderBy(e => Presenter.EntityName(e) ?? "", StringComparer.Create(L.Culture, true)).ToArray();
                if (entities.Length == 1)
                {
                    var e = entities[0];
                    var name = Presenter.EntityName(e) ?? Presenter.FullName(e);
                    content.Children.Add(Row(name, device.Key?.Name, presenter.StateText(e), () => Select(e)));
                    _debugRows.Add(area.Key + " / " + name);
                    continue;
                }

                var rows = new StackPanel { Spacing = 2, Margin = new Thickness(0, 2, 0, 6) };
                foreach (var e in entities)
                {
                    var name = Presenter.EntityName(e) ?? Presenter.FullName(e);
                    rows.Children.Add(Row(name, null, presenter.StateText(e), () => Select(e)));
                    _debugRows.Add(area.Key + " / " + (device.Key?.Name ?? "") + " / " + name);
                }

                content.Children.Add(new Expander
                {
                    Header = Header(device.Key?.Name ?? L.T("没有设备", "No device"), entities.Length, 13, 0.9),
                    Content = rows,
                    IsExpanded = searching,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Margin = new Thickness(0, 1, 0, 1),
                });
            }

            _debugRows.Add("区域 " + area.Key + "（" + area.Count() + "）");
            into.Children.Add(new Expander
            {
                Header = Header(area.Key, area.Count(), 14, 1),
                Content = content,
                IsExpanded = searching,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 0, 0, 4),
            });
        }

        if (matching.Length == 0)
        {
            into.Children.Add(new TextBlock
            {
                Text = L.T("没有匹配的设备。", "No matching devices."),
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap,
            });
        }
    }

    private static StackPanel Header(string text, int count, double size, double opacity)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        header.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Opacity = opacity,
            TextWrapping = TextWrapping.Wrap,
        });
        header.Children.Add(new TextBlock
        {
            Text = count.ToString(L.Culture),
            FontSize = 12,
            Opacity = 0.5,
            VerticalAlignment = VerticalAlignment.Center,
        });
        return header;
    }

    private static string AreaName(string? id, Dictionary<string, Area> areas) =>
        id is { Length: > 0 } && areas.TryGetValue(id, out var area) ? area.Name : L.T("未分配区域", "Unassigned");

    private static bool Matches(Entity e, string area, string filter)
    {
        if (filter.Length == 0)
        {
            return true;
        }

        return new[] { Presenter.EntityName(e), Presenter.FullName(e), e.Device?.Name, area, e.EntityId }
            .Any(v => v is { Length: > 0 } && v.Contains(filter, StringComparison.CurrentCultureIgnoreCase));
    }

    // ---------------- 第三页：条件 ----------------

    /// <summary>打开某个实体的条件页（点订阅、点设备、通知入口都走这里）。</summary>
    public void Select(Entity e)
    {
        _entity = e;
        _error = null;
        _pageKind = Page.Condition;
        BuildCondition(e);
        UpdateStatus();
    }

    private void BuildCondition(Entity e)
    {
        var r = e.Tray.Reminder;
        _selected.Clear();
        if (r is not null)
        {
            foreach (var v in r.Values)
            {
                _selected.Add(v);
            }
        }

        var body = new StackPanel { Spacing = 16, Padding = new Thickness(24, 20, 24, 12) };
        var back = new Button { Content = L.T("返回", "Back"), HorizontalAlignment = HorizontalAlignment.Left };
        back.Click += (_, _) => ShowList();
        body.Children.Add(back);

        var head = new StackPanel { Spacing = 2 };
        head.Children.Add(new TextBlock
        {
            Text = Presenter.FullName(e),
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        _current = new TextBlock { Opacity = 0.65, FontSize = 13, TextWrapping = TextWrapping.Wrap };
        head.Children.Add(_current);
        body.Children.Add(head);

        _enabled = new ToggleSwitch { Header = L.T("启用", "Enabled"), IsOn = r?.Enabled ?? true };
        body.Children.Add(_enabled);

        var kinds = e.Domain == "event"
            ? new[] { new Choice("event", L.T("事件发生", "Event occurs")) }
            : new[]
            {
                new Choice("state", L.T("= 等于", "= equals")),
                new Choice("below", L.T("< 小于", "< below")),
                new Choice("above", L.T("> 大于", "> above")),
            };
        _kindValue = r?.Kind is { } saved && kinds.Any(k => k.Value == saved)
            ? saved
            : e.Domain == "event" ? "event" : ReminderRule.TryNumber(e.State, out _) ? "below" : "state";

        var kindChips = new WrapPanel();
        void RenderKinds()
        {
            kindChips.Children.Clear();
            foreach (var k in kinds)
            {
                var value = k.Value;
                kindChips.Children.Add(Chip(k.Text, value == _kindValue, () =>
                {
                    _kindValue = value;
                    RenderKinds();
                    ShowCondition();
                }));
            }
        }

        RenderKinds();
        var kindBlock = new StackPanel { Spacing = 6 };
        kindBlock.Children.Add(Label(L.T("提醒时机", "Notify when")));
        kindBlock.Children.Add(kindChips);
        body.Children.Add(kindBlock);

        var valueBlock = new StackPanel { Spacing = 6 };
        valueBlock.Children.Add(Label(L.T("值", "Values")));
        _values = new WrapPanel();
        valueBlock.Children.Add(_values);
        _other = new TextBox();
        valueBlock.Children.Add(_other);
        body.Children.Add(valueBlock);

        var thresholdBlock = new StackPanel { Spacing = 8 };
        _threshold = new NumberBox { Value = r?.Threshold ?? double.NaN };
        _reset = new NumberBox { Value = r?.ResetThreshold ?? double.NaN };
        thresholdBlock.Children.Add(_threshold);
        thresholdBlock.Children.Add(_reset);
        body.Children.Add(thresholdBlock);

        _attribute = new AutoSuggestBox { PlaceholderText = L.T("不限", "Any") };
        _attribute.ItemsSource = e.Attributes
            .Where(a => a.Key is not ("event_type" or "friendly_name" or "icon") && ReminderRule.Scalar(a.Value) is not null)
            .Select(a => a.Key).ToArray();
        _attribute.Text = r?.Attribute ?? "";
        _attribute.TextChanged += (_, _) =>
        {
            if (_attributeValue is not null)
            {
                _attributeValue.PlaceholderText = ReminderRule.Scalar(e.Attributes[_attribute.Text]) ?? "";
            }
        };
        _attributeValue = new TextBox { Text = r?.AttributeValue ?? "" };
        var attributeBlock = new StackPanel { Spacing = 6 };
        attributeBlock.Children.Add(Label(L.T("事件属性（可选）", "Event attribute (optional)")));
        attributeBlock.Children.Add(_attribute);
        attributeBlock.Children.Add(_attributeValue);
        body.Children.Add(attributeBlock);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
        _save = new Button
        {
            Content = L.T("保存", "Save"),
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
        };
        _save.Click += async (_, _) => await SaveAsync(false);
        _remove = new Button { Content = L.T("取消订阅", "Unsubscribe") };
        _remove.Click += async (_, _) => await SaveAsync(true);
        buttons.Children.Add(_save);
        buttons.Children.Add(_remove);
        body.Children.Add(buttons);

        _page.Content = new ScrollViewer { Content = body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        ShowCondition();
    }

    /// <summary>徽章：选中时用强调色底加勾，未选中是浅底细边框。</summary>
    private static Border Chip(string text, bool selected, Action click)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        if (selected)
        {
            content.Children.Add(Mdi.Icon("check", 12, R("TextOnAccentFillColorPrimaryBrush", 0xFFFFFFFF)));
        }

        content.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Foreground = selected ? R("TextOnAccentFillColorPrimaryBrush", 0xFFFFFFFF) : R("TextFillColorPrimaryBrush", 0xFF000000),
            VerticalAlignment = VerticalAlignment.Center,
        });
        var chip = new Border
        {
            Child = content,
            Padding = new Thickness(10, 5, 12, 5),
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1),
            Background = selected ? R("AccentFillColorDefaultBrush", 0xFF0067C0) : R("ControlFillColorDefaultBrush", 0x11000000),
            BorderBrush = selected ? R("AccentFillColorDefaultBrush", 0xFF0067C0) : R("ControlStrokeColorDefaultBrush", 0x22000000),
        };
        chip.Tapped += (_, _) => click();
        return chip;
    }

    private static Brush R(string key, uint fallback) =>
        Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush
            ? brush
            : new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(
                (byte)(fallback >> 24), (byte)(fallback >> 16), (byte)(fallback >> 8), (byte)fallback));

    /// <summary>切换时机时只重画值区域，徽章都是新造的。</summary>
    private void ShowCondition()
    {
        if (_entity is not { } e || _values is null)
        {
            return;
        }

        var numeric = _kindValue is "below" or "above";
        var events = _kindValue == "event";
        _values.Visibility = numeric ? Visibility.Collapsed : Visibility.Visible;
        Show(_other, _kindValue == "state");
        Show(_threshold, numeric);
        Show(_reset, numeric);
        Show(_attribute, events);
        Show(_attributeValue, events);
        _debugRows.Clear();

        if (numeric)
        {
            var unit = e.Str("unit_of_measurement") is { Length: > 0 } u ? "（" + u + "）" : "";
            _threshold!.Header = L.T("阈值", "Threshold") + unit;
            _reset!.Header = _kindValue == "below" ? L.T("恢复 ≥", "Reset ≥") : L.T("恢复 ≤", "Reset ≤");
            _debugRows.Add(_kindValue + " " + _threshold.Value);
            return;
        }

        _other!.Header = events ? null : L.T("其他状态值（可选）", "Other state value (optional)");
        _attribute!.Header = L.T("事件属性（可选）", "Event attribute (optional)");
        _attributeValue!.Header = L.T("属性值", "Attribute value");

        _values.Children.Clear();
        IEnumerable<string> options = events ? e.EventTypes : e.StateOptions;
        if (!events && e.Domain is "binary_sensor" or "switch" or "input_boolean" or "update")
        {
            options = options.Concat(["on", "off"]);
        }

        if (!events && e.HasState && e.State is not ("unknown" or "unavailable"))
        {
            options = options.Append(e.State);
        }

        var presenter = new Presenter(_home);
        foreach (var value in options.Concat(_selected).Distinct(StringComparer.Ordinal))
        {
            var v = value;
            var text = events ? presenter.AttrText(e, "event_type", v) : presenter.StateText(e, v);
            _values.Children.Add(Chip(text, _selected.Contains(v), () =>
            {
                if (!_selected.Remove(v))
                {
                    _selected.Add(v);
                }

                ShowCondition();
            }));
            _debugRows.Add(text);
        }

        if (_values.Children.Count == 0)
        {
            _values.Children.Add(new TextBlock
            {
                Text = L.T("这个实体没有可选的值，可以填在下面。", "This entity has no listed values. Enter one below."),
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap,
            });
        }
    }

    private static void Show(UIElement? control, bool visible)
    {
        if (control is not null)
        {
            control.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    // ---------------- 保存 ----------------

    private async Task SaveAsync(bool remove)
    {
        if (_saving || _entity is not { } e || _backend() is not { IsAdmin: true, Status: ConnectionStatus.Connected } backend)
        {
            return;
        }

        _error = null;
        ReminderRule? rule = null;
        if (!remove)
        {
            var kind = _kindValue;
            var numeric = kind is "below" or "above";
            var values = _selected.ToList();
            if (kind == "state" && _other?.Text.Trim() is { Length: > 0 } other)
            {
                values.Add(other);
            }

            var threshold = _threshold?.Value ?? double.NaN;
            var reset = _reset?.Value ?? double.NaN;
            if ((numeric && !double.IsFinite(threshold)) || (!numeric && values.Count == 0))
            {
                _error = L.T("请选择目标状态或填写有效阈值。", "Select target states or enter a valid threshold.");
            }

            if (numeric && !double.IsNaN(reset) && (!double.IsFinite(reset) ||
                (kind == "below" ? reset < threshold : reset > threshold)))
            {
                _error = L.T("恢复阈值必须在阈值的另一侧。", "The reset threshold must be on the recovery side of the threshold.");
            }

            var attribute = kind == "event" && _attribute?.Text.Trim() is { Length: > 0 } field ? field : null;
            if (attribute is not null && _attributeValue?.Text is not { Length: > 0 })
            {
                _error = L.T("请填写事件属性值。", "Enter the event attribute value.");
            }

            if (_error is not null)
            {
                UpdateStatus();
                return;
            }

            rule = new ReminderRule(kind, [.. values.Distinct(StringComparer.Ordinal)], numeric ? threshold : null,
                numeric && double.IsFinite(reset) ? reset : null, attribute,
                attribute is not null ? _attributeValue!.Text : null, _enabled?.IsOn ?? true);
        }

        _saving = true;
        UpdateStatus();
        try
        {
            var options = e.Tray with { Reminder = rule };
            await backend.SetTrayOptionsAsync(e.EntityId, options);
            e.Tray = options;
            _home.NotifyStructureChanged();
        }
        catch (Exception ex) when (ex is HaCommandException or IOException or TimeoutException or InvalidOperationException)
        {
            _error = L.T("保存订阅失败：", "Couldn't save subscription: ") + ex.Message;
        }
        finally
        {
            _saving = false;
        }

        // 保存完回到列表：改完就能看到它排在已订阅里，不用自己再退一级。
        if (_error is null)
        {
            ShowList();
        }
        else
        {
            UpdateStatus();
        }
    }

    private void OnStructure()
    {
        if (_pageKind == Page.List)
        {
            ShowList();
        }
        else
        {
            UpdateStatus();
        }
    }

    private void OnStates(IReadOnlyCollection<string> ids)
    {
        if (_entity is not null && ids.Contains(_entity.EntityId))
        {
            UpdateStatus();
        }
    }

    private void UpdateStatus()
    {
        var backend = _backend();
        var editable = !_saving && _entity?.InRegistry == true && backend is { IsAdmin: true, Status: ConnectionStatus.Connected };
        if (_save is not null)
        {
            _save.IsEnabled = editable;
        }

        if (_remove is not null)
        {
            _remove.IsEnabled = editable && _entity?.Tray.Reminder is not null;
        }

        if (_current is not null && _entity is { } e)
        {
            _current.Text = new Presenter(_home).StateText(e);
        }

        _status.Text = _error ?? _service.Error ?? (backend?.Status != ConnectionStatus.Connected
            ? L.T("未连接到 Home Assistant", "Not connected to Home Assistant")
            : backend.IsAdmin ? "" : L.T("保存提醒需要 Home Assistant 管理员权限。", "Saving notifications requires Home Assistant administrator access."));
    }

    /// <summary>条件的一句话表达：= 值、&lt; 阈值、&gt; 阈值、发生 事件。</summary>
    public static string Summary(Entity e, Presenter? presenter = null)
    {
        var r = e.Tray.Reminder;
        if (r is null)
        {
            return L.T("未订阅", "Not subscribed");
        }

        if (!r.Enabled)
        {
            return L.T("已暂停", "Paused");
        }

        var unit = e.Str("unit_of_measurement") is { Length: > 0 } u ? " " + u : "";
        return r.Kind switch
        {
            "below" => "< " + r.Threshold?.ToString(L.Culture) + unit,
            "above" => "> " + r.Threshold?.ToString(L.Culture) + unit,
            "event" => L.T("发生 ", "Occurs ") + string.Join("、", r.Values.Select(v => Text(e, r.Kind, v, presenter))),
            _ => "= " + string.Join("、", r.Values.Select(v => Text(e, r.Kind, v, presenter))),
        };
    }

    private static string Text(Entity e, string kind, string value, Presenter? presenter) => presenter is null
        ? value
        : kind == "event" ? presenter.AttrText(e, "event_type", value) : presenter.StateText(e, value);

    public object DebugReport => new
    {
        page = _pageKind.ToString(),
        entity = _entity?.EntityId,
        error = _status.Text,
        current = _current?.Text,
        kind = _kindValue,
        options = _selected.ToArray(),
        rows = _debugRows,
        subscriptions = _home.Entities.Values.Where(e => e.Tray.Reminder is not null)
            .Select(e => new { e.EntityId, rule = e.Tray.Reminder }),
    };
}
