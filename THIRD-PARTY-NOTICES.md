# Third-party notices

## Shipped in the package

| Component | License | Use |
|---|---|---|
| [Whisper.net](https://github.com/sandrohanea/whisper.net) 1.9.1 | MIT | .NET bindings for whisper.cpp. |
| [Microsoft.Extensions.AI.Abstractions](https://github.com/dotnet/extensions) | MIT | A dependency of Whisper.net. |
| [sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx) 1.13.8 (.NET bindings) | Apache-2.0 | Runs Spleeter with ONNX Runtime. |

## Downloaded when the administrator sets the extension up

Checked against the SHA-256 written in `Components.cs` before use:

| Component | License | From |
|---|---|---|
| whisper.cpp native libraries (`Whisper.net.Runtime`, `Whisper.net.Runtime.NoAvx` 1.9.1) | MIT | nuget.org |
| sherpa-onnx and [ONNX Runtime](https://github.com/microsoft/onnxruntime) native libraries (`org.k2fsa.sherpa.onnx.runtime.<platform>` 1.13.8) | Apache-2.0 and MIT | nuget.org |
| [Whisper](https://github.com/openai/whisper) small, in whisper.cpp's format (`ggml-small-q8_0.bin`, `ggml-small-q5_1.bin`) | MIT (OpenAI) | huggingface.co/ggerganov/whisper.cpp |
| [Spleeter](https://github.com/deezer/spleeter) 2-stems, exported to ONNX by the sherpa-onnx project (`vocals.fp16.onnx`, `accompaniment.fp16.onnx`) | MIT (Deezer) | huggingface.co/csukuangfj/sherpa-onnx-spleeter-2stems-fp16 |

Arcanist's ffmpeg (found or installed by Arcanist) decodes the songs; it runs as a separate program.

Not used, for their licenses: Demucs's weights (for research), UVR's MDX-Net weights (no license) and Open-Unmix's
UMX-L (CC BY-NC-SA).
