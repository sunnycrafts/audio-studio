using System.Windows;
using System.Windows.Input;
using AudioStudio.App.ViewModels;
using Wpf.Ui.Controls;

namespace AudioStudio.App;

public partial class MainWindow : FluentWindow
{
    private readonly MainViewModel _viewModel = new();
    private bool _isDraggingSlider;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;

        AllowDrop = true;
        Drop += OnFileDropped;
        DragOver += OnDragOver;

        Waveform.MouseLeftButtonDown += OnTimelineClick;
        Waveform.MouseWheel += OnTimelineWheel;
        Spectrogram.MouseLeftButtonDown += OnTimelineClick;
        Spectrogram.MouseWheel += OnTimelineWheel;

        Closed += (_, _) => _viewModel.Dispose();

        RegisterShortcuts();
    }

    private void RegisterShortcuts()
    {
        InputBindings.Add(new KeyBinding(_viewModel.OpenCommand, Key.O, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(_viewModel.ExportCommand, Key.E, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(_viewModel.UndoCommand, Key.Z, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(_viewModel.RedoCommand, Key.Y, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(_viewModel.AnalyzeCommand, Key.F5, ModifierKeys.None));
        InputBindings.Add(new KeyBinding(_viewModel.ZoomFitCommand, Key.D0, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(_viewModel.ZoomInCommand, Key.OemPlus, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(_viewModel.ZoomOutCommand, Key.OemMinus, ModifierKeys.Control));
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // Space toggles playback, unless the user is typing into something.
        if (e.Key == Key.Space && Keyboard.FocusedElement is not System.Windows.Controls.TextBox)
        {
            _viewModel.PlayPauseCommand.Execute(null);
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    // ------------------------------------------------------------ timeline

    private void OnTimelineClick(object sender, MouseButtonEventArgs e)
    {
        if (!_viewModel.HasProject) return;

        double x = e.GetPosition((IInputElement)sender).X;
        double seconds = sender == Waveform
            ? Waveform.PixelToTime(x)
            : Spectrogram.PixelToTime(x);

        _viewModel.SeekTo(seconds);
        e.Handled = true;
    }

    private void OnTimelineWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_viewModel.HasProject) return;

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            // Zoom around the pointer, so the moment under the cursor stays put.
            double x = e.GetPosition((IInputElement)sender).X;
            double anchor = sender == Waveform
                ? Waveform.PixelToTime(x)
                : Spectrogram.PixelToTime(x);

            _viewModel.Zoom(e.Delta > 0 ? 0.8 : 1.25, anchor);
        }
        else
        {
            _viewModel.ScrollBy(-Math.Sign(e.Delta) * _viewModel.ViewLength * 0.15);
        }

        e.Handled = true;
    }

    private void OnScrollSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Only respond to the user dragging, not to the playhead updating the value.
        if (!_isDraggingSlider) return;
        _viewModel.SeekTo(e.NewValue);
    }

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && IsDescendantOf(source, ScrollSlider))
            _isDraggingSlider = true;

        base.OnPreviewMouseDown(e);
    }

    protected override void OnPreviewMouseUp(MouseButtonEventArgs e)
    {
        _isDraggingSlider = false;
        base.OnPreviewMouseUp(e);
    }

    private static bool IsDescendantOf(DependencyObject node, DependencyObject ancestor)
    {
        while (node is not null)
        {
            if (ReferenceEquals(node, ancestor)) return true;
            node = System.Windows.Media.VisualTreeHelper.GetParent(node)
                   ?? (node as FrameworkElement)?.Parent!;
        }
        return false;
    }

    // ------------------------------------------------------------ drag/drop

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnFileDropped(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;
        await _viewModel.OpenFileAsync(files[0]);
    }
}
