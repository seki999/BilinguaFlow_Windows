# BilinguaFlow

BilinguaFlow is a local-first Windows desktop application for real-time English and
Japanese recognition, context-aware correction, and Simplified Chinese bilingual
subtitles. Milestone 3 adds an in-process Qwen/llama.cpp translation pipeline; no cloud
API or manually launched model server is used.

## Architecture

The solution uses WPF, MVVM, dependency injection, NAudio, WASAPI microphone capture,
and WASAPI loopback capture. A bounded producer/consumer pipeline converts captured
audio to mono 16 kHz, performs energy-based utterance segmentation, and runs SenseVoice
off the UI thread. Hardware and AI engines remain behind interfaces.
See [the architecture notes](docs/architecture.md).

## Solution structure

```text
src/
  BilinguaFlow.App             WPF UI, MVVM, composition root
  BilinguaFlow.Core            Domain models and service contracts
  BilinguaFlow.Audio           NAudio/WASAPI adapters
  BilinguaFlow.Asr             SenseVoice/sherpa-onnx adapter, preprocessing, VAD
  BilinguaFlow.Llm             Qwen/LLamaSharp adapter, prompts, parsing, bounded queue
  BilinguaFlow.Infrastructure  System services
tests/
  BilinguaFlow.Core.Tests      Hardware-independent tests
docs/                          Design documentation
models/                        Local model placement (binaries ignored)
```

## Requirements

- Windows 10 or Windows 11
- .NET 10 SDK
- An active playback endpoint for Movie/Meeting Mode
- Microphone permission and an active input endpoint for Microphone/Meeting Mode
- Windows x64 for the current packaged sherpa-onnx runtime

## Build and run

```powershell
dotnet restore BilinguaFlow.slnx
dotnet build BilinguaFlow.slnx
dotnet test BilinguaFlow.slnx
dotnet run --project src/BilinguaFlow.App/BilinguaFlow.App.csproj
```

Select a mode and the required endpoints, then press **Start**. Press **Stop** before
changing sources. WAV files are written under the application's `recordings` folder
using collision-safe names such as `2026-09-20_183000_system.wav`. Meeting Mode writes
separate system and microphone files. Closing the window stops and disposes all active
capture sessions. Output loopback can be silent until Windows is actually playing audio.

## SenseVoice Setup

Download the SenseVoiceSmall INT8 model from the official
[sherpa-onnx SenseVoice release documentation](https://k2-fsa.github.io/sherpa/onnx/pretrained_models/offline-ctc/sense-voice.html).
The application does not download or commit model binaries. Place the files exactly at:

```text
models/sensevoice/model.int8.onnx
models/sensevoice/tokens.txt
```

At startup BilinguaFlow searches upward from the application directory for
`models/sensevoice`. If either file is missing, the Status panel displays
`SenseVoice model not found.` and the absolute expected directory. The official
`org.k2fsa.sherpa.onnx` NuGet package supplies and copies the native Windows runtime.

To test Movie Mode, play English or Japanese audio and select the same output endpoint
used by Windows. To test Microphone Mode, select an active microphone and speak for at
least 300 ms, followed by a short pause. SenseVoice emits final utterances after roughly
650 ms of silence. Meeting Mode runs Remote and Microphone recognition separately.

### Troubleshooting

1. **No System Audio level:** select the same Windows playback device currently used by the movie.
2. **Audio level moves but no transcript:** verify both model files, speak/play a complete utterance, and inspect ASR diagnostics/logs.
3. **Native library error:** verify the application runs as x64 and that the sherpa-onnx runtime files were copied beside the build output.
4. **Transcript is delayed:** Movie Mode intentionally waits through 900 ms of endpoint
   silence and may hold short speech for up to a 1.2 second merge window. Recognition
   also starts at the 12-second maximum segment.

## ASR Engines

The **ASR Engine** selector is locked during capture and offers:

- **SenseVoice:** fast and lightweight, suited to minimum-latency transcription.
- **Whisper:** heavier, but generally better at complete movie/dialogue sentences and an important alternative for difficult Japanese audio.

The selected engine supplies the raw Japanese or English text to Qwen. Whisper translation
mode is never enabled; Simplified Chinese remains Qwen's responsibility. Enable developer
**ASR Compare Mode** to run the same primary audio segment through both engines. The two
results appear as separate transcript items with their engine and timing; only the selected
primary engine result is sent to Qwen.

## Whisper Setup

BilinguaFlow embeds whisper.cpp through the Whisper.net Windows CPU runtime. It does not
use Python, a server, or cloud ASR, and it never downloads a model. Obtain the multilingual
Whisper Small `ggml-small.bin` model from the official whisper.cpp model distribution and
place it exactly at:

```text
models/whisper/ggml-small.bin
```

Select **Whisper**, choose Japanese or English explicitly, then press **Start**. Japanese is
passed as `ja` and English as `en`; automatic language detection and Whisper translation are
disabled. Movie Mode gives Whisper more natural phrases: 1.5-second recognition minimum,
900 ms endpoint silence, and a conservative six-second activity fallback with a 12-second cap.

To compare a 30–60 second Japanese movie clip, select the same playback endpoint and run it
once with SenseVoice and once with Whisper. Compare kana, kanji, names, phrase boundaries,
RTF, and the downstream Chinese result. Compare Mode can also show both engines for each
selected-engine segment during one run.

### Whisper troubleshooting

1. **Whisper model not found:** verify the exact `models/whisper/ggml-small.bin` path.
2. **Native runtime failure:** use Windows x64, install the Visual C++ 2022 x64 runtime, and verify the Whisper.net runtime files were copied beside the build.
3. **Unsupported model:** use a whisper.cpp-compatible multilingual GGML model; English-only models cannot recognize Japanese.
4. **Slow recognition:** Whisper Small is CPU-intensive; use the default half-CPU thread setting or configure fewer threads to keep Windows responsive.
5. **SenseVoice works but Whisper does not:** stop capture, inspect the readable status/log error, correct the model/runtime issue, and select SenseVoice manually if needed.

## Qwen Setup

BilinguaFlow uses the local `Qwen3-1.7B Q4_K_M` GGUF model through LLamaSharp and its
packaged Windows CPU llama.cpp backend. The model is not downloaded or committed to Git.
Place it exactly at:

```text
models/qwen/Qwen_Qwen3-1.7B-Q4_K_M.gguf
```

Model discovery searches upward from the executable directory for that relative path.
Pressing **Start** loads Qwen once in the background and reuses it across capture sessions.
No PowerShell command, Ollama, LM Studio, HTTP service, or cloud connection is required.
If the model is absent or fails to load, the Status panel reports the expected path and
the app continues in ASR-only mode.

Select **General Movie** for Japanese or English films. Select **Japanese IT Meeting**
or **English IT Meeting** for technical meetings; these profiles preserve common cloud
and development terminology. **Additional Context** accepts a short description of the
current topic. **Show Raw ASR** controls presentation only: raw SenseVoice text is always
retained internally and is never overwritten. Pressing **Clear** clears both visible
items and the rolling five-utterance Qwen context without unloading either model.

### Qwen troubleshooting

1. **Qwen model not found:** confirm the exact file name and `models/qwen` directory.
2. **Native DLL load failure:** use Windows x64 and confirm the LLamaSharp CPU backend files are beside the executable.
3. **Translation queue grows:** reduce audio activity, shorten Additional Context, or lower context/output-token settings; the bounded queue drops the oldest pending translation while preserving raw ASR.
4. **ASR works but translation does not:** inspect Status and debug logs; Qwen failure is isolated and does not stop capture or SenseVoice.
5. **Invalid JSON output:** the raw transcript remains visible and the item shows `Translation error`; the raw Qwen output is logged.
6. **Translation is slow:** CPU generation depends on hardware. BilinguaFlow uses half the logical processors by default to keep Windows responsive.

## Implemented through Milestone 3

- Resizable, DPI-aware English WPF interface with keyboard-accessible controls
- Movie, Meeting, and Microphone mode selection
- Active Windows input/output endpoint enumeration and refresh
- Microphone and endpoint loopback recording with independent level meters
- Collision-safe WAV recording, cancellation, serialized Start/Stop, and cleanup
- Structured logging and graceful capture/device error status
- Replaceable ASR/LLM contracts plus context-profile data model
- SenseVoiceSmall INT8 recognition through official sherpa-onnx C# bindings
- PCM 16/24/32-bit and float32 mono conversion plus 16 kHz resampling
- Mode-specific configurable VAD profiles with adaptive noise floor and a basic
  zero-crossing speech heuristic
- Movie Mode stabilization: 500 ms minimum speech, 1.2 second recognition minimum,
  900 ms endpoint silence, 1.2 second merge window, 250 ms pre/post-roll, and a
  12 second maximum segment
- Short-utterance buffering, nearby-fragment merging, timeout flush, and Stop flush
- Independent Remote/Microphone ASR queues with final-result labels
- Scrollable 500-item transcript, automatic scrolling, timing and RTF diagnostics
- Missing-model/native-runtime validation and background-worker error isolation
- Hardware-independent tests for paths, preprocessing, VAD, models, results, and cancellation
- In-process Qwen3 GGUF loading through llama.cpp/LLamaSharp with deterministic, non-thinking prompts
- Single-pass correction plus Simplified Chinese translation with defensive JSON parsing
- Context profiles, editable additional context, and rolling recent-utterance context
- Bounded serialized LLM queue, overflow/failure isolation, sequence-stable bilingual UI, and latency diagnostics

## Roadmap

1. Application-specific WASAPI loopback
2. Transparent TopMost bilingual subtitle overlay
3. Persistent recording and transcript history
4. SRT, TXT, Markdown, and JSON export
5. Meeting summaries and action items
6. MSIX packaging and Microsoft Store preparation
