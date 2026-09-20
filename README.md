# BilinguaFlow

BilinguaFlow is a local-first Windows desktop application for real-time English and
Japanese recognition, context-aware correction, and Simplified Chinese bilingual
subtitles. Milestone 2 provides local original-language transcription. Translation and
the LLM are intentionally not enabled yet.

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
  BilinguaFlow.Llm             Future local LLM adapter
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
4. **Transcript is delayed:** SenseVoice is utterance-based; recognition begins after end-of-speech silence or the 12-second maximum segment.

## Implemented through Milestone 2

- Resizable, DPI-aware English WPF interface with keyboard-accessible controls
- Movie, Meeting, and Microphone mode selection
- Active Windows input/output endpoint enumeration and refresh
- Microphone and endpoint loopback recording with independent level meters
- Collision-safe WAV recording, cancellation, serialized Start/Stop, and cleanup
- Structured logging and graceful capture/device error status
- Replaceable ASR/LLM contracts plus context-profile data model
- SenseVoiceSmall INT8 recognition through official sherpa-onnx C# bindings
- PCM 16/24/32-bit and float32 mono conversion plus 16 kHz resampling
- Configurable energy VAD (300 ms minimum, 650 ms endpoint, 12 second maximum)
- Independent Remote/Microphone ASR queues with final-result labels
- Scrollable 500-item transcript, automatic scrolling, timing and RTF diagnostics
- Missing-model/native-runtime validation and background-worker error isolation
- Hardware-independent tests for paths, preprocessing, VAD, models, results, and cancellation

## Roadmap

1. Application-specific WASAPI loopback
2. Qwen3-1.7B Q4 GGUF + llama.cpp integration
3. Context-aware ASR correction and Simplified Chinese translation
4. Transparent TopMost bilingual subtitle overlay
5. Persistent recording and transcript history
6. SRT, TXT, Markdown, and JSON export
7. Meeting summaries and action items
8. MSIX packaging and Microsoft Store preparation
