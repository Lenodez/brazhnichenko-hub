using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LocalAIHub.Services;

namespace LocalAIHub;

public partial class MaskEditorWindow : Window
{
    private readonly int _w;
    private readonly int _h;
    private readonly byte[] _mask; // 0 = keep original, 255 = repaint
    private readonly byte[] _overlayBgra;
    private readonly WriteableBitmap _overlayBmp;
    private readonly Stack<byte[]> _undoStack = new();

    private bool _isDrawing;
    private bool _isEraser;
    private System.Windows.Point? _lastPt;

    public string? SavedMaskPath { get; private set; }

    public MaskEditorWindow(string imagePath, string? existingMaskPath = null)
    {
        InitializeComponent();

        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.UriSource = new Uri(imagePath, UriKind.Absolute);
        bmp.EndInit();
        bmp.Freeze();

        _w = bmp.PixelWidth;
        _h = bmp.PixelHeight;
        BaseImage.Source = bmp;
        PaintSurface.Width = _w;
        PaintSurface.Height = _h;

        _mask = new byte[_w * _h];
        _overlayBgra = new byte[_w * _h * 4];
        _overlayBmp = new WriteableBitmap(_w, _h, 96, 96, PixelFormats.Bgra32, null);
        MaskOverlayImage.Source = _overlayBmp;

        if (!string.IsNullOrWhiteSpace(existingMaskPath) && File.Exists(existingMaskPath))
            TryLoadExistingMask(existingMaskPath);

        RefreshOverlay();
    }

    private void TryLoadExistingMask(string path)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();

            BitmapSource src = bmp;
            if (bmp.PixelWidth != _w || bmp.PixelHeight != _h)
                src = new TransformedBitmap(bmp, new ScaleTransform((double)_w / bmp.PixelWidth, (double)_h / bmp.PixelHeight));

            var fmt = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
            byte[] buf = new byte[_w * _h * 4];
            fmt.CopyPixels(buf, _w * 4, 0);
            for (int i = 0; i < _w * _h; i++)
                _mask[i] = buf[i * 4 + 2] > 127 ? (byte)255 : (byte)0;
        }
        catch { }
    }

    private void PushUndo()
    {
        if (_undoStack.Count >= 16)
        {
            var items = _undoStack.ToArray();
            _undoStack.Clear();
            for (int i = Math.Min(items.Length - 1, 14); i >= 0; i--)
                _undoStack.Push(items[i]);
        }
        _undoStack.Push((byte[])_mask.Clone());
    }

    private void PaintCircle(int cx, int cy, int radius)
    {
        byte val = _isEraser ? (byte)0 : (byte)255;
        int r2 = radius * radius;
        int y0 = Math.Max(0, cy - radius);
        int y1 = Math.Min(_h - 1, cy + radius);
        int x0 = Math.Max(0, cx - radius);
        int x1 = Math.Min(_w - 1, cx + radius);

        for (int y = y0; y <= y1; y++)
        {
            int dy = y - cy;
            int dy2 = dy * dy;
            int row = y * _w;
            for (int x = x0; x <= x1; x++)
            {
                int dx = x - cx;
                if (dx * dx + dy2 <= r2)
                    _mask[row + x] = val;
            }
        }
    }

    private void PaintLine(System.Windows.Point a, System.Windows.Point b)
    {
        // Scale brush proportionally to image width so slider feels natural on 512px and 2048px images alike
        double scale = Math.Max(0.5, _w / 1024.0);
        int radius = Math.Max(3, (int)Math.Round(BrushSizeSlider.Value * scale * 0.5));

        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        double dist = Math.Sqrt(dx * dx + dy * dy);
        int steps = Math.Max(1, (int)Math.Ceiling(dist / Math.Max(1.0, radius * 0.3)));

        for (int i = 0; i <= steps; i++)
        {
            double t = (double)i / steps;
            int x = (int)Math.Round(a.X + dx * t);
            int y = (int)Math.Round(a.Y + dy * t);
            PaintCircle(x, y, radius);
        }
        RefreshOverlay();
    }

    private void RefreshOverlay()
    {
        for (int i = 0; i < _mask.Length; i++)
        {
            int p = i * 4;
            if (_mask[i] > 0)
            {
                _overlayBgra[p] = 255;     // B
                _overlayBgra[p + 1] = 140; // G
                _overlayBgra[p + 2] = 91;  // R (#5B8CFF vivid blue highlight)
                _overlayBgra[p + 3] = 220; // A
            }
            else
            {
                _overlayBgra[p] = 0;
                _overlayBgra[p + 1] = 0;
                _overlayBgra[p + 2] = 0;
                _overlayBgra[p + 3] = 0;
            }
        }
        _overlayBmp.WritePixels(new Int32Rect(0, 0, _w, _h), _overlayBgra, _w * 4, 0);
    }

    private void PaintSurface_MouseDown(object sender, MouseButtonEventArgs e)
    {
        PushUndo();
        _isDrawing = true;
        PaintSurface.CaptureMouse();
        var pt = e.GetPosition(PaintSurface);
        _lastPt = pt;
        PaintLine(pt, pt);
    }

    private void PaintSurface_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isDrawing || _lastPt is null) return;
        var pt = e.GetPosition(PaintSurface);
        PaintLine(_lastPt.Value, pt);
        _lastPt = pt;
    }

    private void PaintSurface_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDrawing) return;
        _isDrawing = false;
        _lastPt = null;
        PaintSurface.ReleaseMouseCapture();
    }

    private void BrushToolBtn_Click(object sender, RoutedEventArgs e)
    {
        _isEraser = false;
        BrushToolBtn.Style = (Style)FindResource("PrimaryButton");
        EraserToolBtn.Style = (Style)FindResource("DarkButton");
    }

    private void EraserToolBtn_Click(object sender, RoutedEventArgs e)
    {
        _isEraser = true;
        BrushToolBtn.Style = (Style)FindResource("DarkButton");
        EraserToolBtn.Style = (Style)FindResource("PrimaryButton");
    }

    private void BrushSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (BrushSizeText is not null)
            BrushSizeText.Text = $"{Math.Round(e.NewValue):0} px";
    }

    private void UndoBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_undoStack.Count == 0) return;
        byte[] prev = _undoStack.Pop();
        Array.Copy(prev, _mask, _mask.Length);
        RefreshOverlay();
    }

    private void InvertBtn_Click(object sender, RoutedEventArgs e)
    {
        PushUndo();
        for (int i = 0; i < _mask.Length; i++)
            _mask[i] = (byte)(255 - _mask[i]);
        RefreshOverlay();
    }

    private void ClearBtn_Click(object sender, RoutedEventArgs e)
    {
        PushUndo();
        Array.Clear(_mask);
        RefreshOverlay();
    }

    private void ApplyBtn_Click(object sender, RoutedEventArgs e)
    {
        bool anyPainted = false;
        for (int i = 0; i < _mask.Length; i++)
            if (_mask[i] > 0) { anyPainted = true; break; }

        if (!anyPainted)
        {
            HubDialogWindow.Show("Маска пустая — закрась кистью область, которую нужно изменить.", "Редактор маски", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string dir = Path.Combine(HubConfig.ConfigDir, "inpaint_masks");
        Directory.CreateDirectory(dir);
        string outPath = Path.Combine(dir, $"mask_{DateTime.Now:yyyyMMdd_HHmmss}.png");

        var gray = BitmapSource.Create(_w, _h, 96, 96, PixelFormats.Gray8, null, _mask, _w);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(gray));
        using (var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write))
            enc.Save(fs);

        SavedMaskPath = outPath;
        DialogResult = true;
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            DialogResult = false;
        }
        else if (e.Key == Key.Z && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            e.Handled = true;
            UndoBtn_Click(this, new RoutedEventArgs());
        }
    }

    /// <summary>
    /// Composites generated image strictly inside the painted mask (with smooth feathered border)
    /// onto the original reference image — works reliably across FLUX 3, Qwen-Image, and Z-Image.
    /// </summary>
    public static string ApplyInpaintMaskComposite(string originalPath, string generatedPath, string maskPath)
    {
        var genBmp = LoadFrozen(generatedPath);
        int w = genBmp.PixelWidth;
        int h = genBmp.PixelHeight;

        byte[] genPixels = ExtractBgra32(genBmp, w, h);
        byte[] origPixels = ExtractBgra32(LoadFrozen(originalPath), w, h);
        byte[] maskBgra = ExtractBgra32(LoadFrozen(maskPath), w, h);

        // Build normalized float alpha [0..1] and apply 2-pass separable box blur (feather ~8px)
        float[] alpha = new float[w * h];
        for (int i = 0; i < w * h; i++)
            alpha[i] = maskBgra[i * 4 + 2] / 255f;

        int feather = Math.Clamp(w / 110, 4, 14);
        alpha = BoxBlurHorizontal(alpha, w, h, feather);
        alpha = BoxBlurVertical(alpha, w, h, feather);
        alpha = BoxBlurHorizontal(alpha, w, h, Math.Max(2, feather / 2));
        alpha = BoxBlurVertical(alpha, w, h, Math.Max(2, feather / 2));

        byte[] outPixels = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            float a = Math.Clamp(alpha[i], 0f, 1f);
            float inv = 1f - a;
            int p = i * 4;
            outPixels[p] = (byte)Math.Clamp(origPixels[p] * inv + genPixels[p] * a, 0, 255);
            outPixels[p + 1] = (byte)Math.Clamp(origPixels[p + 1] * inv + genPixels[p + 1] * a, 0, 255);
            outPixels[p + 2] = (byte)Math.Clamp(origPixels[p + 2] * inv + genPixels[p + 2] * a, 0, 255);
            outPixels[p + 3] = 255;
        }

        string dir = Path.GetDirectoryName(generatedPath) ?? Path.GetTempPath();
        string stem = Path.GetFileNameWithoutExtension(generatedPath);
        string outPath = Path.Combine(dir, $"{stem}_inpaint.png");

        var wb = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, outPixels, w * 4);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(wb));
        using (var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write))
            enc.Save(fs);

        return outPath;
    }

    private static BitmapImage LoadFrozen(string path)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.UriSource = new Uri(path, UriKind.Absolute);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    private static byte[] ExtractBgra32(BitmapSource src, int targetW, int targetH)
    {
        BitmapSource resized = src;
        if (src.PixelWidth != targetW || src.PixelHeight != targetH)
            resized = new TransformedBitmap(src, new ScaleTransform((double)targetW / src.PixelWidth, (double)targetH / src.PixelHeight));
        var fmt = new FormatConvertedBitmap(resized, PixelFormats.Bgra32, null, 0);
        byte[] buf = new byte[targetW * targetH * 4];
        fmt.CopyPixels(buf, targetW * 4, 0);
        return buf;
    }

    private static float[] BoxBlurHorizontal(float[] src, int w, int h, int r)
    {
        float[] dst = new float[src.Length];
        float inv = 1f / (2 * r + 1);
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            float sum = src[row] * (r + 1);
            for (int x = 1; x <= r; x++) sum += src[row + Math.Min(x, w - 1)];
            for (int x = 0; x < w; x++)
            {
                dst[row + x] = sum * inv;
                int addX = Math.Min(x + r + 1, w - 1);
                int subX = Math.Max(x - r, 0);
                sum += src[row + addX] - src[row + subX];
            }
        }
        return dst;
    }

    private static float[] BoxBlurVertical(float[] src, int w, int h, int r)
    {
        float[] dst = new float[src.Length];
        float inv = 1f / (2 * r + 1);
        for (int x = 0; x < w; x++)
        {
            float sum = src[x] * (r + 1);
            for (int y = 1; y <= r; y++) sum += src[Math.Min(y, h - 1) * w + x];
            for (int y = 0; y < h; y++)
            {
                dst[y * w + x] = sum * inv;
                int addY = Math.Min(y + r + 1, h - 1);
                int subY = Math.Max(y - r, 0);
                sum += src[addY * w + x] - src[subY * w + x];
            }
        }
        return dst;
    }
}
