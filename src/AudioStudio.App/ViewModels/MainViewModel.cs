using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using AudioStudio.App.Controls;
using AudioStudio.App.Playback;
using AudioStudio.Core.Analysis;
using AudioStudio.Core.Audio;
using AudioStudio.Core.Diagnostics;
using AudioStudio.Core.Graph;
using AudioStudio.Core.Io;
using AudioStudio.Core.Project;
using Microsoft.Win32;

namespace AudioStudio.App.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly AudioPlayer _player = new();
    private readonly OperationLog _log = new();
    private readonly AnalysisEngine _engine;
    private readonly DispatcherTimer _playbackTimer;
    private CancellationTokenSource? _workCts;

    public MainViewModel()
    {
        _engine = new AnalysisEngine(_log);

        _log.EntryWritten += (_, entry) => Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            LogEntries.Insert(0, entry);
            while (LogEntries.Count > 400) LogEntries.RemoveAt(LogEntries.Count - 1);
        });

        _playbackTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(33)
        };
        _playbackTimer.Tick += (_, _) => OnPlaybackTick();
        _playbackTimer.Start();

        _player.PlaybackStopped += (_, _) => Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            OnPropertyChanged(nameof(IsPlaying));
            OnPropertyChanged(nameof(PlayPauseGlyph));
        });

        OpenCommand = new RelayCommand(async () => await OpenAsync(), () => !IsBusy);
        AnalyzeCommand = new RelayCommand(async () => await AnalyzeAsync(), () => Project is not null && !IsBusy);
        ApplyPlanCommand = new RelayCommand(
            async () => await ApplyPlanAsync(),
            () => !IsBusy && PlanSteps.Any(s => s.State == TreatmentStepState.Proposed));
        PrimaryActionCommand = new RelayCommand(async () => await RunPrimaryActionAsync(), () => !IsBusy);
        ExportCommand = new RelayCommand(async () => await ExportAsync(), () => Project is not null && !IsBusy);
        PlayPauseCommand = new RelayCommand(TogglePlayback, () => Project is not null);
        StopCommand = new RelayCommand(StopPlayback, () => Project is not null);
        UndoCommand = new RelayCommand(Undo, () => Project?.History.CanUndo == true && !IsBusy);
        RedoCommand = new RelayCommand(Redo, () => Project?.History.CanRedo == true && !IsBusy);
        ZoomInCommand = new RelayCommand(() => Zoom(0.5), () => Project is not null);
        ZoomOutCommand = new RelayCommand(() => Zoom(2.0), () => Project is not null);
        ZoomFitCommand = new RelayCommand(ZoomToFit, () => Project is not null);
        ZoomToFindingCommand = new RelayCommand(ZoomToSelectedFinding,
            () => SelectedFinding?.Occurrences.Count > 0);
        ApplySuggestionCommand = new RelayCommand(p => ApplySuggestion(p as RepairSuggestion),
                                                  p => p is RepairSuggestion && !IsBusy);
        RemoveNodeCommand = new RelayCommand(p => RemoveNode(p as IProcessingNode),
                                             p => p is IProcessingNode && !IsBusy);
        ToggleNodeCommand = new RelayCommand(p => ToggleNode(p as IProcessingNode),
                                             p => p is IProcessingNode && !IsBusy);
        RevertAllCommand = new RelayCommand(RevertAll, () => Project?.HasEdits == true && !IsBusy);
        SaveReportCommand = new RelayCommand(async () => await SaveReportAsync(),
                                             () => Project?.Report is not null && !IsBusy);

        CheckEnvironment();
    }

    // ---------------------------------------------------------------- state

    private AudioProject? _project;
    public AudioProject? Project
    {
        get => _project;
        private set
        {
            if (SetProperty(ref _project, value))
            {
                OnPropertyChanged(nameof(HasProject));
                OnPropertyChanged(nameof(FileNameLabel));
                OnPropertyChanged(nameof(SourceSummary));
                RaiseAllCanExecute();
            }
        }
    }

    public bool HasProject => Project is not null;

    public string FileNameLabel => Project?.FileName ?? "No recording open";

    public string SourceSummary => Project is null
        ? "Open a WAV, FLAC, MP3 or any other audio file to begin."
        : $"{Project.SourceInfo.DisplaySummary} · {TimeRange.Format(Project.OriginalBuffer.Duration)}";

    public ObservableCollection<Finding> Findings { get; } = new();
    public ObservableCollection<RepairSuggestion> Suggestions { get; } = new();
    public ObservableCollection<TreatmentStep> PlanSteps { get; } = new();
    public ObservableCollection<VerificationMetric> LastVerification { get; } = new();
    public ObservableCollection<IProcessingNode> ChainNodes { get; } = new();
    public ObservableCollection<HistoryEntry> HistoryEntries { get; } = new();
    public ObservableCollection<LogEntry> LogEntries { get; } = new();

    private Finding? _selectedFinding;
    public Finding? SelectedFinding
    {
        get => _selectedFinding;
        set
        {
            if (!SetProperty(ref _selectedFinding, value)) return;

            Suggestions.Clear();
            if (value is not null && Project?.Report is not null)
            {
                foreach (var s in RepairSuggestions.For(value, Project.Report))
                    Suggestions.Add(s);
            }

            OnPropertyChanged(nameof(HasSelectedFinding));
            OnPropertyChanged(nameof(HasSuggestions));
            OnPropertyChanged(nameof(NoSuggestionsReason));
            UpdateMarkers();
            ZoomToFindingCommand.RaiseCanExecuteChanged();

            // Selecting a finding takes you to it. Being told a problem is at
            // 4:12 and then having to go and find it yourself is exactly the
            // busywork this application exists to remove.
            if (value is not null && value.Occurrences.Count > 0) ZoomToSelectedFinding();
        }
    }

    public bool HasSelectedFinding => SelectedFinding is not null;
    public bool HasSuggestions => Suggestions.Count > 0;

    public string NoSuggestionsReason =>
        SelectedFinding is null ? ""
        : Suggestions.Count > 0 ? ""
        : SelectedFinding.SuggestedStrategy is not null
            ? "The repair for this finding needs the AI restoration stage, which arrives in a later phase. The recommended strategy is described above."
            : "Nothing to repair — this finding is informational.";

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsIdle));
                RaiseAllCanExecute();
            }
        }
    }

    public bool IsIdle => !IsBusy;

    private string _statusText = "Ready";
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    private double _progress;
    public double Progress
    {
        get => _progress;
        private set => SetProperty(ref _progress, value);
    }

    private string? _environmentWarning;
    public string? EnvironmentWarning
    {
        get => _environmentWarning;
        private set
        {
            if (SetProperty(ref _environmentWarning, value))
                OnPropertyChanged(nameof(HasEnvironmentWarning));
        }
    }

    public bool HasEnvironmentWarning => !string.IsNullOrEmpty(EnvironmentWarning);

    // ------------------------------------------------------------- analysis

    public bool HasReport => Project?.Report is not null;
    public int HealthScore => Project?.Report?.HealthScore ?? 0;
    public string HealthLabel => Project?.Report?.HealthLabel ?? "";

    public string ReportSummary => Project?.Report?.Narrative
        ?? "Open a recording and I will inspect it before changing anything.";

    /// <summary>Verdict line under the health figure.</summary>
    public string HealthVerdict => Project?.Report is null
        ? ""
        : Project.Report.HealthScore switch
        {
            >= 90 => "No restoration needed",
            >= 75 => "Minor restoration suggested",
            >= 55 => "Restoration recommended",
            >= 35 => "Restoration strongly recommended",
            _ => "Extensive restoration needed"
        };

    /// <summary>Mean detector confidence across the problems found.</summary>
    public string DiagnosisConfidence
    {
        get
        {
            var problems = Project?.Report?.Findings
                .Where(f => f.Severity >= FindingSeverity.Minor).ToList();
            if (problems is null || problems.Count == 0) return "—";
            return $"{problems.Average(f => f.Confidence) * 100:0}%";
        }
    }

    /// <summary>Ten-block meter used instead of a bare number.</summary>
    public string HealthMeter
    {
        get
        {
            if (Project?.Report is null) return "";
            int filled = (int)Math.Round(HealthScore / 10.0);
            return new string('■', Math.Clamp(filled, 0, 10)) +
                   new string('□', Math.Clamp(10 - filled, 0, 10));
        }
    }

    public bool HasPlan => PlanSteps.Count > 0;

    public string PlanHeadline => PlanSteps.Count == 0
        ? "No treatment required"
        : $"{PlanSteps.Count} step{(PlanSteps.Count == 1 ? "" : "s")} · " +
          $"{PlanSteps.Count(s => s.State is TreatmentStepState.Applied or TreatmentStepState.Verified)} complete";

    /// <summary>Drives the primary button's label through the workflow.</summary>
    public string PrimaryActionLabel =>
        !HasProject ? "Open recording"
        : !HasReport ? "Inspect recording"
        : HasPlan && PlanSteps.Any(s => s.State == TreatmentStepState.Proposed) ? "Repair recording"
        : "Export result";

    public string AnalysisMetaLabel => Project?.Report is null
        ? ""
        : $"{Project.Report.DetectorRuns.Count} detectors · " +
          $"{Project.Report.ElapsedTime.TotalSeconds:0.0} s · " +
          $"{Project.Report.CompletedAt:HH:mm:ss}";

    // ------------------------------------------------------------- timeline

    private double _viewStart;
    public double ViewStart
    {
        get => _viewStart;
        set => SetProperty(ref _viewStart, Math.Max(0, value));
    }

    private double _viewLength;
    public double ViewLength
    {
        get => _viewLength;
        set => SetProperty(ref _viewLength, Math.Max(0.001, value));
    }

    public double TotalDuration => Project?.OriginalBuffer.Duration.TotalSeconds ?? 0;

    private double _playhead;
    public double Playhead
    {
        get => _playhead;
        set
        {
            if (SetProperty(ref _playhead, value))
                OnPropertyChanged(nameof(PositionLabel));
        }
    }

    public string PositionLabel =>
        Project is null ? "0:00.0 / 0:00.0"
        : $"{TimeRange.Format(TimeSpan.FromSeconds(Playhead))} / " +
          $"{TimeRange.Format(TimeSpan.FromSeconds(TotalDuration))}";

    public bool IsPlaying => _player.IsPlaying;
    public string PlayPauseGlyph => IsPlaying ? "" : "";   // pause / play

    private IReadOnlyList<TimelineMarker> _markers = Array.Empty<TimelineMarker>();
    public IReadOnlyList<TimelineMarker> Markers
    {
        get => _markers;
        private set => SetProperty(ref _markers, value);
    }

    private float[]? _displaySamples;
    /// <summary>Mono version of the currently audible audio, for the spectrogram.</summary>
    public float[]? DisplaySamples
    {
        get => _displaySamples;
        private set => SetProperty(ref _displaySamples, value);
    }

    public int DisplaySampleRate => Project?.OriginalBuffer.SampleRate ?? AudioFormat.SampleRate;

    private PeakData? _currentPeaks;
    public PeakData? CurrentPeaks
    {
        get => _currentPeaks;
        private set => SetProperty(ref _currentPeaks, value);
    }

    private PeakData? _comparisonPeaks;
    /// <summary>Original waveform, ghosted behind the edited one when edits exist.</summary>
    public PeakData? ComparisonPeaks
    {
        get => _comparisonPeaks;
        private set => SetProperty(ref _comparisonPeaks, value);
    }

    // ------------------------------------------------------------- commands

    public RelayCommand OpenCommand { get; }
    public RelayCommand AnalyzeCommand { get; }
    public RelayCommand ApplyPlanCommand { get; }

    /// <summary>
    /// The single prominent action, which advances through the workflow:
    /// open → inspect → repair → export. Fluent design wants one obvious next
    /// step, and at any moment there is exactly one.
    /// </summary>
    public RelayCommand PrimaryActionCommand { get; }

    private async Task RunPrimaryActionAsync()
    {
        if (!HasProject) { await OpenAsync(); return; }
        if (!HasReport) { await AnalyzeAsync(); return; }
        if (PlanSteps.Any(s => s.State == TreatmentStepState.Proposed)) { await ApplyPlanAsync(); return; }
        await ExportAsync();
    }
    public RelayCommand ExportCommand { get; }
    public RelayCommand PlayPauseCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand UndoCommand { get; }
    public RelayCommand RedoCommand { get; }
    public RelayCommand ZoomInCommand { get; }
    public RelayCommand ZoomOutCommand { get; }
    public RelayCommand ZoomFitCommand { get; }
    public RelayCommand ZoomToFindingCommand { get; }
    public RelayCommand ApplySuggestionCommand { get; }
    public RelayCommand RemoveNodeCommand { get; }
    public RelayCommand ToggleNodeCommand { get; }
    public RelayCommand RevertAllCommand { get; }
    public RelayCommand SaveReportCommand { get; }

    private void RaiseAllCanExecute()
    {
        OpenCommand.RaiseCanExecuteChanged();
        AnalyzeCommand.RaiseCanExecuteChanged();
        ApplyPlanCommand.RaiseCanExecuteChanged();
        PrimaryActionCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(PrimaryActionLabel));
        OnPropertyChanged(nameof(HasVerification));
        ExportCommand.RaiseCanExecuteChanged();
        PlayPauseCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        UndoCommand.RaiseCanExecuteChanged();
        RedoCommand.RaiseCanExecuteChanged();
        ZoomInCommand.RaiseCanExecuteChanged();
        ZoomOutCommand.RaiseCanExecuteChanged();
        ZoomFitCommand.RaiseCanExecuteChanged();
        ZoomToFindingCommand.RaiseCanExecuteChanged();
        ApplySuggestionCommand.RaiseCanExecuteChanged();
        RemoveNodeCommand.RaiseCanExecuteChanged();
        ToggleNodeCommand.RaiseCanExecuteChanged();
        RevertAllCommand.RaiseCanExecuteChanged();
        SaveReportCommand.RaiseCanExecuteChanged();
    }

    // ----------------------------------------------------------------- open

    private void CheckEnvironment()
    {
        if (!FFmpegLocator.IsAvailable)
        {
            EnvironmentWarning =
                "FFmpeg was not found, so files cannot be opened or exported. " +
                "Install FFmpeg and add it to PATH, or place ffmpeg.exe next to AudioStudio.exe.";
        }
        else
        {
            _log.Write("environment", new Dictionary<string, object?>
            {
                ["ffmpeg"] = FFmpegLocator.FFmpegPath,
                ["version"] = FFmpegLocator.GetVersionString()
            });
        }
    }

    public async Task OpenAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open a recording",
            Filter = AudioImporter.FileDialogFilter,
            CheckFileExists = true
        };

        if (dialog.ShowDialog() != true) return;
        await OpenFileAsync(dialog.FileName);
    }

    public async Task OpenFileAsync(string path)
    {
        if (IsBusy) return;

        IsBusy = true;
        _workCts = new CancellationTokenSource();

        try
        {
            StatusText = $"Opening {Path.GetFileName(path)}…";
            Progress = 0;

            var result = await AudioImporter.ImportAsync(
                path,
                new Progress<double>(p => { Progress = p * 70; }),
                _workCts.Token);

            _player.Stop();

            StatusText = "Building waveform…";
            var peaks = await Task.Run(() => PeakData.Build(result.Buffer, _workCts.Token), _workCts.Token);
            Progress = 90;

            var project = AudioProject.Create(path, result.Buffer, result.Info);
            project.OriginalPeaks = peaks;
            project.Chain.Changed += OnChainChanged;
            project.History.Changed += OnHistoryChanged;

            Project = project;

            Findings.Clear();
            Suggestions.Clear();
            ChainNodes.Clear();
            HistoryEntries.Clear();
            PlanSteps.Clear();
            LastVerification.Clear();
            VerificationHeadline = "";
            SelectedFinding = null;
            Markers = Array.Empty<TimelineMarker>();

            CurrentPeaks = peaks;
            ComparisonPeaks = null;
            DisplaySamples = await Task.Run(() => result.Buffer.ToMono(), _workCts.Token);

            _player.Load(result.Buffer);
            Playhead = 0;
            ZoomToFit();

            OnPropertyChanged(nameof(TotalDuration));
            OnPropertyChanged(nameof(PositionLabel));
            RaiseReportProperties();

            Progress = 100;
            StatusText = $"Opened {result.Info.DisplaySummary}. " +
                         "Inspect the recording and I will tell you what is wrong with it.";

            _log.Write("import", new Dictionary<string, object?>
            {
                ["file"] = Path.GetFileName(path),
                ["codec"] = result.Info.Codec,
                ["duration_s"] = result.Buffer.Duration.TotalSeconds,
                ["source_rate"] = result.Info.SampleRate
            });
        }
        catch (OperationCanceledException)
        {
            StatusText = "Open cancelled.";
        }
        catch (Exception ex)
        {
            StatusText = "Could not open the file.";
            MessageBox.Show(ex.Message, "Could not open file",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
            Progress = 0;
        }
    }

    // ------------------------------------------------------------- analysis

    public async Task AnalyzeAsync()
    {
        if (Project is null || IsBusy) return;

        IsBusy = true;
        _workCts = new CancellationTokenSource();

        try
        {
            var buffer = Project.GetCurrentBuffer(_workCts.Token);

            var progress = new Progress<(string Stage, double Fraction)>(p =>
            {
                StatusText = p.Stage + "…";
                Progress = p.Fraction * 100;
            });

            var report = await _engine.AnalyzeAsync(buffer, Project.SourceInfo, progress, _workCts.Token);

            Project.Report = report;

            Findings.Clear();
            foreach (var f in report.Findings) Findings.Add(f);

            PlanSteps.Clear();
            foreach (var step in TreatmentPlan.Build(report).Steps) PlanSteps.Add(step);
            LastVerification.Clear();

            SelectedFinding = report.Findings.FirstOrDefault(f => f.Severity >= FindingSeverity.Minor)
                              ?? report.Findings.FirstOrDefault();

            UpdateMarkers();

            RaiseReportProperties();
            RaiseAllCanExecute();

            StatusText = report.Summary;
        }
        catch (OperationCanceledException)
        {
            StatusText = "Analysis cancelled.";
        }
        catch (Exception ex)
        {
            StatusText = "Analysis failed.";
            MessageBox.Show(ex.Message, "Analysis failed",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
            Progress = 0;
        }
    }

    private void UpdateMarkers()
    {
        if (Project?.Report is null)
        {
            Markers = Array.Empty<TimelineMarker>();
            return;
        }

        // Every located problem stays on the timeline, so the shape of the
        // damage is visible at a glance. The selected finding is flagged so it
        // stands out from the rest rather than replacing them.
        var located = Project.Report.Findings
            .Where(f => f.Severity >= FindingSeverity.Minor && f.Occurrences.Count > 0)
            .ToList();

        Markers = located
            .SelectMany(f => f.Occurrences.Select(o => new TimelineMarker(
                o, f.Severity, f.Title, ReferenceEquals(f, SelectedFinding))))
            .Take(2000)
            .ToList();
    }

    // ---------------------------------------------------------------- edits

    private void OnChainChanged(object? sender, EventArgs e)
    {
        ChainNodes.Clear();
        if (Project is null) return;

        foreach (var node in Project.Chain.Nodes) ChainNodes.Add(node);

        OnPropertyChanged(nameof(HasEdits));
        RefreshEditedAudio();
        RaiseAllCanExecute();
    }

    private void OnHistoryChanged(object? sender, EventArgs e)
    {
        HistoryEntries.Clear();
        if (Project is null) return;

        foreach (var entry in Project.History.Entries.Reverse()) HistoryEntries.Add(entry);
        RaiseAllCanExecute();
    }

    public bool HasEdits => Project?.HasEdits == true;

    private async void RefreshEditedAudio()
    {
        if (Project is null) return;

        try
        {
            IsBusy = true;
            StatusText = "Applying changes…";

            var project = Project;
            var cts = new CancellationTokenSource();

            var buffer = await Task.Run(() => project.Chain.Evaluate(null, cts.Token), cts.Token);
            var peaks = await Task.Run(() => PeakData.Build(buffer, cts.Token), cts.Token);
            var mono = await Task.Run(() => buffer.ToMono(), cts.Token);

            project.EditedPeaks = peaks;
            CurrentPeaks = peaks;
            ComparisonPeaks = project.HasEdits ? project.OriginalPeaks : null;
            DisplaySamples = mono;

            _player.Load(buffer, preservePosition: true);

            StatusText = project.HasEdits
                ? $"{project.Chain.Nodes.Count(n => n.IsEnabled)} change" +
                  $"{(project.Chain.Nodes.Count(n => n.IsEnabled) == 1 ? "" : "s")} applied. " +
                  "The original is untouched — undo at any time."
                : "Back to the original recording.";
        }
        catch (Exception ex)
        {
            StatusText = $"Could not apply changes: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RaiseReportProperties()
    {
        OnPropertyChanged(nameof(HasReport));
        OnPropertyChanged(nameof(HealthScore));
        OnPropertyChanged(nameof(HealthLabel));
        OnPropertyChanged(nameof(HealthVerdict));
        OnPropertyChanged(nameof(HealthMeter));
        OnPropertyChanged(nameof(DiagnosisConfidence));
        OnPropertyChanged(nameof(ReportSummary));
        OnPropertyChanged(nameof(AnalysisMetaLabel));
        OnPropertyChanged(nameof(HasPlan));
        OnPropertyChanged(nameof(PlanHeadline));
        OnPropertyChanged(nameof(PrimaryActionLabel));
        OnPropertyChanged(nameof(HasVerification));
        OnPropertyChanged(nameof(VerificationHeadline));
    }

    public bool HasVerification => LastVerification.Count > 0;

    private string _verificationHeadline = "";
    public string VerificationHeadline
    {
        get => _verificationHeadline;
        private set => SetProperty(ref _verificationHeadline, value);
    }

    /// <summary>
    /// Runs the whole treatment plan in order, measuring each step against the
    /// audio as it stood before that step ran. A step that fails to improve its
    /// target metric is rolled straight back out of the chain rather than left
    /// in place — the point of verifying is to act on the result.
    /// </summary>
    public async Task ApplyPlanAsync()
    {
        if (Project is null || IsBusy || PlanSteps.Count == 0) return;

        IsBusy = true;
        _workCts = new CancellationTokenSource();

        try
        {
            var project = Project;
            var pending = PlanSteps.Where(s => s.State == TreatmentStepState.Proposed).ToList();
            int applied = 0, rejected = 0;

            for (int i = 0; i < pending.Count; i++)
            {
                _workCts.Token.ThrowIfCancellationRequested();

                var step = pending[i];
                StatusText = $"{step.Suggestion.Title}… ({i + 1} of {pending.Count})";
                Progress = 100.0 * i / pending.Count;

                var before = await Task.Run(
                    () => project.Chain.Evaluate(null, _workCts.Token), _workCts.Token);

                var node = step.Suggestion.CreateNode();
                project.ApplyNode(node, step.Suggestion.Title);
                step.AppliedNode = node;
                step.State = TreatmentStepState.Applied;

                var after = await Task.Run(
                    () => project.Chain.Evaluate(null, _workCts.Token), _workCts.Token);

                var verification = await Task.Run(() => RepairVerification.Verify(
                    before, after, step.Suggestion.Title, step.Suggestion.TargetMetric,
                    step.Suggestion.HumFundamental, _workCts.Token), _workCts.Token);

                step.Verification = verification;

                if (verification.Target.Improved)
                {
                    step.State = TreatmentStepState.Verified;
                    applied++;
                }
                else
                {
                    // Measured no benefit — take it back out.
                    project.RemoveNode(node);
                    step.State = TreatmentStepState.Rejected;
                    rejected++;
                }

                _log.Write("treatment.step", new Dictionary<string, object?>
                {
                    ["repair"] = step.Suggestion.Title,
                    ["method"] = step.Suggestion.Method,
                    ["target"] = verification.Target.Name,
                    ["before"] = verification.Target.Before,
                    ["after"] = verification.Target.After,
                    ["kept"] = verification.Target.Improved,
                    ["collateral_db"] = verification.CollateralChangeDb
                });

                RefreshPlanDisplay();
            }

            var last = pending.LastOrDefault(s => s.Verification is not null)?.Verification;
            LastVerification.Clear();
            if (last is not null)
                foreach (var m in last.Metrics) LastVerification.Add(m);

            VerificationHeadline = rejected == 0
                ? $"Applied {applied} repair{(applied == 1 ? "" : "s")}, each measured and confirmed."
                : $"Applied {applied}, rolled back {rejected} that measured no improvement.";

            StatusText = VerificationHeadline;
            RaiseReportProperties();
        }
        catch (OperationCanceledException)
        {
            StatusText = "Repair cancelled.";
        }
        catch (Exception ex)
        {
            StatusText = "Repair failed.";
            MessageBox.Show(ex.Message, "Repair failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
            Progress = 0;
            RaiseAllCanExecute();
        }
    }

    private void RefreshPlanDisplay()
    {
        // TreatmentStep is a plain object, so nudge the list to redraw.
        var snapshot = PlanSteps.ToList();
        PlanSteps.Clear();
        foreach (var s in snapshot) PlanSteps.Add(s);

        OnPropertyChanged(nameof(PlanHeadline));
        OnPropertyChanged(nameof(PrimaryActionLabel));
    }

    private void ApplySuggestion(RepairSuggestion? suggestion)
    {
        if (suggestion is null || Project is null) return;

        var node = suggestion.CreateNode();
        Project.ApplyNode(node, suggestion.Title);

        _log.Write("repair.apply", new Dictionary<string, object?>
        {
            ["node"] = node.Kind,
            ["title"] = suggestion.Title,
            ["safety"] = suggestion.Safety.ToString(),
            ["method"] = suggestion.Method
        });
    }

    private void RemoveNode(IProcessingNode? node)
    {
        if (node is null || Project is null) return;
        Project.RemoveNode(node);
    }

    private void ToggleNode(IProcessingNode? node)
    {
        if (node is null || Project is null) return;
        Project.SetNodeEnabled(node, !node.IsEnabled);
    }

    private void RevertAll()
    {
        if (Project is null) return;

        var removed = Project.Chain.Nodes.ToList();
        if (removed.Count == 0) return;

        Project.Chain.Clear();
        Project.History.Record("Revert all changes",
            undo: () => { foreach (var n in removed) Project.Chain.Add(n); },
            redo: () => Project.Chain.Clear());
    }

    private void Undo()
    {
        Project?.History.Undo();
    }

    private void Redo()
    {
        Project?.History.Redo();
    }

    // ------------------------------------------------------------- playback

    private void TogglePlayback()
    {
        _player.TogglePlayPause();
        OnPropertyChanged(nameof(IsPlaying));
        OnPropertyChanged(nameof(PlayPauseGlyph));
    }

    private void StopPlayback()
    {
        _player.Stop();
        _player.Position = 0;
        Playhead = 0;
        OnPropertyChanged(nameof(IsPlaying));
        OnPropertyChanged(nameof(PlayPauseGlyph));
    }

    public void SeekTo(double seconds)
    {
        if (Project is null) return;
        double clamped = Math.Clamp(seconds, 0, TotalDuration);
        _player.Position = clamped;
        Playhead = clamped;
    }

    private void OnPlaybackTick()
    {
        if (!_player.IsPlaying) return;

        Playhead = _player.Position;

        // Follow the playhead when it leaves the visible window.
        if (Playhead > ViewStart + ViewLength || Playhead < ViewStart)
            ViewStart = Math.Max(0, Playhead - ViewLength * 0.1);
    }

    // ----------------------------------------------------------------- zoom

    public void Zoom(double factor, double? anchorSeconds = null)
    {
        if (Project is null) return;

        double anchor = anchorSeconds ?? (ViewStart + ViewLength / 2);
        double newLength = Math.Clamp(ViewLength * factor, 0.01, TotalDuration);
        double fraction = ViewLength > 0 ? (anchor - ViewStart) / ViewLength : 0.5;

        ViewLength = newLength;
        ViewStart = Math.Clamp(anchor - fraction * newLength, 0, Math.Max(0, TotalDuration - newLength));
    }

    public void ZoomToFit()
    {
        if (Project is null) return;
        ViewStart = 0;
        ViewLength = Math.Max(0.01, TotalDuration);
    }

    private void ZoomToSelectedFinding()
    {
        var finding = SelectedFinding;
        if (finding is null || finding.Occurrences.Count == 0 || Project is null) return;

        var first = finding.Occurrences[0];
        double padding = Math.Max(0.5, first.Duration.TotalSeconds * 0.5);

        ViewStart = Math.Max(0, first.Start.TotalSeconds - padding);
        ViewLength = Math.Min(TotalDuration - ViewStart,
                              first.Duration.TotalSeconds + padding * 2);
        SeekTo(first.Start.TotalSeconds);
    }

    public void ScrollBy(double seconds)
    {
        if (Project is null) return;
        ViewStart = Math.Clamp(ViewStart + seconds, 0, Math.Max(0, TotalDuration - ViewLength));
    }

    // --------------------------------------------------------------- export

    public async Task ExportAsync()
    {
        if (Project is null || IsBusy) return;

        var dialog = new SaveFileDialog
        {
            Title = "Export audio",
            FileName = Project.Name + (Project.HasEdits ? " (repaired)" : ""),
            Filter = string.Join("|", ExportFormat.All.Select(f => $"{f.Name}|*{f.Extension}")),
            AddExtension = true
        };

        if (dialog.ShowDialog() != true) return;

        var format = ExportFormat.All[Math.Clamp(dialog.FilterIndex - 1, 0, ExportFormat.All.Count - 1)];

        IsBusy = true;
        _workCts = new CancellationTokenSource();

        try
        {
            StatusText = "Preparing audio…";
            var buffer = await Task.Run(() => Project.GetCurrentBuffer(_workCts.Token), _workCts.Token);

            StatusText = $"Exporting as {format.Name}…";
            await AudioExporter.ExportAsync(
                buffer, dialog.FileName, format,
                new Progress<double>(p => Progress = p * 100),
                _workCts.Token);

            StatusText = $"Exported to {Path.GetFileName(dialog.FileName)}.";

            _log.Write("export", new Dictionary<string, object?>
            {
                ["file"] = Path.GetFileName(dialog.FileName),
                ["format"] = format.Name,
                ["edits"] = Project.Chain.Nodes.Count(n => n.IsEnabled)
            });
        }
        catch (OperationCanceledException)
        {
            StatusText = "Export cancelled.";
        }
        catch (Exception ex)
        {
            StatusText = "Export failed.";
            MessageBox.Show(ex.Message, "Export failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
            Progress = 0;
        }
    }

    private async Task SaveReportAsync()
    {
        if (Project?.Report is null) return;

        var dialog = new SaveFileDialog
        {
            Title = "Save analysis report",
            FileName = Project.Name + " - analysis.txt",
            Filter = "Text report|*.txt|JSON log|*.jsonl"
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            if (dialog.FilterIndex == 2)
            {
                await _log.SaveAsync(dialog.FileName);
            }
            else
            {
                await File.WriteAllTextAsync(dialog.FileName, BuildTextReport(Project, Project.Report));
            }
            StatusText = $"Report saved to {Path.GetFileName(dialog.FileName)}.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Could not save report",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string BuildTextReport(AudioProject project, AnalysisReport report)
    {
        var sb = new System.Text.StringBuilder();

        sb.AppendLine("AI Audio Restoration Studio — analysis report");
        sb.AppendLine(new string('=', 60));
        sb.AppendLine($"File:      {project.FileName}");
        sb.AppendLine($"Source:    {project.SourceInfo.DisplaySummary}");
        sb.AppendLine($"Duration:  {TimeRange.Format(report.Duration)}");
        sb.AppendLine($"Analysed:  {report.CompletedAt:yyyy-MM-dd HH:mm:ss} " +
                      $"(took {report.ElapsedTime.TotalSeconds:0.0} s)");
        sb.AppendLine();
        sb.AppendLine($"Condition: {report.HealthLabel} ({report.HealthScore}/100)");
        sb.AppendLine($"Summary:   {report.Summary}");
        sb.AppendLine();

        foreach (var finding in report.Findings)
        {
            sb.AppendLine(new string('-', 60));
            sb.AppendLine($"{finding.SeverityLabel.ToUpperInvariant()}  {finding.Title}");
            sb.AppendLine($"Confidence: {finding.ConfidencePercent}   Location: {finding.LocationLabel}");
            sb.AppendLine();
            sb.AppendLine(Wrap(finding.Description));

            if (finding.ProbableCause is not null)
            {
                sb.AppendLine();
                sb.AppendLine("Probable cause:");
                sb.AppendLine(Wrap(finding.ProbableCause));
            }

            if (finding.SuggestedStrategy is not null)
            {
                sb.AppendLine();
                sb.AppendLine("Suggested strategy:");
                sb.AppendLine(Wrap(finding.SuggestedStrategy));
            }

            if (finding.Evidence.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Measurements:");
                foreach (var m in finding.Evidence)
                    sb.AppendLine($"  {m.Name,-32} {m.Value}" +
                                  (m.Note is null ? "" : $"   ({m.Note})"));
            }

            if (finding.Occurrences.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"Locations ({finding.Occurrences.Count}):");
                foreach (var o in finding.Occurrences.Take(40))
                    sb.AppendLine($"  {o}");
                if (finding.Occurrences.Count > 40)
                    sb.AppendLine($"  … and {finding.Occurrences.Count - 40} more");
            }

            sb.AppendLine();
        }

        sb.AppendLine(new string('=', 60));
        sb.AppendLine("Detector timings:");
        foreach (var run in report.DetectorRuns)
            sb.AppendLine($"  {run.DetectorName,-20} {run.FindingCount} finding(s)  " +
                          $"{run.Elapsed.TotalMilliseconds,8:0} ms" +
                          (run.Error is null ? "" : $"  ERROR: {run.Error}"));

        return sb.ToString();

        static string Wrap(string text, int width = 76)
        {
            var words = text.Split(' ');
            var line = new System.Text.StringBuilder();
            var result = new System.Text.StringBuilder();

            foreach (var word in words)
            {
                if (line.Length + word.Length + 1 > width)
                {
                    result.AppendLine("  " + line);
                    line.Clear();
                }
                if (line.Length > 0) line.Append(' ');
                line.Append(word);
            }
            if (line.Length > 0) result.AppendLine("  " + line);
            return result.ToString().TrimEnd();
        }
    }

    public void Dispose()
    {
        _playbackTimer.Stop();
        _workCts?.Cancel();
        _player.Dispose();
    }
}
