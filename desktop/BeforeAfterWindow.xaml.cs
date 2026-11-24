using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LocalAIHub;

public partial class BeforeAfterWindow : Window
{
    private bool _dragging;
    private double _ratio = 0.5;

    public BeforeAfterWindow(string beforePath, string afterPath, string title = "Сравнение До / После")
    {
        InitializeComponent();
        CompareTitleText.Text = title;

        var bmpBefore = LoadBmp(beforePath);
        var bmpAfter = LoadBmp(afterPath);
        BeforeImage.Source = bmpBefore;
        AfterImage.Source = bmpAfter;

        BeforeBadgeText.Text = $"ДО • {bmpBefore.PixelWidth}×{bmpBefore.PixelHeight}";
        AfterBadgeText.Text = $"ПОСЛЕ • {bmpAfter.PixelWidth}×{bmpAfter.PixelHeight}";
        CompareSubtitleText.Text = $"{Path.GetFileName(beforePath)}  →  {Path.GetFileName(afterPath)}";

        Loaded += (_, _) => UpdateSplitVisual();
    }

    private static BitmapImage LoadBmp(string path)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.UriSource = new Uri(path, UriKind.Absolute);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    private void UpdateSplitVisual()
    {
        double w = SplitHost.ActualWidth;
        double h = SplitHost.ActualHeight;
        if (w <= 1 || h <= 1) return;

        double x = Math.Clamp(w * _ratio, 0, w);
        BeforeImage.Clip = new RectangleGeometry(new Rect(0, 0, x, h));

        DividerLine.Height = h;
        Canvas.SetLeft(DividerLine, x - 1);
        Canvas.SetTop(DividerLine, 0);

        Canvas.SetLeft(DividerHandle, x - DividerHandle.Width / 2.0);
        Canvas.SetTop(DividerHandle, (h - DividerHandle.Height) / 2.0);

        SplitPctText.Text = $"{Math.Round(_ratio * 100):0}%";
    }

    private void UpdateFromPointer(System.Windows.Point pt)
    {
        if (SplitHost.ActualWidth <= 1) return;
        _ratio = Math.Clamp(pt.X / SplitHost.ActualWidth, 0.0, 1.0);
        SplitSlider.Value = _ratio * 100.0;
        UpdateSplitVisual();
    }

    private void SplitHost_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateSplitVisual();

    private void SplitHost_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragging = true;
        SplitHost.CaptureMouse();
        UpdateFromPointer(e.GetPosition(SplitHost));
    }

    private void SplitHost_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_dragging)
            UpdateFromPointer(e.GetPosition(SplitHost));
    }

    private void SplitHost_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        SplitHost.ReleaseMouseCapture();
    }

    private void SplitSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        _ratio = Math.Clamp(e.NewValue / 100.0, 0.0, 1.0);
        UpdateSplitVisual();
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }
}
