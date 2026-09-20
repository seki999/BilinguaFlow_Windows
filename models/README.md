# Local model files

Model binaries are intentionally not downloaded or committed.

Milestone 2 expects this layout:

```text
models/
  sensevoice/
    model.int8.onnx
    tokens.txt
  llm/qwen3-1.7b-q4/            # Future GGUF model for llama.cpp
```

Download the SenseVoiceSmall INT8 archive from the official sherpa-onnx model releases,
then copy only the model and token files above. The application never downloads models.
