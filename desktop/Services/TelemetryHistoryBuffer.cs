namespace LocalAIHub.Services;

/// <summary>
/// Pure C# ring buffer for live telemetry sparklines (VRAM %, GPU Temp °C, RAM %, LLM tok/s).
/// </summary>
public sealed class TelemetryHistoryBuffer
{
    private readonly int _capacity;
    private readonly double[] _values;
    private int _count;
    private int _head;

    public TelemetryHistoryBuffer(int capacity = 30)
    {
        _capacity = Math.Max(2, capacity);
        _values = new double[_capacity];
    }

    public int Count => _count;
    public int Capacity => _capacity;

    public void Push(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) value = 0;
        _values[_head] = value;
        _head = (_head + 1) % _capacity;
        if (_count < _capacity) _count++;
    }

    public double[] GetOrderedValues()
    {
        var result = new double[_count];
        int start = _count < _capacity ? 0 : _head;
        for (int i = 0; i < _count; i++)
            result[i] = _values[(start + i) % _capacity];
        return result;
    }

    public double Min => _count == 0 ? 0 : GetOrderedValues().Min();
    public double Max => _count == 0 ? 0 : GetOrderedValues().Max();
    public double Latest => _count == 0 ? 0 : _values[(_head - 1 + _capacity) % _capacity];

    /// <summary>
    /// Computes normalized (X, Y) screen coordinates inside [width x height] for a WPF Polyline sparkline.
    /// </summary>
    public List<(double X, double Y)> ComputeSparklinePoints(double width, double height, double minScale = 0, double maxScale = 100, double padY = 2)
    {
        var pts = new List<(double X, double Y)>(_count);
        if (_count == 0 || width <= 1 || height <= 1) return pts;

        var ordered = GetOrderedValues();
        double range = Math.Max(0.0001, maxScale - minScale);
        double usableH = Math.Max(1, height - padY * 2);

        if (ordered.Length == 1)
        {
            double norm = Math.Clamp((ordered[0] - minScale) / range, 0.0, 1.0);
            double y = padY + (1.0 - norm) * usableH;
            pts.Add((0, y));
            pts.Add((width, y));
            return pts;
        }

        double stepX = width / Math.Max(1, _capacity - 1);
        double startX = width - stepX * (ordered.Length - 1);

        for (int i = 0; i < ordered.Length; i++)
        {
            double x = Math.Clamp(startX + i * stepX, 0, width);
            double norm = Math.Clamp((ordered[i] - minScale) / range, 0.0, 1.0);
            double y = padY + (1.0 - norm) * usableH;
            pts.Add((x, y));
        }
        return pts;
    }
}
