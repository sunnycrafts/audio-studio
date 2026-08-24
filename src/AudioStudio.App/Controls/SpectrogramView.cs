using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AudioStudio.Core.Dsp;

namespace AudioStudio.App.Controls;

/// <summary>
/// Renders the spectrogram on a logarithmic frequency axis, which is how
/// hearing is organised — an octave occupies the same vertical distance
/// everywhere, so a 50 Hz hum and its harmonics are visible instead of being
/// crushed into the bottom pixel row.
///
/// The transform is computed for the visible window only, at whatever
/// resolution the current zoom deserves, on a background thread. That keeps
/// memory flat no matter how long the recording is, and means zooming in
/// reveals real additional detail rather than magnifying a cached image.
/// </summary>
public sealed class SpectrogramView : FrameworkElement
{
    public static readonly DependencyProperty SamplesProperty = DependencyProperty.Register(
        nameof(Samples), typeof(float[]), typeof(SpectrogramView),
        new FrameworkPropertyMetadata(null, OnSourceChanged));

    public static readonly DependencyProperty SampleRateProperty = DependencyProperty.Register(
        nameof(SampleRate), typeof(int), typeof(SpectrogramView),
        new FrameworkPropertyMetadata(48000, OnSourceChanged));

    public static readonly DependencyProperty ViewStartProperty = DependencyProperty.Register(
        nameof(ViewStart), typeof(double), typeof(SpectrogramView),
        new FrameworkPropertyMetadata(0.0, OnSourceChanged));

    public static readonly DependencyProperty ViewLengthProperty = DependencyProperty.Register(
        nameof(ViewLength), typeof(double), typeof(SpectrogramView),
        new FrameworkPropertyMetadata(0.0, OnSourceChanged));

    public static readonly DependencyProperty PlayheadProperty = DependencyProperty.Register(
        nameof(Playhead), typeof(double), typeof(SpectrogramView),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Dynamic range shown, in dB below the loudest visible point.</summary>
    public static readonly DependencyProperty DynamicRangeProperty = DependencyProperty.Register(
        nameof(DynamicRange), typeof(double), typeof(SpectrogramView),
        new FrameworkPropertyMetadata(80.0, OnSourceChanged));

    /// <summary>Mono signal to analyse. Held by reference, never copied.</summary>
    public float[]? Samples
    {
        get => (float[]?)GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value);
    }

    public int SampleRate
    {
        get => (int)GetValue(SampleRateProperty);
        set => SetValue(SampleRateProperty, value);
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

    public double DynamicRange
    {
        get => (double)GetValue(DynamicRangeProperty);
        set => SetValue(DynamicRangeProperty, value);
    }

    private WriteableBitmap? _bitmap;
    private readonly DispatcherTimer _debounce;
    private CancellationTokenSource? _renderCts;
    private const double MinFrequency = 20;

    private static readonly Brush BackgroundBrush = new SolidColorBrush(Color.FromRgb(0x0B, 0x0D, 0x11));
    private static readonly Brush AxisTextBrush = new SolidColorBrush(Color.FromArgb(0xA0, 0xFF, 0xFF, 0xFF));
    private static readonly Pen AxisPen = new(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), 1);
    private static readonly Pen PlayheadPen = new(new SolidColorBrush(Color.FromRgb(0xFF, 0x60, 0x50)), 1.4);
    private static readonly Typeface AxisTypeface = new("Segoe UI");

    static SpectrogramView()
    {
        BackgroundBrush.Freeze();
        AxisTextBrush.Freeze();
        AxisPen.Freeze();
        PlayheadPen.Freeze();
    }

    public SpectrogramView()
    {
        _debounce = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(70)
        };
        _debounce.Tick += (_, _) => { _debounce.Stop(); _ = RebuildAsync(); };

        SizeChanged += (_, _) => ScheduleRebuild();
    }

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((SpectrogramView)d).ScheduleRebuild();

    private void ScheduleRebuild()
    {
        _debounce.Stop();
        _debounce.Start();
    }

    private async Task RebuildAsync()
    {
        _renderCts?.Cancel();
        var cts = new CancellationTokenSource();
        _renderCts = cts;

        var samples = Samples;
        int sampleRate = SampleRate;
        int width = (int)Math.Ceiling(ActualWidth);
        int height = (int)Math.Ceiling(ActualHeight);
        double viewStart = ViewStart, viewLength = ViewLength, range = DynamicRange;

        if (samples is null || samples.Length == 0 || width < 4 || height < 4 ||
            viewLength <= 0 || sampleRate <= 0)
        {
            _bitmap = null;
            InvalidateVisual();
            return;
        }

        try
        {
            var pixels = await Task.Run(
                () => Render(samples, sampleRate, width, height, viewStart, viewLength, range, cts.Token),
                cts.Token).ConfigureAwait(true);

            if (cts.IsCancellationRequested || pixels is null) return;

            var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
            bitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * 4, 0);
            bitmap.Freeze();

            _bitmap = bitmap;
            InvalidateVisual();
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer request; the newer one will draw.
        }
    }

    private static byte[]? Render(float[] samples, int sampleRate, int width, int height,
                                  double viewStart, double viewLength, double dynamicRange,
                                  CancellationToken ct)
    {
        // Extract the visible window, padded by one FFT frame either side so
        // the edges are analysed with real neighbouring audio rather than zeros.
        int visibleSamples = (int)(viewLength * sampleRate);
        if (visibleSamples < 16) return null;

        // One STFT column per pixel, with the transform sized to the zoom:
        // wide views get long windows (fine frequency detail, coarse timing),
        // close-up views get short ones (fine timing).
        int hop = Math.Max(1, visibleSamples / Math.Max(width, 1));
        int fftSize = NextPowerOfTwo(Math.Clamp(hop * 8, 512, 16384));

        int startSample = (int)(viewStart * sampleRate) - fftSize;
        int endSample = (int)((viewStart + viewLength) * sampleRate) + fftSize;

        startSample = Math.Clamp(startSample, 0, Math.Max(0, samples.Length - 1));
        endSample = Math.Clamp(endSample, startSample + 1, samples.Length);

        int windowLength = endSample - startSample;
        if (windowLength < fftSize)
        {
            fftSize = NextPowerOfTwo(Math.Max(256, windowLength / 2));
            if (windowLength < fftSize) return null;
        }

        var slice = new float[windowLength];
        Array.Copy(samples, startSample, slice, 0, windowLength);

        var spec = Stft.Compute(slice, sampleRate, fftSize, hop, WindowType.Hann, null, ct);
        if (spec.FrameCount == 0) return null;

        double sliceStartSeconds = (double)startSample / sampleRate;
        double nyquist = sampleRate / 2.0;

        // Precompute which bins each output row covers. Taking the maximum
        // across the range rather than sampling one bin means narrow tonal
        // lines survive the downscale instead of aliasing away.
        var rowBinLow = new int[height];
        var rowBinHigh = new int[height];

        for (int y = 0; y < height; y++)
        {
            double fracTop = 1.0 - (double)y / height;
            double fracBottom = 1.0 - (double)(y + 1) / height;

            double fTop = MinFrequency * Math.Pow(nyquist / MinFrequency, fracTop);
            double fBottom = MinFrequency * Math.Pow(nyquist / MinFrequency, fracBottom);

            int bLow = Math.Clamp(spec.FrequencyToBin(fBottom), 0, spec.BinCount - 1);
            int bHigh = Math.Clamp(spec.FrequencyToBin(fTop), 0, spec.BinCount - 1);
            if (bHigh < bLow) (bLow, bHigh) = (bHigh, bLow);

            rowBinLow[y] = bLow;
            rowBinHigh[y] = Math.Max(bLow, bHigh);
        }

        // Reference level: loudest bin anywhere in the window, so the display
        // auto-scales instead of going black on a quiet recording.
        double maxMagnitude = 1e-12;
        foreach (var frame in spec.Frames)
            for (int b = 1; b < frame.Length; b++)
                if (frame[b] > maxMagnitude) maxMagnitude = frame[b];

        double maxDb = 20 * Math.Log10(maxMagnitude);
        double floorDb = maxDb - dynamicRange;

        var pixels = new byte[width * height * 4];

        Parallel.For(0, width, new ParallelOptions { CancellationToken = ct }, x =>
        {
            double t0 = viewStart + (double)x / width * viewLength - sliceStartSeconds;
            double t1 = viewStart + (double)(x + 1) / width * viewLength - sliceStartSeconds;

            int f0 = Math.Clamp(spec.SecondsToFrame(t0), 0, spec.FrameCount - 1);
            int f1 = Math.Clamp(spec.SecondsToFrame(t1), f0, spec.FrameCount - 1);

            for (int y = 0; y < height; y++)
            {
                double peak = 0;
                int bLow = rowBinLow[y], bHigh = rowBinHigh[y];

                for (int f = f0; f <= f1; f++)
                {
                    var frame = spec.Frames[f];
                    for (int b = bLow; b <= bHigh && b < frame.Length; b++)
                        if (frame[b] > peak) peak = frame[b];
                }

                double db = peak <= 1e-12 ? -240 : 20 * Math.Log10(peak);
                double normalised = Math.Clamp((db - floorDb) / Math.Max(dynamicRange, 1), 0, 1);

                (byte r, byte g, byte bl) = Colormap(normalised);

                int index = (y * width + x) * 4;
                pixels[index + 0] = bl;
                pixels[index + 1] = g;
                pixels[index + 2] = r;
                pixels[index + 3] = 255;
            }
        });

        return pixels;
    }

    private static int NextPowerOfTwo(int value)
    {
        int result = 256;
        while (result < value) result <<= 1;
        return result;
    }

    /// <summary>
    /// Dark-to-bright perceptual ramp (near-black → indigo → magenta → orange →
    /// pale yellow). Brightness increases monotonically with level, so relative
    /// loudness stays readable even for a viewer who cannot separate the hues.
    /// </summary>
    private static (byte R, byte G, byte B) Colormap(double t)
    {
        (double r, double g, double b)[] stops =
        {
            (0.02, 0.02, 0.09),
            (0.15, 0.06, 0.32),
            (0.42, 0.09, 0.44),
            (0.70, 0.19, 0.38),
            (0.91, 0.41, 0.24),
            (0.99, 0.68, 0.24),
            (0.99, 0.93, 0.75)
        };

        double scaled = Math.Clamp(t, 0, 1) * (stops.Length - 1);
        int i = Math.Min((int)scaled, stops.Length - 2);
        double f = scaled - i;

        double r = stops[i].r + (stops[i + 1].r - stops[i].r) * f;
        double g = stops[i].g + (stops[i + 1].g - stops[i].g) * f;
        double b = stops[i].b + (stops[i + 1].b - stops[i].b) * f;

        return ((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }

    public double TimeToPixel(double seconds) =>
        ViewLength <= 0 ? 0 : (seconds - ViewStart) / ViewLength * ActualWidth;

    public double PixelToTime(double x) =>
        ActualWidth <= 0 || ViewLength <= 0 ? 0 : ViewStart + x / ActualWidth * ViewLength;

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth, height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        dc.DrawRectangle(BackgroundBrush, null, new Rect(0, 0, width, height));

        if (_bitmap is not null)
            dc.DrawImage(_bitmap, new Rect(0, 0, width, height));
        else
            DrawPlaceholder(dc, width, height);

        DrawFrequencyAxis(dc, width, height);

        double px = TimeToPixel(Playhead);
        if (px >= 0 && px <= width)
            dc.DrawLine(PlayheadPen, new Point(px, 0), new Point(px, height));
    }

    private void DrawFrequencyAxis(DrawingContext dc, double width, double height)
    {
        if (Samples is null) return;

        double nyquist = SampleRate / 2.0;
        double[] labels = { 50, 100, 200, 500, 1000, 2000, 5000, 10000, 20000 };
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        foreach (double hz in labels)
        {
            if (hz >= nyquist) continue;

            double fraction = Math.Log(hz / MinFrequency) / Math.Log(nyquist / MinFrequency);
            double y = height * (1 - fraction);
            if (y < 10 || y > height - 4) continue;

            dc.DrawLine(AxisPen, new Point(0, y), new Point(width, y));

            var text = new FormattedText(
                hz >= 1000 ? $"{hz / 1000:0.#}k" : $"{hz:0}",
                CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                AxisTypeface, 10, AxisTextBrush, pixelsPerDip);

            dc.DrawText(text, new Point(3, y - text.Height - 1));
        }
    }

    private void DrawPlaceholder(DrawingContext dc, double width, double height)
    {
        var text = new FormattedText(
            "Open a recording to see its spectrogram",
            CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            AxisTypeface, 13, new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)),
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        dc.DrawText(text, new Point((width - text.Width) / 2, (height - text.Height) / 2));
    }
}
