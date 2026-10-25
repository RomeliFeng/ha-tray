using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using static HaTray.Native.Win32;

namespace HaTray;

/// <summary>关于：图标、名字、版本、许可证、仓库地址。和设置窗口同一种样子（Mica、同样的留白和字号）。</summary>
internal sealed class AboutWindow : Window
{
    private const string RepoUrl = "https://github.com/RomeliFeng/ha-tray";

    public AboutWindow()
    {
        SystemBackdrop = new MicaBackdrop();
        Title = L.T("关于 HA Tray", "About HA Tray");
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "HaTray.ico");
        AppWindow.SetIcon(iconPath);
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.IsMaximizable = false;
            p.IsMinimizable = false;
            p.IsResizable = false;
        }

        var info = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new Image
        {
            Source = LargestFrame(iconPath),
            Width = 64,
            Height = 64,
            Margin = new Thickness(0, 0, 0, 8),
        });
        info.Children.Add(Centered(new TextBlock { Text = "HA Tray", Style = Resource<Style>("SubtitleTextBlockStyle") }));
        info.Children.Add(Centered(Secondary(L.T("版本 ", "Version ") + Version)));
        info.Children.Add(Centered(Secondary(L.T("MIT 许可证", "MIT License"))));
        info.Children.Add(new HyperlinkButton
        {
            Content = "github.com/RomeliFeng/ha-tray",
            NavigateUri = new Uri(RepoUrl),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0),
        });

        var close = new Button { Content = L.T("关闭", "Close"), MinWidth = 96, HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Close();
        Grid.SetRow(close, 1);

        var root = new Grid { Padding = new Thickness(24, 20, 24, 20), RowSpacing = 16 };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(info);
        root.Children.Add(close);
        root.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Escape)
            {
                Close();
            }
        };
        Content = root;

        // 和设置窗口一样：按屏幕缩放定尺寸，放在鼠标所在屏幕的中间。
        GetCursorPos(out var pt);
        var monitor = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
        GetDpiForMonitor(monitor, 0, out var dpi, out _);
        var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(monitor, ref mi);
        var scale = dpi / 96.0;
        var size = new SizeInt32((int)(360 * scale), (int)(340 * scale));
        var work = mi.rcWork;
        AppWindow.MoveAndResize(new RectInt32(
            work.Left + ((work.Right - work.Left - size.Width) / 2),
            work.Top + ((work.Bottom - work.Top - size.Height) / 2),
            size.Width,
            size.Height));
    }

    /// <summary>版本号只来自 csproj 的 Version（去掉编译器加的 +commit 尾巴）。</summary>
    public static string Version { get; } =
        (typeof(AboutWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "").Split('+')[0];

    /// <summary>
    /// 取 ico 里最大的一帧（make-icons.ps1 生成的每帧都是 PNG）。直接用 ico 当图片源时只解出第一帧 16×16，放大后是糊的。
    /// 按逻辑像素解码，200% 缩放下就是 128 像素。
    /// </summary>
    private static BitmapImage LargestFrame(string icoPath)
    {
        var bytes = File.ReadAllBytes(icoPath);
        var count = BitConverter.ToUInt16(bytes, 4);
        var (best, offset, length) = (0, 0, 0);
        for (var i = 0; i < count; i++)
        {
            var entry = 6 + (16 * i);
            var size = bytes[entry] == 0 ? 256 : bytes[entry];
            if (size > best)
            {
                (best, length, offset) = (size, BitConverter.ToInt32(bytes, entry + 8), BitConverter.ToInt32(bytes, entry + 12));
            }
        }

        var image = new BitmapImage { DecodePixelType = DecodePixelType.Logical, DecodePixelWidth = 64 };
        // 不释放流：解码可能晚于 SetSource 返回。
        var stream = new MemoryStream(bytes, offset, length);
        image.SetSource(stream.AsRandomAccessStream());
        return image;
    }

    private static T Resource<T>(string key) where T : class => (T)Application.Current.Resources[key];

    private static TextBlock Secondary(string text) => new()
    {
        Text = text,
        Style = Resource<Style>("BodyTextBlockStyle"),
        Foreground = Resource<Brush>("TextFillColorSecondaryBrush"),
    };

    private static TextBlock Centered(TextBlock t)
    {
        t.HorizontalAlignment = HorizontalAlignment.Center;
        return t;
    }
}
