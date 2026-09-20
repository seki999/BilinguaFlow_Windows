# Architecture

The application uses dependency injection and MVVM. `BilinguaFlow.Core` defines
engine-neutral contracts and models. The WPF application depends only on those
contracts, while adapter projects implement operating-system and future AI details.

```text
Microphone ----> audio session ----> WAV + level events --+
                                                       UI
WASAPI loopback -> audio session ----> WAV + level events --+

Milestone 2: audio -> bounded Channel -> mono/16 kHz preprocessing
       -> energy VAD -> ISpeechRecognitionService -> original transcript UI

Future: transcript -> ILlmService (correction + zh-CN translation) -> bilingual subtitle UI
```

Each active source owns an independent capture object and WAV writer. The view model
serializes lifecycle transitions, refuses duplicate starts, and cancels/disposes all
sessions when Stop is pressed or the main window closes. Movie Mode uses ordinary
endpoint loopback. The `IAudioCaptureSessionFactory` boundary leaves room for a later
application-specific loopback implementation.

The ASR side has one transcription session per source. Meeting Mode therefore preserves
separate Remote and Microphone queues and labels. Both use one loaded SenseVoice
recognizer, with native decoding serialized on a background worker. The bounded queue
prevents memory growth under sustained overload and reports dropped chunks.
