# ASR adapter

Milestone 3 will add a `ISpeechRecognitionService` implementation, initially targeting
SenseVoiceSmall INT8 through sherpa-onnx. The Core project owns the contract so the
engine can later be replaced by whisper.cpp without changing the UI or pipeline.
