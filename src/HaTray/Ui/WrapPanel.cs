using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace HaTray.Ui;

/// <summary>按可用宽度自动换行排列子元素。WinUI 没有内置 WrapPanel，徽章需要它。</summary>
internal sealed class WrapPanel : Panel
{
    public double HorizontalSpacing { get; set; } = 8;

    public double VerticalSpacing { get; set; } = 8;

    protected override Size MeasureOverride(Size availableSize)
    {
        var limit = double.IsInfinity(availableSize.Width) ? double.PositiveInfinity : availableSize.Width;
        var x = 0.0;
        var y = 0.0;
        var line = 0.0;
        var width = 0.0;
        foreach (var child in Children)
        {
            child.Measure(new Size(limit, double.PositiveInfinity));
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > limit)
            {
                width = Math.Max(width, x - HorizontalSpacing);
                x = 0;
                y += line + VerticalSpacing;
                line = 0;
            }

            x += size.Width + HorizontalSpacing;
            line = Math.Max(line, size.Height);
        }

        width = Math.Max(width, x > 0 ? x - HorizontalSpacing : 0);
        return new Size(Math.Min(width, limit), y + line);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var x = 0.0;
        var y = 0.0;
        var line = 0.0;
        foreach (var child in Children)
        {
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > finalSize.Width)
            {
                x = 0;
                y += line + VerticalSpacing;
                line = 0;
            }

            child.Arrange(new Rect(x, y, size.Width, size.Height));
            x += size.Width + HorizontalSpacing;
            line = Math.Max(line, size.Height);
        }

        return finalSize;
    }
}
