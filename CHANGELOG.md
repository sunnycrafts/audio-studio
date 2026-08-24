# Changelog

All notable changes to AI Audio Restoration Studio.
Format follows [Keep a Changelog](https://keepachangelog.com/); versioning is
semantic. Phase numbers refer to the project specification.

---

## [0.3.1] — 2026-07-27

Correctness work on the treatment plan, found by testing the full workflow on a
genuinely damaged file rather than on individual repairs in isolation.

### Fixed

- **Click repair was rolled back as useless every time.** Its verification
  target was "noise floor", which clicks barely move — so a repair that removed
  every click measured as having achieved nothing and was automatically undone.
  Verification now counts clicks directly.
- **Repair order left the recording clicking.** Clicks were swept before hum was
  notched. Click detection works against a local scale, so removing a loud tonal
  component lifts the relative prominence of everything left behind: a second
  crop of discontinuities appeared the moment the hum went, with nothing left in
  the plan to catch them. Reordering to rebuild → de-hum → de-click took the end
  result from 59/100 to 94/100 on the test recording, and the remaining crackle
  from Critical to Minor.
- **Clipped peaks are now rebuilt before clicks are swept**, since flat tops
  mask clicks during detection and the reconstruction adds fresh joins.

### Changed

- Click detection extracted to a shared `ClickFinder` used by the detector, the
  repair and the verifier. Three copies had drifted apart, which is what allowed
  the verifier to disagree with the repair. `ClickDetector` dropped from about
  250 lines to 92.
- The workflow test now synthesises its damaged file in code instead of through
  an ffmpeg filter graph. The old graph silently produced a file 12 dB below
  full scale, so the clipping it was supposed to contain never existed and the
  ordering assertions were quietly skipping.

---

## [0.3.0] — 2026-07-27

The restoration stage. Recordings can now actually be repaired, and every
repair is measured rather than assumed.

### Added — repair engine

- **Analysis–modify–synthesis framework** (`SpectralProcessor`): windowed STFT
  with overlap-add synthesis normalised by the accumulated window energy, so
  reconstruction stays exact at the edges of the signal. Verified transparent to
  −240 dBFS when nothing is modified.
- **Hum removal** — a bank of zero-phase notch filters, one per harmonic, of
  constant width in hertz rather than constant Q. Measured: 28 dB of hum removed
  from a real recording with 0.29 dB of collateral change, and the music above
  200 Hz altered by 0.13 dB.
- **Noise reduction** — Wiener filtering driven by a decision-directed SNR
  estimate, with noise tracked by minimum statistics so no noise profile has to
  be selected by hand. Measured: 8.5 dB SNR improvement while the content
  itself lost 0.12 LU.
- **Click repair** — cubic interpolation across each detected discontinuity.
  Measured: click sites restored to within −64 dBFS of the undamaged signal,
  with audio away from clicks bit-identical.
- **Clipped peak reconstruction** — cubic Hermite arcs across each flat top with
  tangents taken from the approach, then peak scaling to restore headroom.
- **Treatment plan** — repairs ordered so they do not interfere: trim, then
  sample-level damage, then hum, then broadband noise, then level. Applied in
  one action, with each step measured against the audio as it stood before it.
- **Verification with rollback** — every repair is measured before and after
  against the metric it was meant to move, plus collateral change elsewhere. A
  step that shows no improvement is removed from the chain automatically.

### Added — interface

- Reframed around diagnosis: **inspect → diagnose → treatment plan → repair →
  verification**, with a single primary action that advances through it.
- Diagnosis panel written in the engine's own voice, stating what was found,
  how confident it is, and what it can and cannot repair.
- Treatment plan panel showing each step and its state — proposed, verified or
  rolled back.
- Recording health shown as a percentage, a ten-block meter and a verdict
  ("Restoration recommended"), with separate diagnostic confidence.
- Findings list led by a severity dot and the severity word, so rank reads
  before the text does.
- Timeline markers for every located problem, labelled where there is room,
  with the selected finding highlighted; selecting a finding jumps to it.
- Repairs described by outcome ("Silence the electrical hum") with a safety
  label, and the technique shown as a footnote rather than as the headline.
- Colour reserved for severity and outcome; everything else stays neutral.

### Changed

- `RepairSuggestion` now carries method, safety class and target metric.
- Noise floor estimation extracted to a shared `NoiseFloorEstimator` used by
  both the analysis engine and the repair verifier.

### Fixed

- **Repair verification could not see its own improvements.** The verifier
  measured the noise floor with a plain percentile over time, which counts any
  sustained tone as noise — so a denoiser that cleaned the gaps between notes
  measured as having done nothing. Both sides now share one estimator.
- **Noise reduction removed sustained notes.** Minimum statistics assumes every
  band falls quiet occasionally; a held note never does, so it was tracked as
  noise and suppressed. Guarded with a cross-frequency median, which a tone
  cannot shift.
- **The published exe crashed on launch.** Static field initialisers in
  `WaveformView` ran in declaration order and used a cache declared below them,
  which was still null.

### Not included

Source separation — isolating overlapping voices, counting speakers — needs
trained models and is not in this release. Findings that require it state their
strategy and deliberately offer no repair button.

---

## [0.2.0] — 2026-07-27

First working build. Phases 1 and 2 of the specification are complete.

### Added — Phase 1, core foundation

- Canonical internal audio format: 48 kHz, stereo, 32-bit float, applied to
  every import regardless of source.
- FFmpeg-backed import streamed over stdout, so long recordings never need a
  temporary WAV on disk. Supports WAV, FLAC, AIFF, MP3, AAC, M4A, OGG, Opus,
  CAF, BWF, WMA and the audio track of MP4/MKV/MOV.
- FFmpeg-backed export to WAV (16/24/32-bit), FLAC, MP3 and AAC, written via a
  temporary file so a failure cannot leave a truncated output.
- Min/max peak pyramid for waveform rendering at a cost proportional to window
  width rather than recording length.
- Logarithmic spectrogram, recomputed per visible window at zoom-appropriate
  resolution.
- WASAPI playback from the in-memory buffer with click-to-seek.
- Non-destructive processing chain over an immutable source, with per-node
  output caching and reachability-based cache eviction.
- Processing nodes: gain, peak normalise, DC offset removal, polarity invert,
  high-pass, trim.
- Undo/redo over chain mutations with full history.
- WPF/Fluent interface: command bar, waveform and spectrogram, findings list,
  inspector, edit chain, history and structured diagnostics log.
- Structured JSON operation logging for every analysis step.

### Added — Phase 2, analysis engine

- Shared feature context computed once per pass: mono mixdown, 4096-point STFT,
  32768-point high-resolution STFT, long-term average spectrum, noise floor
  estimate, frame RMS and BS.1770 loudness.
- ITU-R BS.1770-4 loudness: K-weighting, gated integrated LUFS, loudness range,
  peak, RMS and crest factor.
- Eleven detectors: level, clipping, DC offset, mains hum, broadband noise,
  clicks, bandwidth, stereo, reverberation, silence and voice activity.
- Findings model carrying confidence, severity, locations, the measurements
  behind each verdict, probable cause and recommended strategy.
- Health score, plain-English summary and exportable text report.
- Repair suggestions mapped from findings, offered only where classical DSP
  performs the fix correctly.

### Added — packaging

- Portable single-file `AudioStudio.exe`, self-contained, no install required.
- Inno Setup installer (`installer/AudioStudio.iss` → `AudioStudio-Setup.exe`)
  with Start menu and optional desktop shortcuts, opt-in file associations for
  WAV/FLAC/MP3/M4A/AIFF, and a post-install notice if FFmpeg cannot be found.
  Installs per-user without elevation by default. Verified through a full
  install → launch → uninstall cycle leaving no files, shortcuts or registry
  keys behind.

### Added — tests

- Synthetic detector suite: twelve cases asserting both required detections and
  required non-detections.
- End-to-end suite covering FFmpeg import, format conversion, lossy detection,
  chain immutability and export round trip.

### Fixed during development

- **Clicks were never reported.** The minimum-gap check initialised its sentinel
  to `int.MinValue`, so `i - lastEvent` overflowed to a negative value and every
  candidate was rejected.
- **Noise floor was wrong for stationary material.** Estimating it from a low
  percentile over time alone made a sustained tone measure as its own noise,
  reporting a pure sine as severely noisy. A valley follower across frequency
  now runs as a second stage.
- **Mains hum fired on bass content.** Added a peak-width test and a
  frequency-drift test, and tightened the presence measurement to the exact bin
  from the long-term spectrum instead of re-searching a tolerance window per
  frame.
- **Brick-wall detection missed real codec cutoffs.** Steepness was measured in
  dB per kHz over too narrow a span; it is now dB per octave across the
  transition.
- **Codec artefacts were reported as dense crackle.** Added an isolation test,
  raised the global floor to the same outlier multiplier as the local test, and
  set a minimum event count.
- **The published exe crashed with `DllNotFoundException`.** WPF's native
  components are excluded from ordinary single-file bundling and were left loose
  beside the executable; `IncludeAllContentForSelfExtract` now bundles them.
- **Startup crashed on a binding error.** `ProgressBar.Value` binds two-way by
  default and the view model exposes `Progress` read-only; the binding is now
  pinned one-way.
- **FFmpeg beside the exe was never found in the portable build.**
  `AppContext.BaseDirectory` points at the self-extraction temp folder; the
  lookup and the crash log now use the real executable path.
