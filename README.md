# AI Audio Restoration Studio

I built this because every "fix my audio" tool I tried asks you to pick a
filter and guess at settings before it's told you what's actually wrong with
the recording. This one measures first, explains the problem in plain
English with the numbers behind it, and only then offers a repair — so you're
not stabbing in the dark with a noise reduction slider.

A Windows 11 audio restoration tool built on the principle that software should
work out what is wrong with a recording before it touches it.

The user is never asked which filter to reach for. The engine measures the
audio, reports what it found in plain English with the numbers behind each
verdict, and only then offers a repair.

**Current version: 0.3.0 — Phases 1–3 complete, plus verification from Phase 4.**

The workflow is deliberately medical: **inspect → diagnose → treatment plan →
repair → verification**. The engine measures the recording, explains what it
found and how confident it is, proposes an ordered plan, carries it out, and
then measures again to prove each step helped. A step that shows no measurable
improvement is removed automatically.

---

## What works today

### Phase 1 — Core foundation

- **Import** anything FFmpeg can decode: WAV, FLAC, AIFF, MP3, AAC, M4A, OGG,
  Opus, CAF, BWF, WMA, and the audio track of MP4/MKV/MOV. Everything is
  converted on the way in to one internal format — 48 kHz, stereo, 32-bit float —
  so no code downstream ever branches on sample rate or bit depth.
- **Waveform display** driven by a min/max peak pyramid, so drawing cost depends
  on the window width rather than the length of the recording.
- **Spectrogram** on a logarithmic frequency axis, recomputed for the visible
  window at whatever resolution the current zoom deserves. Memory stays flat no
  matter how long the file is, and zooming in reveals real extra detail instead
  of magnifying a cached image.
- **Playback** straight from the in-memory buffer via WASAPI, with click-to-seek
  on either display.
- **Non-destructive editing.** The import is immutable. Every change is a node
  in a processing chain that reads its input and returns a new buffer; nodes can
  be bypassed, removed or reordered and the result is recomputed from the
  original. Each node's output is cached, so editing the last node in a chain of
  ten does not recompute the first nine.
- **Undo/redo** over chain mutations, with full history.
- **Export** to WAV (16/24/32-bit), FLAC, MP3 or AAC. Writes to a temporary file
  and moves it into place, so a failed or cancelled export never leaves a
  truncated file where a finished one should be.

### Phase 2 — Analysis engine

Eleven detectors run against one shared feature set computed once per pass.
Every finding carries a confidence, a severity, the locations affected, the
measurements that produced it, and the repair strategy it implies.

| Detector | What it finds | How |
|---|---|---|
| Level | Loudness, headroom, over-compression | ITU-R BS.1770-4 gated LUFS, loudness range, crest factor |
| Clipping | Flat-topped waveform peaks | Runs of consecutive samples pinned at full scale, verified flat |
| DC offset | Waveform sitting off centre | Per-channel mean |
| Mains hum | 50/60 Hz electrical interference | Harmonic comb scoring on a 32768-point transform, plus peak-width and frequency-drift tests |
| Broadband noise | Hiss, rumble, room tone, SNR | Two-stage noise floor: low percentile over time, then a valley follower across frequency |
| Clicks | Clicks, pops, crackle | Median-filter residual against a local robust scale, with an isolation test |
| Bandwidth | Codec damage, restricted top end | Brick-wall detection measured in dB per octave |
| Stereo | Phase inversion, imbalance, dual mono | Inter-channel correlation and mid/side energy |
| Reverberation | RT60, room character | Least-squares fit to natural decays found in the recording |
| Silence | Edge silence, gaps, digital dropouts | Adaptive level threshold plus exact-zero detection |
| Voice activity | Where speech is | Speech-band energy ratio combined with spectral flatness |

Findings feed a health score, a plain-English summary, and an exportable report.

### Phase 3 — Restoration

| Repair | Technique | Measured on a real recording |
|---|---|---|
| Silence electrical hum | Zero-phase notch bank, constant width in hertz | 28 dB of hum removed, 0.29 dB collateral, music above 200 Hz altered by 0.13 dB |
| Reduce background noise | Wiener gain from a decision-directed SNR estimate, noise tracked by minimum statistics | 8.5 dB SNR improvement, content down only 0.12 LU |
| Repair clicks | Cubic interpolation across each discontinuity | Click sites restored to within −64 dBFS; audio elsewhere bit-identical |
| Rebuild clipped peaks | Cubic Hermite across each flat top, then peak scaling | All clipped samples eliminated, headroom restored |
| Remove rumble, recentre, rebalance, normalise, trim | Filters and gain | Exact |

Two design choices are worth stating plainly. Hum notches are a fixed width in
hertz rather than a fixed Q — constant Q sounds reasonable until you notice that
6 Hz at the fundamental becomes 48 Hz by the eighth harmonic, tearing a hole
through the music. And noise reduction deliberately leaves a floor of residual
noise: driving quiet bins to silence is what produces the tinkling "musical
noise" that gives cheap denoising away.

**Source separation is not included.** Isolating overlapping voices or counting
speakers needs trained models. Findings that require it state their strategy and
offer no repair button, rather than substituting something weaker under the
right label.

### Phase 4 (partial) — Verification

Every repair is measured before and after against the metric it was meant to
move, plus how much of the rest of the recording changed. If a step shows no
improvement it is rolled back out of the chain automatically — the point of
measuring is to act on the result.

---

## Building

Requires the .NET 8 SDK and FFmpeg.

```bash
dotnet publish src/AudioStudio.App/AudioStudio.App.csproj -c Release -r win-x64 -o publish
```

This produces a single portable `AudioStudio.exe`. To build the installer as
well, compile the Inno Setup script afterwards:

```bash
"C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\AudioStudio.iss
```

Both artefacts land at the repository root: `AudioStudio.exe` (portable) and
`AudioStudio-Setup.exe` (installer).

`IncludeAllContentForSelfExtract` is set deliberately: WPF's native components
are otherwise left loose beside the executable, and the app dies with a
`DllNotFoundException` as soon as the exe is moved on its own.

### FFmpeg

Found automatically from the application folder, then `PATH`, then the usual
install locations. To ship it portably, drop `ffmpeg.exe` and `ffprobe.exe`
beside `AudioStudio.exe`.

---

## Tests

```bash
dotnet run --project tests/AudioStudio.Tests -c Release
```

Two suites, both of which must pass:

- **Synthetic** — signals built with known defects, each asserting both what
  must be found and what must *not* be. The negative cases carry most of the
  weight: a sustained bass note must not read as mains hum, a loud but unclipped
  sine must not read as clipping.
- **End-to-end** — real files through FFmpeg, checking format conversion, that
  a 64 kbps MP3 is correctly identified as bandwidth-limited, that the edit
  chain leaves the source untouched, and that an export round trip preserves
  level and duration.

---

## Known limitations

- **Lossy sources can produce a small clicks finding.** A low-bitrate encode
  genuinely contains a handful of transient artefacts. The detector is tuned so
  these never escalate to a dense-crackle verdict, but a few may still be
  reported. The real finding in that case is the bandwidth loss.
- **Reverberation estimates need natural decays.** A recording with no pauses
  gives the detector nothing to measure and it reports nothing rather than
  guessing.
- **Detectors do not yet reason about each other.** The spec's repair planner,
  which weighs findings against one another, arrives with Phase 3.
- **The processing graph is linear.** The node model and caching are built for
  the full DAG, but parallel branches feeding an intelligent mixer are Phase 3.

---

## Still to come

- **Phase 3** — ONNX model hosting, source separation, spectral noise
  reduction, hum notching, click interpolation, adaptive repair planning.
- **Phase 4** — quality scoring with automatic rollback, GPU acceleration,
  batch processing.
- **Phase 5** — plugin SDK, live cleanup, multichannel, CLI.

The architecture is laid out for these: detectors register through a single
interface, and repairs are nodes in the existing chain.

---

## Layout

```
src/AudioStudio.Core/     engine — no UI dependency
  Audio/                  buffer, format, peak pyramid
  Dsp/                    FFT, STFT, windows, biquads, BS.1770 loudness
  Analysis/               findings, shared feature context, detectors
  Graph/                  processing chain, nodes, repair suggestions
  Io/                     FFmpeg import/export
  Project/                project state, undo history
src/AudioStudio.App/      WPF UI
tests/AudioStudio.Tests/  detector and end-to-end suites
```
