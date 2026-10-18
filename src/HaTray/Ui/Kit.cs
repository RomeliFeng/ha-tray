using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace HaTray.Ui;

/// <summary>
/// 面板的控件套件，样子照 docs/design.md 第 6 节（接近 macOS 控制中心）。全部在代码里拼，按钮共用 App.xaml 的 MacButtonStyle。
/// </summary>
internal sealed class Kit(Palette p, Action<bool> menuOpenChanged)
{
    private static Style ButtonStyle => (Style)Application.Current.Resources["MacButtonStyle"];

    public Palette P => p;

    // ---------------- 文字和容器 ----------------

    public TextBlock Text(string text, double size = 13, Brush? brush = null, bool strong = false, bool trim = true) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = brush ?? p.Text1,
        FontWeight = strong ? FontWeights.SemiBold : FontWeights.Normal,
        TextTrimming = trim ? TextTrimming.CharacterEllipsis : TextTrimming.None,
        TextWrapping = trim ? TextWrapping.NoWrap : TextWrapping.Wrap,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>分组标题：11 号、半粗、次要色。</summary>
    public TextBlock Section(string text) => new()
    {
        Text = text,
        FontSize = 11,
        FontWeight = FontWeights.SemiBold,
        Foreground = p.Text2,
        Margin = new Thickness(6, 14, 6, 6),
    };

    /// <summary>卡片：半透明白，圆角 14，不画边框。</summary>
    public Border Card(UIElement child, double padding = 10, double radius = 14) => new()
    {
        Background = p.Module,
        CornerRadius = new CornerRadius(radius),
        Padding = new Thickness(padding),
        Child = child,
    };

    public Rectangle Separator() => new() { Height = 0.5, Fill = p.Separator, HorizontalAlignment = HorizontalAlignment.Stretch };

    // ---------------- 按钮 ----------------

    /// <summary>圆形图标按钮：开着时填强调色、图标白色。</summary>
    public Button CircleButton(string icon, double size, bool on, Action? click, string? tip = null)
    {
        var b = new Button
        {
            Style = ButtonStyle,
            Width = size,
            Height = size,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(size / 2),
            Background = on ? p.Accent : p.Control,
            Foreground = on ? p.OnAccent : p.Text1,
            Content = Mdi.Icon(icon, Math.Round(size * 0.53)),
            IsEnabled = click is not null,
        };
        if (click is not null)
        {
            b.Click += (_, _) => click();
        }

        if (tip is not null)
        {
            ToolTipService.SetToolTip(b, tip);
        }

        return b;
    }

    /// <summary>灰色圆角按钮（「按一下」、开 / 停 / 关这类）。</summary>
    public Button PlainButton(string text, string? icon, Action click, double height = 24)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        if (icon is not null)
        {
            content.Children.Add(Mdi.Icon(icon, 14));
        }

        if (text.Length > 0)
        {
            content.Children.Add(new TextBlock { Text = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        }

        var b = new Button
        {
            Style = ButtonStyle,
            Height = height,
            Padding = new Thickness(text.Length > 0 ? 12 : 6, 0, text.Length > 0 ? 12 : 6, 0),
            Background = p.Control,
            Foreground = p.Text1,
            Content = content,
        };
        b.Click += (_, _) => click();
        return b;
    }

    /// <summary>无底色的按钮（顶栏的星标、返回、房间名）。</summary>
    public Button GhostButton(UIElement content, Action click, string? tip = null)
    {
        var b = new Button
        {
            Style = ButtonStyle,
            Height = 28,
            Padding = new Thickness(6, 0, 6, 0),
            CornerRadius = new CornerRadius(6),
            Foreground = p.Text1,
            Content = content,
        };
        b.Click += (_, _) => click();
        if (tip is not null)
        {
            ToolTipService.SetToolTip(b, tip);
        }

        return b;
    }

    /// <summary>
    /// 弹出按钮（下拉选择）：圆角 7 的灰色按钮，右边上下双箭头，点了弹出菜单，当前选项打勾。
    /// 菜单限制在面板窗口里弹，不开独立窗口，免得抢走焦点让面板收起。
    /// </summary>
    public Button Popup(string label, IReadOnlyList<(string Value, string Text)> options, string? current, Action<string> picked, double height = 26)
    {
        var content = new Grid { ColumnSpacing = 4 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new TextBlock { Text = label, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var arrows = Mdi.Icon("unfold-more-horizontal", 14, p.Text2);
        Grid.SetColumn(arrows, 1);
        content.Children.Add(text);
        content.Children.Add(arrows);

        var menu = new MenuFlyout { Placement = FlyoutPlacementMode.Bottom, ShouldConstrainToRootBounds = true, MenuFlyoutPresenterStyle = p.MenuStyle };
        foreach (var (value, optionText) in options)
        {
            var item = new ToggleMenuFlyoutItem { Text = optionText, IsChecked = value == current };
            item.Click += (_, _) => picked(value);
            menu.Items.Add(item);
        }

        menu.Opened += (_, _) => menuOpenChanged(true);
        menu.Closed += (_, _) => menuOpenChanged(false);
        return new Button
        {
            Style = ButtonStyle,
            Height = height,
            Padding = new Thickness(9, 0, 6, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = p.Control,
            Foreground = p.Text1,
            Content = content,
            Flyout = menu,
        };
    }

    /// <summary>分段控件：灰色底槽，选中的那段凸起、半粗。</summary>
    public Border Segmented(IReadOnlyList<(string Value, string Text)> options, string? current, Action<string> picked, double height = 24)
    {
        var grid = new Grid { ColumnSpacing = 2 };
        for (var i = 0; i < options.Count; i++)
        {
            var (value, text) = options[i];
            var selected = value == current;
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var b = new Button
            {
                Style = ButtonStyle,
                Height = height,
                Padding = new Thickness(4, 0, 4, 0),
                CornerRadius = new CornerRadius(6),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Background = selected ? p.SegmentSelected : null,
                Foreground = p.Text1,
                FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal,
                Content = new TextBlock { Text = text, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis },
            };
            b.Click += (_, _) => picked(value);
            Grid.SetColumn(b, i);
            grid.Children.Add(b);
        }

        return new Border { Background = p.Control, CornerRadius = new CornerRadius(8), Padding = new Thickness(2), Child = grid };
    }

    /// <summary>开关：34 × 20 的胶囊，开着填强调色，白色圆钮。</summary>
    public Button Switch(bool on, Action toggled, bool enabled = true)
    {
        var b = new Button
        {
            Style = ButtonStyle,
            Width = 34,
            Height = 20,
            Padding = new Thickness(2),
            CornerRadius = new CornerRadius(10),
            Background = on ? p.Accent : p.Control,
            HorizontalContentAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Content = new Ellipse { Width = 16, Height = 16, Fill = p.Knob },
            IsEnabled = enabled,
            VerticalAlignment = VerticalAlignment.Center,
        };
        b.Click += (_, _) => toggled();
        return b;
    }

    /// <summary>± 调节器：两个圆形按钮夹着数值。</summary>
    public StackPanel Stepper(string value, Action? dec, Action? inc, bool big = false)
    {
        var s = new StackPanel { Orientation = Orientation.Horizontal, Spacing = big ? 12 : 6, VerticalAlignment = VerticalAlignment.Center };
        s.Children.Add(CircleButton("minus", big ? 36 : 28, false, dec));
        s.Children.Add(new TextBlock
        {
            Text = value,
            FontSize = big ? 44 : 20,
            FontWeight = big ? FontWeights.Light : FontWeights.SemiBold,
            MinWidth = big ? 120 : 44,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = p.Text1,
        });
        s.Children.Add(CircleButton("plus", big ? 36 : 28, false, inc));
        return s;
    }

    public PillSlider Pill(string icon, double min, double max, double step, double? value, Func<double, string> format) => new(p, icon, min, max, step, value, format);
}

/// <summary>
/// 控制中心那种粗胶囊滑条：高 26，浅色填充从左往右，左端图标，右端数值。按下就跳到那里，拖动时只报 Changing，
/// 松手才报 Committed（发命令在松手时）。
/// 图标和数值各画两层：底槽上的一层用浅色，填充上的一层用深色并裁到填充的宽度，所以填充拖到字下面时字也看得清
/// （只画一层时，浅色字落在浅色填充上就看不见了，2026-10-18 用户发现）。
/// </summary>
internal sealed partial class PillSlider : Grid
{
    private const double H = 26;
    private readonly Border _fill;
    private readonly Grid _onFill;
    private readonly TextBlock _label;
    private readonly TextBlock _labelOnFill;
    private readonly Microsoft.UI.Xaml.Media.RectangleGeometry _clip = new();
    private readonly Microsoft.UI.Xaml.Media.RectangleGeometry _trackClip = new();
    private readonly double _min, _max, _step;
    private readonly Func<double, string> _format;
    private double? _value;
    private bool _dragging;

    public PillSlider(Palette p, string icon, double min, double max, double step, double? value, Func<double, string> format)
    {
        _min = min;
        _max = max <= min ? min + 1 : max;
        _step = step > 0 ? step : 1;
        _format = format;
        _value = value;
        Height = H;
        CornerRadius = new CornerRadius(H / 2);
        Background = p.PillTrack;
        _fill = new Border { Background = p.PillFill, CornerRadius = new CornerRadius(H / 2), HorizontalAlignment = HorizontalAlignment.Left };
        (var onTrack, _label) = Layer(icon, p.Text2);
        (_onFill, _labelOnFill) = Layer(icon, p.PillIcon);
        _onFill.Clip = _clip;
        onTrack.Clip = _trackClip;
        Children.Add(_fill);
        Children.Add(onTrack);
        Children.Add(_onFill);
        SizeChanged += (_, _) => Update();
        PointerPressed += OnPressed;
        PointerMoved += OnMoved;
        PointerReleased += OnReleased;
        PointerCaptureLost += (_, _) => _dragging = false;
        Update();
    }

    public event Action<double>? Changing;

    public event Action<double>? Committed;

    public void SetEnabled(bool enabled)
    {
        IsHitTestVisible = enabled;
        Opacity = enabled ? 1 : 0.4;
    }

    /// <summary>一层：左端图标、右端数值，同一种颜色。</summary>
    private static (Grid Layer, TextBlock Label) Layer(string icon, Microsoft.UI.Xaml.Media.Brush brush)
    {
        var layer = new Grid { IsHitTestVisible = false };
        var glyph = Mdi.Icon(icon, 16, brush);
        glyph.HorizontalAlignment = HorizontalAlignment.Left;
        glyph.Margin = new Thickness(6, 0, 0, 0);
        var label = new TextBlock { FontSize = 11, Foreground = brush, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        layer.Children.Add(glyph);
        layer.Children.Add(label);
        return (layer, label);
    }

    private void Update()
    {
        var w = ActualWidth;
        var pc = _value is { } v ? Math.Clamp((v - _min) / (_max - _min), 0, 1) : 0;
        var fill = _value is null ? 0 : H + (Math.Max(w, H) - H) * pc;
        _fill.Width = fill;
        _clip.Rect = new Windows.Foundation.Rect(0, 0, fill, H);
        _trackClip.Rect = new Windows.Foundation.Rect(fill, 0, Math.Max(0, w - fill), H);
        _label.Text = _labelOnFill.Text = _value is { } x ? _format(x) : "";
    }

    private double At(PointerRoutedEventArgs e)
    {
        var x = e.GetCurrentPoint(this).Position.X;
        var pc = Math.Clamp((x - (H / 2)) / Math.Max(1, ActualWidth - H), 0, 1);
        var raw = _min + (pc * (_max - _min));
        return Math.Clamp(_min + (Math.Round((raw - _min) / _step) * _step), _min, _max);
    }

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        _dragging = CapturePointer(e.Pointer);
        Set(At(e));
        e.Handled = true;
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragging)
        {
            Set(At(e));
        }
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        ReleasePointerCapture(e.Pointer);
        if (_value is { } v)
        {
            Committed?.Invoke(v);
        }
    }

    private void Set(double v)
    {
        _value = v;
        Update();
        Changing?.Invoke(v);
    }
}
