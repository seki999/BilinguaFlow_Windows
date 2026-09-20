# Local LLM adapter

This project loads Qwen3-1.7B Q4_K_M directly in-process through LLamaSharp's CPU
llama.cpp backend. It owns prompt construction, non-thinking single-pass correction and
translation, defensive JSON parsing, and a bounded serialized translation worker.
