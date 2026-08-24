using System.Globalization;
using System.Windows;
using System.Windows.Media;
using AudioStudio.Core.Analysis;
using AudioStudio.Core.Audio;

namespace AudioStudio.App.Controls;

/// <summary>A highlighted region on the timeline, drawn behind the waveform.</summary>
public sealed record TimelineMarker(
    TimeRange Range, FindingSeverity Severity, string Label, bool IsSelected = false);

/// <summary>
/// Draws the waveform from the peak pyramid, plus the playhead, time ruler and
/// any issue markers the analysis produced. Rendering cost depends on the
/// control's width, not on the length of the recording.
/// </summary>
public sealed class WaveformView : FrameworkElement
{
    public static readonly DependencyProperty PeaksProperty = DependencyProperty.Register(
        nameof(Peaks), typeof(PeakData), typeof(WaveformView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ViewStartProperty = DependencyProperty.Register(
        nameof(ViewStart), typeof(double), typeof(WaveformView),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ViewLengthProperty = DependencyProperty.Register(
        nameof(ViewLength), typeof(double), typeof(WaveformView),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PlayheadProperty = DependencyProperty.Register(
        nameof(Playhead), typeof(double), typeof(WaveformView),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MarkersProperty = DependencyProperty.Register(
        nameof(Markers), typeof(IReadOnlyList<TimelineMarker>), typeof(WaveformView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty WaveBrushProperty = DependencyProperty.Register(
        nameof(WaveBrush), typeof(Brush), typeof(WaveformView),
        new FrameworkPropertyMetadata(Brushes.SteelBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Peaks of the original import, drawn faintly behind the edited result.</summary>
    public static readonly DependencyProperty ComparisonPeaksProperty = DependencyProperty.Register(
        nameof(ComparisonPeaks), typeof(PeakData), typeof(WaveformView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public PeakData? Peaks
    {
        get => (PeakData?)GetValue(PeaksProperty);
        set => SetValue(PeaksProperty, value);
    }

    public PeakData? ComparisonPeaks
    {
        get => (PeakData?)GetValue(ComparisonPeaksProperty);
        set => SetValue(ComparisonPeaksProperty, value);
    }

    public double ViewStart
    {
        get => (double)GetValue(ViewStartProperty);
        set => SetValue(ViewStartProperty, value);
    }

    public double ViewLength
    {
        get => (double)GetValue(ViewLengthProperty);
        set => SetValue(ViewLengthProperty, value);
    }

    public double Playhead
    {
        get => (double)GetValue(PlayheadProperty);
        set => SetValue(PlayheadProperty, value);
    }

    public IReadOnlyList<TimelineMarker>? Markers
    {
        get => (IReadOnlyList<TimelineMarker>?)GetValue(MarkersProperty);
        set => SetValue(MarkersProperty, value);
    }

    public Brush WaveBrush
    {
        get => (Brush)GetValue(WaveBrushProperty);
        set => SetValue(WaveBrushProperty, value);
    }

    private const double RulerHeight = 22;

    private static readonly Brush BackgroundBrush = new SolidColorBrush(Color.FromRgb(0x14, 0x17, 0x1C));
    private static readonly Brush GridBrush = new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF));
    private static readonly Brush CentreBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
    private static readonly Brush RulerTextBrush = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF));
    private static readonly Brush ComparisonBrush = new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF));
    private static readonly Pen PlayheadPen = new(new SolidColorBrush(Color.FromRgb(0xFF, 0x60, 0x50)), 1.4);
    private static readonly Pen GridPen = new(GridBrush, 1);
    private static readonly Typeface RulerTypeface = new("Segoe UI");

    static WaveformView()
    {
        BackgroundBrush.Freeze();
        GridBrush.Freeze();
        CentreBrush.Freeze();
        RulerTextBrush.Freeze();
        ComparisonBrush.Freeze();
        PlayheadPen.Freeze();
        GridPen.Freeze();
    }

    /// <summary>Converts a horizontal pixel position to a time in seconds.</summary>
    public double PixelToTime(double x)
    {
        if (ActualWidth <= 0 || ViewLength <= 0) return 0;
        return ViewStart + x / ActualWidth * ViewLength;
    }

    public double TimeToPixel(double seconds)
    {
        if (ViewLength <= 0) return 0;
        return (seconds - ViewStart) / ViewLength * ActualWidth;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth, height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        dc.DrawRectangle(BackgroundBrush, null, new Rect(0, 0, width, height));

        double waveTop = RulerHeight;
        double waveHeight = height - RulerHeight;
        if (waveHeight <= 0) return;

        var peaks = Peaks;
        if (peaks is null || ViewLength <= 0)
        {
            DrawPlaceholder(dc, width, height);
            return;
        }

        DrawMarkers(dc, width, waveTop, waveHeight);
        DrawRuler(dc, width);

        int channels = peaks.ChannelCount;
        double channelHeight = waveHeight / channels;

        if (ComparisonPeaks is not null)
            DrawChannels(dc, ComparisonPeaks, waveTop, channelHeight, width, ComparisonBrush, 0.6);

        DrawChannels(dc, peaks, waveTop, channelHeight, width, WaveBrush, 1.0);

        // Channel separators.
        for (int c = 1; c < channels; c++)
        {
            double y = waveTop + c * channelHeight;
            dc.DrawLine(GridPen, new Point(0, y), new Point(width, y));
        }

        // Playhead.
        double px = TimeToPixel(Playhead);
        if (px >= 0 && px <= width)
            dc.DrawLine(PlayheadPen, new Point(px, 0), new Point(px, height));
    }

    private void DrawChannels(DrawingContext dc, PeakData peaks, double top,
                              double channelHeight, double width, Brush brush, double opacity)
    {
        double viewStartSample = ViewStart * peaks.SampleRate;
        double viewLengthSamples = ViewLength * peaks.SampleRate;
        if (viewLengthSamples <= 0) return;

        var level = peaks.SelectLevel((int)viewLengthSamples, width);
        int pixels = (int)Math.Ceiling(width);

        for (int c = 0; c < peaks.ChannelCount && c < level.Min.Length; c++)
        {
            double centre = top + c * channelHeight + channelHeight / 2;
            double halfHeight = channelHeight / 2 - 3;

            dc.DrawLine(new Pen(CentreBrush, 1), new Point(0, centre), new Point(width, centre));

            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                var mins = level.Min[c];
                var maxs = level.Max[c];
                bool started = false;

                for (int x = 0; x < pixels; x++)
                {
                    double sampleStart = viewStartSample + (double)x / width * viewLengthSamples;
                    double sampleEnd = viewStartSample + (double)(x + 1) / width * viewLengthSamples;

                    int p0 = (int)(sampleStart / level.SamplesPerPeak);
                    int p1 = (int)Math.Ceiling(sampleEnd / level.SamplesPerPeak);

                    p0 = Math.Clamp(p0, 0, mins.Length - 1);
                    p1 = Math.Clamp(p1, p0 + 1, mins.Length);

                    float lo = float.MaxValue, hi = float.MinValue;
                    for (int p = p0; p < p1; p++)
                    {
                        if (mins[p] < lo) lo = mins[p];
                        if (maxs[p] > hi) hi = maxs[p];
                    }
                    if (lo > hi) { lo = 0; hi = 0; }

                    double yTop = centre - hi * halfHeight;
                    double yBottom = centre - lo * halfHeight;

                    // Always draw at least a hairline, so quiet passages still
                    // read as a continuous line rather than disappearing.
                    if (yBottom - yTop < 1) { yTop -= 0.5; yBottom += 0.5; }

                    if (!started) { ctx.BeginFigure(new Point(x + 0.5, yTop), false, false); started = true; }
                    else ctx.LineTo(new Point(x + 0.5, yTop), true, false);

                    ctx.LineTo(new Point(x + 0.5, yBottom), true, false);
                }
            }
            geometry.Freeze();

            var pen = new Pen(brush, 1) { Thickness = 1 };
            if (opacity < 1.0) pen = new Pen(brush, 1);
            pen.Freeze();

            dc.PushOpacity(opacity);
            dc.DrawGeometry(null, pen, geometry);
            dc.Pop();
        }
    }

    private void DrawMarkers(DrawingContext dc, double width, double top, double height)
    {
        var markers = Markers;
        if (markers is null || markers.Count == 0) return;

        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // Unselected first, so the selected marker and its label sit on top.
        foreach (var marker in markers.OrderBy(m => m.IsSelected))
        {
            double x0 = TimeToPixel(marker.Range.Start.TotalSeconds);
            double x1 = TimeToPixel(marker.Range.End.TotalSeconds);

            if (x1 < 0 || x0 > width) continue;

            x0 = Math.Max(0, x0);
            x1 = Math.Min(width, x1);
            if (x1 - x0 < 2) x1 = x0 + 2;    // keep very short events visible

            var brush = SeverityBrush(marker.Severity, marker.IsSelected);
            dc.DrawRectangle(brush, null, new Rect(x0, top, x1 - x0, height));

            // Edge posts make a narrow marker readable when it is only a couple
            // of pixels wide.
            var edge = SeverityEdgePen(marker.Severity, marker.IsSelected);
            dc.DrawLine(edge, new Point(x0, top), new Point(x0, top + height));
            if (x1 - x0 > 4)
                dc.DrawLine(edge, new Point(x1, top), new Point(x1, top + height));

            // Label it when there is room, so the timeline names its problems
            // instead of just colouring them.
            if (x1 - x0 < 46) continue;

            var text = new FormattedText(
                marker.Label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                RulerTypeface, 10.5,
                marker.IsSelected ? SelectedLabelBrush : MarkerLabelBrush, pixelsPerDip)
            {
                MaxTextWidth = Math.Max(10, x1 - x0 - 8),
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis
            };

            dc.DrawText(text, new Point(x0 + 4, top + 3));
        }
    }

    // Declared before anything that calls Frozen(). Static field initialisers
    // run in declaration order, so a cache declared further down would still be
    // null when the fields above it were being built.
    private static readonly Dictionary<Color, Brush> BrushCache = new();

    private static Brush Frozen(Color color)
    {
        lock (BrushCache)
        {
            if (BrushCache.TryGetValue(color, out var cached)) return cached;
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            BrushCache[color] = brush;
            return brush;
        }
    }

    private static readonly Brush MarkerLabelBrush = Frozen(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));
    private static readonly Brush SelectedLabelBrush = Frozen(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));

    private static (byte R, byte G, byte B) SeverityColor(FindingSeverity severity) => severity switch
    {
        FindingSeverity.Critical => (0xFF, 0x45, 0x45),
        FindingSeverity.Severe => (0xFF, 0x7A, 0x2F),
        FindingSeverity.Moderate => (0xFF, 0xC4, 0x3D),
        FindingSeverity.Minor => (0x6F, 0xC8, 0xFF),
        _ => (0x9E, 0x9E, 0x9E)
    };

    private static Brush SeverityBrush(FindingSeverity severity, bool selected)
    {
        var (r, g, b) = SeverityColor(severity);
        // The selected finding is held at a much higher opacity so it reads
        // instantly among the others rather than merging into them.
        byte alpha = selected ? (byte)0x66 : severity switch
        {
            FindingSeverity.Critical => (byte)0x30,
            FindingSeverity.Severe => (byte)0x2A,
            FindingSeverity.Moderate => (byte)0x24,
            _ => (byte)0x1E
        };
        return Frozen(Color.FromArgb(alpha, r, g, b));
    }

    private static readonly Dictionary<(FindingSeverity, bool), Pen> EdgePenCache = new();

    private static Pen SeverityEdgePen(FindingSeverity severity, bool selected)
    {
        lock (EdgePenCache)
        {
            if (EdgePenCache.TryGetValue((severity, selected), out var cached)) return cached;

            var (r, g, b) = SeverityColor(severity);
            var pen = new Pen(Frozen(Color.FromArgb(selected ? (byte)0xFF : (byte)0xAA, r, g, b)),
                              selected ? 1.6 : 1.0);
            pen.Freeze();
            EdgePenCache[(severity, selected)] = pen;
            return pen;
        }
    }

    private void DrawRuler(DrawingContext dc, double width)
    {
        if (ViewLength <= 0) return;

        double targetSpacing = 90;                            // pixels between labels
        double secondsPerLabel = ViewLength * targetSpacing / Math.Max(width, 1);
        double step = NiceStep(secondsPerLabel);

        double first = Math.Ceiling(ViewStart / step) * step;

        for (double t = first; t < ViewStart + ViewLength; t += step)
        {
            double x = TimeToPixel(t);
            if (x < 0 || x > width) continue;

            dc.DrawLine(GridPen, new Point(x, 0), new Point(x, RulerHeight - 6));
            dc.DrawLine(GridPen, new Point(x, RulerHeight), new Point(x, ActualHeight));

            var text = new FormattedText(
                FormatTime(t, step), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                RulerTypeface, 11, RulerTextBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

            dc.DrawText(text, new Point(x + 4, 3));
        }
    }

    private static double NiceStep(double raw)
    {
        double[] steps = { 0.01, 0.02, 0.05, 0.1, 0.25, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600 };
        foreach (double s in steps)
            if (s >= raw) return s;
        return 3600;
    }

    private static string FormatTime(double seconds, double step)
    {
        var t = TimeSpan.FromSeconds(seconds);
        if (step < 1) return $"{(int)t.TotalMinutes}:{t.Seconds:00}.{t.Milliseconds / 10:00}";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}";
        return $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    }

    private void DrawPlaceholder(DrawingContext dc, double width, double height)
    {
        var text = new FormattedText(
            "Open a recording to begin",
            CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            RulerTypeface, 14, new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)),
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        dc.DrawText(text, new Point((width - text.Width) / 2, (height - text.Height) / 2));
    }
}
