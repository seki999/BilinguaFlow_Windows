# BilinguaFlow

BilinguaFlow is a local-first Windows desktop application for real-time English and
Japanese recognition, context-aware correction, and Simplified Chinese bilingual
subtitles. Milestone 1 establishes the audio and application foundation; it does not
yet run speech recognition or an LLM.

## Architecture

The solution uses WPF, MVVM, dependency injection, NAudio, WASAPI microphone capture,
and WASAPI loopback capture. Hardware and AI engines are behind interfaces so the UI
is not coupled to NAudio, SenseVoice, sherpa-onnx, whisper.cpp, Qwen, or llama.cpp.
See [the architecture notes](docs/architecture.md).

## Solution structure

```text
src/
  BilinguaFlow.App             WPF UI, MVVM, composition root
  BilinguaFlow.Core            Domain models and service contracts
  BilinguaFlow.Audio           NAudio/WASAPI adapters
  BilinguaFlow.Asr             Future ASR adapter
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

## Implemented in Milestone 1

- Resizable, DPI-aware English WPF interface with keyboard-accessible controls
- Movie, Meeting, and Microphone mode selection
- Active Windows input/output endpoint enumeration and refresh
- Microphone and endpoint loopback recording with independent level meters
- Collision-safe WAV recording, cancellation, serialized Start/Stop, and cleanup
- Structured logging and graceful capture/device error status
- Replaceable ASR/LLM contracts plus context-profile data model
- Hardware-independent tests for recording-path behavior

## Roadmap

1. Application-specific WASAPI loopback
2. VAD and replaceable ASR integration (SenseVoiceSmall INT8 + sherpa-onnx first)
3. Real-time original-language subtitles
4. Qwen3-1.7B Q4 GGUF + llama.cpp integration
5. Context-aware ASR correction and Simplified Chinese translation
6. Transparent TopMost bilingual subtitle overlay
7. Meeting Mode remote/microphone processing pipelines
8. Recording and transcript history
9. SRT, TXT, Markdown, and JSON export
10. Meeting summaries and action items
11. MSIX packaging and Microsoft Store preparation
