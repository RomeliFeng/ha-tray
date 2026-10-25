using HaTray.Model;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;

namespace HaTray.Ui;

/// <summary>编辑主界面要做的事，由面板窗口实现（写回 HA）。见 docs/design.md 第 5 节。</summary>
internal interface IPanelEdits
{
    /// <summary>能不能编辑：令牌对应的 HA 用户是管理员。</summary>
    bool CanEdit { get; }

    /// <summary>放到主界面 / 从主界面拿掉（加、去主界面标签）。</summary>
    void Pin(Entity e, bool on);

    /// <summary>按这个顺序给实体写 options.ha_tray.order。</summary>
    void SaveOrder(IReadOnlyList<Entity> ordered);

    /// <summary>磁贴大小：half / wide / null（按类型）。</summary>
    void SetSize(Entity e, string? size);

    /// <summary>胶囊固定显示（收起时也看得到）。</summary>
    void SetPinned(Entity e, bool pinned);

    /// <summary>改 HA 里的名字或图标；null 恢复默认。</summary>
    void UpdateEntity(Entity e, string field, string? value);

    /// <summary>设区域设置里的温度或湿度传感器（field 是 temperature_entity_id / humidity_entity_id；null 清空）。</summary>
    void SetAreaSensor(Room room, string field, string? entityId);

    /// <summary>打开磁贴设置面板。</summary>
    void OpenSettings(Entity e);

    void OpenReminders(Entity e);
}

/// <summary>
/// 编辑模式（接近 macOS 编辑控制中心）：
/// - 磁贴和胶囊轻轻晃动，左上角是 −；胶囊右上角是图钉，固定的胶囊收起时也显示。
/// - 拖动：按住拖过 6 像素就拿起来（略放大、跟着鼠标走）；拖过别的磁贴时它们用动画让开；松手后滑进新位置，再写回 HA。
///   磁贴在组内换，组标题拖整组，胶囊在胶囊之间换。
/// - 点磁贴打开磁贴设置；下面列出「可以添加」的实体，点 + 放上来。
/// </summary>
internal sealed partial class PanelView
{
    private static readonly TimeSpan MoveDuration = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// 编辑时块与块之间的间距。角上的按钮伸出 7 像素，左边块的图钉和右边块的 − 会伸进同一条缝里，
    /// 间距要大于 7 + 7 才不重叠（8 像素时撞在一起，2026-10-18 用户发现）。
    /// </summary>
    private const double EditGap = 16;

    /// <summary>角上按钮的 Tag，调试通道用它找按钮、查重叠。</summary>
    public const string BadgeTag = "edit-badge";

    private readonly Dictionary<FrameworkElement, Storyboard> _jiggles = [];
    private readonly Dictionary<FrameworkElement, Storyboard> _moves = [];
    private DragState? _drag;
    private int _jiggleCount;

    /// <summary>编辑模式。</summary>
    public bool Editing { get; set; }

    /// <summary>正按着某一块（可能还没拖动）。这时整页重画会把按着的那块换掉，拖动就断了，所以要推迟。</summary>
    public bool IsDragging => _drag is not null;

    /// <summary>拖动结束（松手或被打断），面板窗口借此补上推迟的重画。</summary>
    public event Action? DragEnded;

    /// <summary>拖动日志（调试通道 dump 时带出来）：按下、拿起、松手、被打断、推迟重画，各带时间。最多留 40 条。</summary>
    public List<string> DragLog { get; } = [];

    public void LogDrag(string text)
    {
        DragLog.Add($"{DateTime.Now:HH:mm:ss.fff} {text}");
        if (DragLog.Count > 40)
        {
            DragLog.RemoveAt(0);
        }
    }

    /// <summary>每次重画前清掉上一页的拖动状态和动画。</summary>
    private void ResetEditing()
    {
        foreach (var sb in _jiggles.Values.Concat(_moves.Values))
        {
            sb.Stop();
        }

        _jiggles.Clear();
        _moves.Clear();
        _drag = null;
        _jiggleCount = 0;
    }

    private sealed record Unit(UIElement Element, bool Wide, List<Entity> Entities, Entity? Single);

    private sealed record Section(TileGroup Group, List<Unit> Units);

    /// <summary>一组可以互相换位置的东西（同一组的磁贴、所有组、所有胶囊），都放在同一个 Grid 里，换位置只改行列。</summary>
    private sealed class DragGroup
    {
        public required Grid Container { get; init; }

        public List<FrameworkElement> Frames { get; } = [];

        public List<object> Items { get; } = [];

        /// <summary>按 Frames 的顺序重排行列。</summary>
        public required Action<DragGroup> Layout { get; init; }

        /// <summary>松手后写回 HA。</summary>
        public required Action<DragGroup> Commit { get; init; }
    }

    private sealed class DragState(DragGroup group, FrameworkElement frame, UIElement handle, Point start, Point grab, Action? tap)
    {
        public DragGroup Group { get; } = group;

        public FrameworkElement Frame { get; } = frame;

        public UIElement Handle { get; } = handle;

        public Point Start { get; } = start;

        /// <summary>按下的点相对于拿起的那块左上角的位置，拖动时保持不变。</summary>
        public Point Grab { get; } = grab;

        public Action? Tap { get; } = tap;

        public bool Started { get; set; }

        public bool Moved { get; set; }
    }

    // ---------------- 胶囊 ----------------

    private Grid EditableChips(Room room)
    {
        var grid = new Grid { ColumnSpacing = EditGap, RowSpacing = EditGap };
        for (var i = 0; i < 3; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        var group = new DragGroup
        {
            Container = grid,
            Layout = g => LayoutColumns(g, 3, _ => false),
            Commit = g => edits.SaveOrder([.. g.Items.Cast<Entity>()]),
        };
        foreach (var e in room.Chips)
        {
            var caption = ChipCaption(e, room.Chips);
            var isArea = room.AreaSensors.Contains(e.EntityId);
            var frame = Frame(Live([e.EntityId], () => Chip(e, caption)), 12,
                () =>
                {
                    if (isArea)
                    {
                        edits.SetAreaSensor(room, room.AreaSensors.ElementAtOrDefault(0) == e.EntityId ? "temperature_entity_id" : "humidity_entity_id", null);
                    }

                    if (LabelId is not null && e.Labels.Contains(LabelId))
                    {
                        edits.Pin(e, false);
                    }
                },
                e.Tray.Pinned, () => edits.SetPinned(e, !e.Tray.Pinned),
                Presenter.FullName(e));
            Draggable(frame, frame, group, null);
            group.Frames.Add(frame);
            group.Items.Add(e);
            grid.Children.Add(frame);
            Debug.Add(new { kind = "chip", e.EntityId, caption, pinned = e.Tray.Pinned, editing = true });
        }

        group.Layout(group);
        return grid;
    }

    // ---------------- 组和磁贴 ----------------

    /// <summary>编辑时的全部组：每组是「组标题 + 磁贴」一块，组标题是拖整组的把手。</summary>
    private Grid EditableSections(List<Section> sections)
    {
        var container = new Grid();
        container.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var sectionGroup = new DragGroup
        {
            Container = container,
            Layout = g => LayoutColumns(g, 1, _ => true),
            Commit = g =>
            {
                sections.Clear();
                sections.AddRange(g.Items.Cast<Section>());
                edits.SaveOrder(TileOrder(sections));
            },
        };

        foreach (var s in sections)
        {
            var tiles = new Grid { ColumnSpacing = EditGap, RowSpacing = EditGap };
            tiles.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            tiles.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var tileGroup = new DragGroup
            {
                Container = tiles,
                Layout = g => LayoutColumns(g, 2, u => ((Unit)u).Wide),
                Commit = g =>
                {
                    s.Units.Clear();
                    s.Units.AddRange(g.Items.Cast<Unit>());
                    edits.SaveOrder(TileOrder(sections));
                },
            };
            foreach (var u in s.Units)
            {
                var title = u.Single is { } single ? Presenter.FullName(single) : u.Entities[0].Device?.Name ?? "";
                var frame = Frame(u.Element, 14,
                    () =>
                    {
                        foreach (var e in u.Entities)
                        {
                            edits.Pin(e, false);
                        }
                    },
                    null, null, title);
                Draggable(frame, frame, tileGroup, u.Single is { } one ? () => edits.OpenSettings(one) : null);
                tileGroup.Frames.Add(frame);
                tileGroup.Items.Add(u);
                tiles.Children.Add(frame);
            }

            tileGroup.Layout(tileGroup);

            var handle = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Padding = new Thickness(4, 12, 6, 8), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
            handle.Children.Add(Mdi.Icon("drag-horizontal-variant", 14, P.Text3));
            handle.Children.Add(new TextBlock { Text = RoomBuilder.GroupTitle(s.Group), FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = P.Text2 });
            var block = new StackPanel { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), RenderTransform = new CompositeTransform(), RenderTransformOrigin = new Point(0.5, 0.5) };
            block.Children.Add(handle);
            block.Children.Add(tiles);
            Draggable(handle, block, sectionGroup, null);
            sectionGroup.Frames.Add(block);
            sectionGroup.Items.Add(s);
            container.Children.Add(block);
        }

        sectionGroup.Layout(sectionGroup);
        return container;
    }

    /// <summary>按顺序排进 columns 列：宽的占整行（前面没排满就换行）。</summary>
    private static void LayoutColumns(DragGroup g, int columns, Func<object, bool> wide)
    {
        g.Container.RowDefinitions.Clear();
        int row = 0, col = 0;
        for (var i = 0; i < g.Frames.Count; i++)
        {
            var w = wide(g.Items[i]);
            if (w && col > 0)
            {
                row++;
                col = 0;
            }

            while (g.Container.RowDefinitions.Count <= row)
            {
                g.Container.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }

            Grid.SetRow(g.Frames[i], row);
            Grid.SetColumn(g.Frames[i], col);
            Grid.SetColumnSpan(g.Frames[i], w ? columns : 1);
            if (w || col == columns - 1)
            {
                row++;
                col = 0;
            }
            else
            {
                col++;
            }
        }
    }

    /// <summary>主界面上磁贴的完整顺序：组按显示顺序，组内按单元顺序，设备卡里的实体挨着。</summary>
    private static List<Entity> TileOrder(List<Section> sections) =>
        [.. sections.SelectMany(s => s.Units).SelectMany(u => u.Entities)];

    // ---------------- 编辑外框 ----------------

    /// <summary>
    /// 编辑外框：内容不能点；左上角 −，右上角（胶囊）图钉。− 和图钉放在圆角内容的外面，不会被圆角裁掉。
    /// </summary>
    private Grid Frame(UIElement content, double radius, Action remove, bool? pinned, Action? togglePin, string tip)
    {
        if (content is FrameworkElement fe)
        {
            fe.IsHitTestVisible = false;
        }

        var root = new Grid
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            RenderTransform = new CompositeTransform(),
            RenderTransformOrigin = new Point(0.5, 0.5),
        };
        root.Children.Add(content);
        root.Children.Add(Badge("minus", new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x63, 0x63, 0x66)), HorizontalAlignment.Left,
            L.T("从主界面拿掉", "Remove from panel"), remove));
        if (pinned is { } on && togglePin is not null)
        {
            root.Children.Add(Badge(on ? "pin" : "pin-outline", on ? P.Accent : new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x63, 0x63, 0x66)), HorizontalAlignment.Right,
                on ? L.T("取消固定", "Unpin") : L.T("固定", "Pin"), togglePin));
        }

        ToolTipService.SetToolTip(root, tip);
        Jiggle(root);
        return root;
    }

    /// <summary>角上的小圆按钮（− 或图钉），伸出内容 7 像素。</summary>
    private static Button Badge(string icon, Brush background, HorizontalAlignment side, string tip, Action click)
    {
        var b = new Button
        {
            Style = (Style)Application.Current.Resources["MacButtonStyle"],
            Width = 20,
            Height = 20,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(10),
            Background = background,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
            HorizontalAlignment = side,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = side == HorizontalAlignment.Left ? new Thickness(-7, -7, 0, 0) : new Thickness(0, -7, -7, 0),
            Content = Mdi.Icon(icon, 13),
        };
        ToolTipService.SetToolTip(b, tip);
        b.Tag = BadgeTag;
        b.Click += (_, _) => click();
        return b;
    }

    // ---------------- 拖动 ----------------

    /// <summary>handle 是按住的地方，frame 是被拿起来的那块（磁贴、胶囊就是它自己；组是整块，按住的是组标题）。</summary>
    private void Draggable(UIElement handle, FrameworkElement frame, DragGroup group, Action? tap)
    {
        handle.PointerPressed += (_, e) =>
        {
            if (_drag is { } stale)
            {
                // 上一次拖动没收尾（按着的那块被移出了界面，收不到「失去捕获」）：当它结束了，不挡这一次。
                LogDrag("上一次拖动没收尾，丢弃");
                _drag = null;
                stale.Frame.Opacity = 1;
            }

            var p = e.GetCurrentPoint(group.Container).Position;
            var o = frame.ActualOffset;
            if (handle.CapturePointer(e.Pointer))
            {
                _drag = new DragState(group, frame, handle, p, new Point(p.X - o.X, p.Y - o.Y), tap);
                LogDrag("按下");
                e.Handled = true;
            }
            else
            {
                LogDrag("按下，但没拿到指针捕获");
            }
        };
        handle.PointerMoved += (_, e) =>
        {
            if (_drag is not { } d || d.Handle != handle)
            {
                return;
            }

            var p = e.GetCurrentPoint(group.Container).Position;
            if (!d.Started)
            {
                if (Math.Abs(p.X - d.Start.X) + Math.Abs(p.Y - d.Start.Y) <= 6)
                {
                    return;
                }

                PickUp(d);
            }

            var index = group.Frames.IndexOf(frame);
            for (var i = 0; i < group.Frames.Count; i++)
            {
                if (i != index && Slot(group.Frames[i]).Contains(p))
                {
                    MoveTo(d, index, i);
                    break;
                }
            }

            Follow(d, p);
        };
        handle.PointerReleased += (_, e) =>
        {
            if (_drag is not { } d || d.Handle != handle)
            {
                return;
            }

            if (d.Started)
            {
                LogDrag(d.Moved ? "松手，换了位置" : "松手，没换位置");
                Drop(d);
            }
            else
            {
                LogDrag("轻点");
                _drag = null;
                d.Tap?.Invoke();
                DragEnded?.Invoke();
            }

            handle.ReleasePointerCapture(e.Pointer);
        };
        handle.PointerCaptureLost += (_, _) =>
        {
            if (_drag is { } d && d.Handle == handle)
            {
                LogDrag(d.Started ? "拖动中被打断：失去指针捕获" : "按下后被打断：失去指针捕获");
                if (d.Started)
                {
                    Drop(d);
                }
                else
                {
                    _drag = null;
                    DragEnded?.Invoke();
                }
            }
        };
    }

    /// <summary>在容器里的位置（不算正在播放的位移动画）。</summary>
    private static Rect Slot(FrameworkElement f) => new(f.ActualOffset.X, f.ActualOffset.Y, f.ActualSize.X, f.ActualSize.Y);

    /// <summary>拿起来：停止晃动，放在最上层，略放大。</summary>
    private void PickUp(DragState d)
    {
        d.Started = true;
        LogDrag("拿起");
        if (_jiggles.Remove(d.Frame, out var jiggle))
        {
            jiggle.Stop();
        }

        StopMove(d.Frame);
        var t = (CompositeTransform)d.Frame.RenderTransform;
        t.Rotation = 0;
        t.ScaleX = t.ScaleY = 1.04;
        Canvas.SetZIndex(d.Frame, 100);
        d.Frame.Opacity = 0.92;
    }

    /// <summary>拿起来的那块跟着鼠标：位移 = 鼠标 − 按下时的抓点 − 它现在的格子位置。</summary>
    private static void Follow(DragState d, Point p)
    {
        var t = (CompositeTransform)d.Frame.RenderTransform;
        var o = d.Frame.ActualOffset;
        t.TranslateX = p.X - d.Grab.X - o.X;
        t.TranslateY = p.Y - d.Grab.Y - o.Y;
    }

    /// <summary>
    /// 拖到别的块上：马上换位置、重排行列；其余每块从原来的位置用动画滑到新位置（先记下旧位置，排好后从差值滑回 0）。
    /// </summary>
    private void MoveTo(DragState d, int from, int to)
    {
        var g = d.Group;
        var before = g.Frames.ToDictionary(f => f, f => f.ActualOffset);
        var frame = g.Frames[from];
        var item = g.Items[from];
        g.Frames.RemoveAt(from);
        g.Items.RemoveAt(from);
        g.Frames.Insert(to, frame);
        g.Items.Insert(to, item);
        g.Layout(g);
        g.Container.UpdateLayout();
        foreach (var f in g.Frames)
        {
            if (f == d.Frame)
            {
                continue;
            }

            var old = before[f];
            var now = f.ActualOffset;
            if (Math.Abs(old.X - now.X) > 0.5 || Math.Abs(old.Y - now.Y) > 0.5)
            {
                var t = (CompositeTransform)f.RenderTransform;
                SlideToZero(f, t.TranslateX + old.X - now.X, t.TranslateY + old.Y - now.Y);
            }
        }

        d.Moved = true;
    }

    /// <summary>松手：滑进新位置、恢复大小，动画完了再写回 HA（写回会重画整页）。</summary>
    private void Drop(DragState d)
    {
        _drag = null;
        var t = (CompositeTransform)d.Frame.RenderTransform;
        t.ScaleX = t.ScaleY = 1;
        d.Frame.Opacity = 1;
        SlideToZero(d.Frame, t.TranslateX, t.TranslateY);
        var timer = d.Frame.DispatcherQueue.CreateTimer();
        timer.Interval = MoveDuration + TimeSpan.FromMilliseconds(30);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            Canvas.SetZIndex(d.Frame, 0);
            if (d.Moved)
            {
                d.Group.Commit(d.Group);
            }

            DragEnded?.Invoke();
        };
        timer.Start();
    }

    /// <summary>从 (dx, dy) 的位移用 200 毫秒滑回 0。本地值先设成 0，动画结束后停掉，值落在 0 上。</summary>
    private void SlideToZero(FrameworkElement f, double dx, double dy)
    {
        StopMove(f);
        var t = (CompositeTransform)f.RenderTransform;
        t.TranslateX = 0;
        t.TranslateY = 0;
        var sb = new Storyboard();
        foreach (var (prop, from) in new[] { ("TranslateX", dx), ("TranslateY", dy) })
        {
            var a = new DoubleAnimation { From = from, To = 0, Duration = new Duration(MoveDuration), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            Storyboard.SetTarget(a, t);
            Storyboard.SetTargetProperty(a, prop);
            sb.Children.Add(a);
        }

        sb.Completed += (_, _) =>
        {
            sb.Stop();
            _moves.Remove(f);
        };
        _moves[f] = sb;
        sb.Begin();
    }

    /// <summary>停掉正在播放的位移动画，把当时的位置留在本地值上（接着拖不会跳）。</summary>
    private void StopMove(FrameworkElement f)
    {
        if (_moves.Remove(f, out var sb))
        {
            var t = (CompositeTransform)f.RenderTransform;
            var (x, y) = (t.TranslateX, t.TranslateY);
            sb.Stop();
            t.TranslateX = x;
            t.TranslateY = y;
        }
    }

    /// <summary>编辑时轻轻晃动（±0.5°），相邻的错开半拍。</summary>
    private void Jiggle(FrameworkElement f)
    {
        var t = (CompositeTransform)f.RenderTransform;
        var animation = new DoubleAnimation
        {
            From = -0.5,
            To = 0.5,
            Duration = new Duration(TimeSpan.FromMilliseconds(140)),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            BeginTime = TimeSpan.FromMilliseconds(_jiggleCount++ % 2 * 140),
        };
        Storyboard.SetTarget(animation, t);
        Storyboard.SetTargetProperty(animation, "Rotation");
        var sb = new Storyboard();
        sb.Children.Add(animation);
        _jiggles[f] = sb;
        sb.Begin();
    }

    // ---------------- 可以添加 ----------------

    /// <summary>编辑模式下主界面最后：这个房间里还没放上来的实体，按设备分组（默认折叠），点 + 放上来。</summary>
    private void Addable(StackPanel body, Room room)
    {
        var shown = room.Chips.Select(c => c.EntityId).Concat(room.Groups.SelectMany(g => g.Items).Select(e => e.EntityId)).ToHashSet();
        var devices = room.Devices
            .Select(d => (d, Normal: d.Normal.Where(e => !shown.Contains(e.EntityId)).ToList(),
                Config: d.Config.Concat(d.Diagnostic).Where(e => !shown.Contains(e.EntityId)).ToList()))
            .Where(x => x.Normal.Count + x.Config.Count > 0)
            .ToList();
        body.Children.Add(_k.Section(L.T("可以添加", "Add to panel")));
        if (devices.Count == 0)
        {
            body.Children.Add(_k.Text(L.T("已全部添加", "All added"), 12, P.Text2, trim: false));
        }

        foreach (var (d, normal, config) in devices)
        {
            var key = "add|" + room.Id + "|" + (d.Device?.Id ?? "");
            var open = _openDevices.Contains(key);
            var content = new StackPanel { Padding = new Thickness(12, 0, 12, 6), Visibility = open ? Visibility.Visible : Visibility.Collapsed };
            void Fill()
            {
                foreach (var (list, title) in new[] { (normal, (string?)null), (config, L.T("设置和诊断", "Configuration and diagnostic")) })
                {
                    if (list.Count == 0)
                    {
                        continue;
                    }

                    if (title is not null)
                    {
                        content.Children.Add(new TextBlock { Text = title, FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = P.Text2, Margin = new Thickness(2, 10, 2, 4) });
                    }

                    foreach (var e in list)
                    {
                        content.Children.Add(AddRow(e));
                    }
                }
            }

            if (open)
            {
                Fill();
            }

            var head = new Grid { Height = 40, ColumnSpacing = 10, Padding = new Thickness(12, 0, 10, 0) };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.Children.Add(_k.Text(d.Name, 13, null, strong: true));
            var count = _k.Text((normal.Count + config.Count).ToString(L.Culture), 12, P.Text2);
            Grid.SetColumn(count, 1);
            head.Children.Add(count);
            var chev = Mdi.Icon(open ? "chevron-down" : "chevron-right", 16, P.Text3);
            Grid.SetColumn(chev, 2);
            head.Children.Add(chev);
            var headButton = new Button
            {
                Style = (Style)Application.Current.Resources["MacButtonStyle"],
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(12),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Content = head,
            };
            headButton.Click += (_, _) =>
            {
                var nowOpen = !_openDevices.Remove(key);
                if (nowOpen)
                {
                    _openDevices.Add(key);
                    if (content.Children.Count == 0)
                    {
                        Fill();
                    }
                }

                content.Visibility = nowOpen ? Visibility.Visible : Visibility.Collapsed;
                chev.Glyph = Mdi.Glyph(nowOpen ? "chevron-down" : "chevron-right");
            };
            var card = new StackPanel();
            card.Children.Add(headButton);
            card.Children.Add(content);
            body.Children.Add(new Border { Background = P.Module, CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 0, 0, 8), Child = card });
        }
    }

    private Grid AddRow(Entity e)
    {
        var g = new Grid { MinHeight = 36, ColumnSpacing = 10 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var plus = new Button
        {
            Style = (Style)Application.Current.Resources["MacButtonStyle"],
            Width = 20,
            Height = 20,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x30, 0xD1, 0x58)),
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
            Content = Mdi.Icon("plus", 14),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(plus, L.T("放到主界面", "Add to panel"));
        plus.Click += (_, _) => edits.Pin(e, true);
        g.Children.Add(plus);
        var icon = Mdi.Icon(pr.Icon(e), 15, P.Text2);
        Grid.SetColumn(icon, 1);
        g.Children.Add(icon);
        var name = _k.Text(Presenter.ShortName(e));
        Grid.SetColumn(name, 2);
        g.Children.Add(name);
        var state = _k.Text(pr.StateText(e), 12, P.Text2);
        state.MaxWidth = 130;
        Grid.SetColumn(state, 3);
        g.Children.Add(state);
        return g;
    }

    // ---------------- 磁贴设置 ----------------

    /// <summary>磁贴设置面板：大小、名字、图标。名字和图标改的是 HA 里的，HA 网页里也会跟着变。</summary>
    public UIElement SettingsSheet(Entity e, Action close)
    {
        var stack = new StackPanel { Spacing = 6 };
        var head = new Grid { ColumnSpacing = 10 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = _k.CircleButton(pr.Icon(e), 34, false, null);
        icon.IsEnabled = true;
        icon.IsHitTestVisible = false;
        head.Children.Add(icon);
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(_k.Text(L.T("磁贴设置", "Tile settings"), 13, null, strong: true));
        names.Children.Add(_k.Text(Presenter.FullName(e), 11, P.Text2));
        Grid.SetColumn(names, 1);
        head.Children.Add(names);
        var x = _k.GhostButton(Mdi.Icon("close", 16), close, L.T("关闭", "Close"));
        Grid.SetColumn(x, 2);
        head.Children.Add(x);
        stack.Children.Add(head);

        void Label(string text) => stack.Children.Add(new TextBlock { Text = text, FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = P.Text2, Margin = new Thickness(2, 10, 2, 2) });

        Label(L.T("大小", "Size"));
        var auto = L.T("自动", "Auto");
        stack.Children.Add(_k.Segmented([("", auto), ("half", L.T("半宽", "Half")), ("wide", L.T("整行", "Full"))], e.Tray.Size ?? "",
            v => edits.SetSize(e, v.Length == 0 ? null : v), 26));

        // 室温：空调报不出室温时显示哪个温度传感器，由用户选，存在 HA 区域设置的「温度传感器」里。
        if (e.Domain == "climate" && e.Num("current_temperature") is null && _room is { IsUnassigned: false } room)
        {
            Label(L.T("室温", "Room temperature"));
            var sensors = room.Devices.SelectMany(d => d.Normal.Concat(d.Config).Concat(d.Diagnostic))
                .Where(s => s.Domain == "sensor" && s.DeviceClass == "temperature")
                .Select(s => (s.EntityId, Presenter.FullName(s) + "  " + pr.StateText(s)))
                .Prepend(("", L.T("不显示", "Don't show")))
                .ToList();
            var current = room.AreaSensors.ElementAtOrDefault(0) ?? "";
            var currentText = sensors.FirstOrDefault(x => x.Item1 == current).Item2 ?? "";
            stack.Children.Add(Row(_k.Popup(currentText, sensors, current, v => edits.SetAreaSensor(room, "temperature_entity_id", v.Length == 0 ? null : v), 28)));
        }

        Label(L.T("名字", "Name"));
        stack.Children.Add(TextField(e.Name ?? "", (e.OriginalName ?? "").Trim(), v => edits.UpdateEntity(e, "name", v)));

        Label(L.T("图标", "Icon"));
        stack.Children.Add(TextField(e.Icon ?? "", pr.Icon(e), v => edits.UpdateEntity(e, "icon", v is null ? null : v.StartsWith("mdi:", StringComparison.Ordinal) ? v : "mdi:" + v), preview: true));

        return new Border
        {
            Background = P.Menu,
            BorderBrush = P.MenuBorder,
            BorderThickness = new Thickness(0.5),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(14),
            Child = stack,
        };
    }

    /// <summary>一行输入框加「应用」「恢复默认」。回车也算应用；空着应用等于恢复默认。preview 时左边实时显示图标。</summary>
    private Grid TextField(string value, string placeholder, Action<string?> apply, bool preview = false)
    {
        var g = new Grid { ColumnSpacing = 6 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var box = new TextBox { Text = value, PlaceholderText = placeholder, FontSize = 13, MinHeight = 28, Padding = new Thickness(8, 4, 8, 4), CornerRadius = new CornerRadius(7) };
        if (preview)
        {
            var glyph = Mdi.Icon(value.Length > 0 ? value : placeholder, 18, P.Text1);
            glyph.VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(glyph);
            box.TextChanged += (_, _) => glyph.Glyph = Mdi.Glyph(box.Text.Trim().Length > 0 ? box.Text.Trim() : placeholder) ?? Mdi.Glyph("help-box");
        }

        Grid.SetColumn(box, 1);
        g.Children.Add(box);
        void Apply() => apply(box.Text.Trim() is { Length: > 0 } t ? t : null);
        box.KeyDown += (_, k) =>
        {
            if (k.Key == Windows.System.VirtualKey.Enter)
            {
                Apply();
                k.Handled = true;
            }
        };
        var ok = _k.PlainButton(L.T("应用", "Apply"), null, Apply, 28);
        Grid.SetColumn(ok, 2);
        g.Children.Add(ok);
        var reset = _k.PlainButton(L.T("默认", "Default"), null, () =>
        {
            box.Text = "";
            apply(null);
        }, 28);
        Grid.SetColumn(reset, 3);
        g.Children.Add(reset);
        return g;
    }
}
