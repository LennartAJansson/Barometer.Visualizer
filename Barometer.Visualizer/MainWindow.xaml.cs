using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Barometer.Visualizer.Models;
using ScottPlot.WPF;
using SpColor = ScottPlot.Color;

namespace Barometer.Visualizer;

public partial class MainWindow : Window
{
    // --- Justera efter din uppsättning ---
    private const string NatsUrl = "nats://192.168.50.254:4222";
    private const string NatsSubject = "sensors.bme280.raw"; // Subjects är skiftlägeskänsliga – gemener, till skillnad från streamen SENSORS
    private const int MaxPoints = 20_000; // Streamen håller max 10 000 meddelanden

    private readonly ConcurrentQueue<BarometerReading> _incoming = new();
    private readonly DispatcherTimer _uiTimer;
    private readonly List<double> _times = new(); // OADate, delas av alla tre serierna
    private readonly ChartSeries _pressure;
    private readonly ChartSeries _temperature;
    private readonly ChartSeries _humidity;

    private NatsBarometerSubscriber? _subscriber;
    private TimeSpan? _window = TimeSpan.FromHours(24); // null = allt
    private long _sampleCount;
    private DateTime _lastRefresh = DateTime.MinValue;

    public MainWindow()
    {
        InitializeComponent();

        _pressure = new ChartSeries(PressurePlot, "Lufttryck", "hPa", SpColor.FromHex("#4FC3F7"), minSpan: 2.0);
        _temperature = new ChartSeries(TemperaturePlot, "Temperatur", "°C", SpColor.FromHex("#FF7043"), minSpan: 1.0);
        _humidity = new ChartSeries(HumidityPlot, "Luftfuktighet", "%", SpColor.FromHex("#81C784"), minSpan: 4.0);

        // Uppdatera grafen i klump några gånger per sekund i stället för per meddelande –
        // viktigt när historiken spelas upp (tusentals meddelanden på en gång).
        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _uiTimer.Tick += (_, _) => DrainQueue();

        Loaded += (_, _) => StartNats();
        Closing += (_, _) =>
        {
            _uiTimer.Stop();
            _subscriber?.Dispose();
        };
    }

    private void StartNats()
    {
        _subscriber = new NatsBarometerSubscriber(NatsUrl, NatsSubject);
        _subscriber.ReadingReceived += r => _incoming.Enqueue(r);
        _subscriber.StatusChanged += s => Dispatcher.InvokeAsync(() => SetStatus(Brushes.Orange, s));
        _subscriber.ConnectionError += ex => Dispatcher.InvokeAsync(() => SetStatus(Brushes.Red, $"Fel: {ex.Message}"));

        _uiTimer.Start();
        // Anslutningen blockerar – kör den utanför UI-tråden
        Task.Run(_subscriber.Start);
    }

    private void DrainQueue()
    {
        BarometerReading? latest = null;

        while (_incoming.TryDequeue(out var r))
        {
            _times.Add(r.Timestamp.ToOADate());
            _pressure.Values.Add(r.PressureHpa);
            _temperature.Values.Add(r.TemperatureC);
            _humidity.Values.Add(r.HumidityPercent);
            latest = r;
            _sampleCount++;
        }

        // Rita bara om vid nytt data, eller var 10:e sekund så att tidsfönstret glider fram.
        // (Då går det också att zooma/panorera med musen mellan uppdateringarna.)
        if (latest is null && DateTime.Now - _lastRefresh < TimeSpan.FromSeconds(10))
            return;

        TrimOldPoints();

        if (latest is not null)
        {
            PressureValue.Text = $"{latest.PressureHpa:F1} hPa";
            TemperatureValue.Text = $"{latest.TemperatureC:F1} °C";
            HumidityValue.Text = $"{latest.HumidityPercent:F1} %";
            LastUpdateText.Text = $"Senast: {latest.Timestamp:yyyy-MM-dd HH:mm:ss}";
            SampleCountText.Text = $"Mätvärden: {_sampleCount}";
            SenderText.Text = latest.Sender;

            if (StatusDot.Fill != Brushes.Red)
                StatusDot.Fill = Brushes.LimeGreen;
        }

        RefreshPlots();
    }

    private void TrimOldPoints()
    {
        var excess = _times.Count - MaxPoints;
        if (excess <= 0) return;

        _times.RemoveRange(0, excess);
        _pressure.Values.RemoveRange(0, excess);
        _temperature.Values.RemoveRange(0, excess);
        _humidity.Values.RemoveRange(0, excess);
    }

    private void RefreshPlots()
    {
        _lastRefresh = DateTime.Now;
        if (_times.Count == 0) return;

        var right = DateTime.Now.ToOADate();
        var left = _window is { } w
            ? DateTime.Now.Subtract(w).ToOADate()
            : _times[0];

        // Lite luft på höger sida så att senaste punkten inte hamnar i kanten
        var pad = (right - left) * 0.01;

        // Första index som ligger inom tidsfönstret (listan är sorterad)
        var first = _times.BinarySearch(left);
        if (first < 0) first = ~first;

        foreach (var series in new[] { _pressure, _temperature, _humidity })
        {
            series.EnsureScatter(_times);
            series.SetLimits(left, right + pad, first);
        }
    }

    private void WindowSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WindowSelector.SelectedItem is not ComboBoxItem { Tag: string tag }) return;

        var hours = int.Parse(tag);
        _window = hours > 0 ? TimeSpan.FromHours(hours) : null;

        // Anropas även under InitializeComponent, innan serierna finns
        if (_pressure is not null)
            RefreshPlots();
    }

    private void SetStatus(Brush color, string text)
    {
        StatusDot.Fill = color;
        StatusText.Text = text;
    }

    /// <summary>
    /// Ett diagram (WpfPlot) med en tidsserie och mörkt tema som matchar LSM6DSOX-visualiseraren.
    /// </summary>
    private sealed class ChartSeries
    {
        private readonly WpfPlot _plot;
        private readonly string _label;
        private readonly SpColor _color;
        private readonly double _minSpan;
        private ScottPlot.Plottables.Scatter? _scatter;

        public List<double> Values { get; } = new();

        public ChartSeries(WpfPlot plot, string title, string unit, SpColor color, double minSpan)
        {
            _plot = plot;
            _label = $"{title} ({unit})";
            _color = color;
            _minSpan = minSpan;

            var p = plot.Plot;
            p.FigureBackground.Color = SpColor.FromHex("#1E1E1E");
            p.DataBackground.Color = SpColor.FromHex("#252526");
            p.Axes.Color(SpColor.FromHex("#BBBBBB"));
            p.Grid.MajorLineColor = SpColor.FromHex("#3A3A3A");
            p.Axes.DateTimeTicksBottom();
            p.YLabel(_label);
            plot.Refresh();
        }

        /// <summary>Skapas först när det finns data – ScottPlot gillar inte tomma serier.</summary>
        public void EnsureScatter(List<double> times)
        {
            if (_scatter is not null) return;

            _scatter = _plot.Plot.Add.Scatter(times, Values);
            _scatter.Color = _color;
            _scatter.LineWidth = 2;
            _scatter.MarkerSize = 0;
        }

        public void SetLimits(double left, double right, int firstVisible)
        {
            // Y-skala efter de punkter som syns i fönstret
            double min = double.MaxValue, max = double.MinValue;
            for (var i = firstVisible; i < Values.Count; i++)
            {
                min = Math.Min(min, Values[i]);
                max = Math.Max(max, Values[i]);
            }

            if (min > max)
            {
                // Inget i fönstret – visa senaste värdet
                min = max = Values[^1];
            }

            var span = Math.Max(max - min, _minSpan);
            var mid = (max + min) / 2;
            var bottom = mid - span * 0.6;
            var top = mid + span * 0.6;

            _plot.Plot.Axes.SetLimits(left, right, bottom, top);
            _plot.Refresh();
        }
    }
}
