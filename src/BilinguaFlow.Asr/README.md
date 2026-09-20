# SenseVoice ASR adapter

This project implements the engine-neutral Core speech contracts with the official
`org.k2fsa.sherpa.onnx` NuGet package. Its runtime packages copy the required native
libraries (including `sherpa-onnx-c-api.dll` and ONNX Runtime dependencies) into the
RID-specific application output automatically.

Pipeline:

```text
AudioChunk -> PCM/float decode -> mono -> 16 kHz linear resample
           -> energy VAD -> utterance -> SenseVoice offline decode
```

SenseVoice is an utterance/non-streaming model, so this adapter publishes final results
only. The recognizer is shared and serializes calls because native recognizer thread
safety is not assumed. Model files remain outside this project under
`models/sensevoice/`.
