# VRHandsFrame 1.5.5: OpenAI-Compatible Translation, VLM OCR & SOCKS5 Adapter

This adapter reuses VRHandsFrame's existing **Google/GAS translation** and **Drive OCR** code paths and redirects them to OpenAI-compatible `POST /chat/completions` endpoints for text and vision. Outbound traffic can go direct or through SOCKS5. No extra daemon is required, and the VRHF UI is unchanged.

## Requirements

- A **VRHandsFrame 1.5.5** installation (Steam or an unmodified copy of the game folder).
- For building/patching: **Mono** with `mcs` and **Mono.Cecil 0.11** (macOS/Linux development machine, or Windows with Mono installed).
- An OpenAI-compatible API (OpenAI, Ollama, LM Studio, vLLM, LiteLLM, etc.) for translation and/or VLM OCR.

Expected folder layout after cloning or copying this repo:

```text
VRHandsFrame_v1.5.5/
├── VRHandsFrame.exe
├── VRHandsFrame_Data/
│   ├── Managed/
│   │   └── Assembly-CSharp.dll          # patched in place
│   └── StreamingAssets/
│       └── config/
│           ├── global.json              # VRHF settings (edit)
│           └── openai-translation.json  # adapter settings (copy from this repo)
└── VRHFOpenAIAdapter/                   # this repository
    ├── build-and-patch.sh
    ├── openai-translation.json
    ├── PatchAssembly.cs
    ├── restore-original.bat
    └── VRHFOpenAIAdapter.cs
```

Place `VRHFOpenAIAdapter/` **next to** `VRHandsFrame.exe`, not inside `VRHandsFrame_Data`.

---

## Applying the Patch to Original VRHF

These steps assume you start from an **unpatched** VRHandsFrame install (no `Assembly-CSharp.dll.vrhf-original` backup yet, or you have restored the original DLL first — see [Rollback](#rollback)).

### Step 1 — Install the adapter config file

Copy the sample config into VRHF's config directory:

```bash
cp VRHFOpenAIAdapter/openai-translation.json \
   VRHandsFrame_Data/StreamingAssets/config/openai-translation.json
```

On Windows, copy `VRHFOpenAIAdapter\openai-translation.json` to `VRHandsFrame_Data\StreamingAssets\config\openai-translation.json`.

Edit that file and set at least `BaseUrl`, `ApiKey` (or `ApiKeyEnvironmentVariable`), and `Model`. See [Adapter configuration](#adapter-configuration) below.

### Step 2 — Build the adapter DLL and patch `Assembly-CSharp.dll`

From the VRHandsFrame root directory (the folder that contains `VRHandsFrame.exe`):

```bash
bash VRHFOpenAIAdapter/build-and-patch.sh
```

The script will:

1. Compile `VRHF.OpenAIAdapter.dll` into `VRHandsFrame_Data/Managed/`.
2. Compile `PatchAssembly.exe` into `VRHFOpenAIAdapter/build/`.
3. Patch `VRHandsFrame_Data/Managed/Assembly-CSharp.dll` using Mono.Cecil.

On first run, the patcher creates a backup:

```text
VRHandsFrame_Data/Managed/Assembly-CSharp.dll.vrhf-original
```

**Mono on macOS (Homebrew):**

```bash
brew install mono
```

If the script reports that Mono.Cecil 0.11 was not found, install Mono and ensure the GAC contains `Mono.Cecil.dll`.

**Re-running the script** is safe: if the assembly is already patched, it exits without changes.

### Step 3 — Configure VRHF engine settings

In `VRHandsFrame_Data/StreamingAssets/config/global.json`, set the translation and OCR engines so VRHF routes traffic through the patched hooks:

| Setting | Value | Meaning |
|---------|-------|---------|
| `Translation.TranslationEngine` | `0` | Google/GAS (patched → OpenAI adapter) |
| `Translation.TextRecognitionEngine` | `1` | Drive API OCR (patched → VLM OCR adapter) |
| `Detail.IsVisionApiEnabled` | `false` | Keep legacy Google Cloud Vision off |

Example excerpt:

```json
"Translation": {
  "TextRecognitionEngine": 1,
  "TranslationEngine": 0,
  "GASKey": ""
},
"Detail": {
  "IsVisionApiEnabled": false
}
```

You can change these in VRHF's in-app settings as long as the same engine IDs are selected.

### Step 4 — Launch VRHF

Start VRHandsFrame as usual (Steam, `launch.bat`, or `VRHandsFrame.exe`). On the first translation or OCR request, the adapter starts a loopback-only HTTP listener on a random port inside the VRHF process.

---

## Adapter configuration

Config file path (default):

```text
VRHandsFrame_Data/StreamingAssets/config/openai-translation.json
```

Minimum settings:

```json
{
  "BaseUrl": "https://api.openai.com/v1",
  "ApiKey": "sk-...",
  "ApiKeyEnvironmentVariable": "OPENAI_API_KEY",
  "Model": "gpt-4o-mini"
}
```

- **`BaseUrl`** — API root (the adapter appends `/chat/completions`) or a full `.../chat/completions` URL.
- **`ApiKey`** — Used first. If empty, the adapter reads the environment variable named by **`ApiKeyEnvironmentVariable`** (default `OPENAI_API_KEY`). When launching via Steam/SteamVR, ensure the variable is inherited, or put the key directly in JSON (plaintext — do not share this file).
- **`Model`** — Chat model for translation.

For local services (Ollama, LM Studio, etc.), set `BaseUrl` and `Model` accordingly; `ApiKey` may be left empty if the server does not require auth.

Override config location with environment variable:

```text
VRHF_OPENAI_CONFIG=<absolute path to another json file>
```

---

## VLM OCR

OCR shares `BaseUrl`, API key, headers, and SOCKS5 with translation, but can use a different model:

```json
"Vision": {
  "Enabled": true,
  "Model": "gpt-4o-mini",
  "Prompt": "Transcribe all readable text in the image exactly...",
  "Detail": "high",
  "MaxTokens": 2048,
  "MaxImageBytes": 10485760
}
```

- **`Vision.Model`** must support image input on your provider.
- Images are sent as OpenAI-style `image_url` with `data:image/png;base64,...`.
- The prompt asks for transcription only; `<NO_TEXT>` is converted to an empty result.
- **Privacy:** cropped screenshot PNGs are uploaded to your configured API.

VRHF still runs its original pipeline before VLM: detect four frame corners, perspective-crop, convert to grayscale PNG, then call OCR. VLM improves recognition quality but **cannot fix failed corner detection** and does not see text outside the cropped region.

---

## SOCKS5 proxy

Enable in the same JSON file:

```json
"Socks5": {
  "Enabled": true,
  "Host": "127.0.0.1",
  "Port": 1080,
  "Username": "",
  "Password": ""
}
```

Supports no-auth and username/password. The API hostname is resolved by the proxy (useful when local DNS is restricted).

**Scope:** SOCKS5 applies only to OpenAI translation and VLM OCR requests from this adapter. DeepL, Azure, MS OCR, Google Cloud Vision, etc. are unchanged.

---

## Other options

| Key | Description |
|-----|-------------|
| `SystemPrompt` | Translation prompt; supports `{source}` and `{target}` placeholders. |
| `Temperature` | Default `0.0`. |
| `TimeoutSeconds` | Outbound API timeout, 1–300 s. VLM OCR local wait is raised to 300 s by the patch. |
| `MaxInputCharacters` | Maximum characters per translation request. |
| `Vision.Detail` | OpenAI image detail: `auto`, `low`, or `high`. |
| `Vision.MaxTokens` | OCR output token limit, 1–32768. |
| `Vision.MaxImageBytes` | Decoded PNG size limit, 1 KB–20 MB. |
| `Headers` | Extra HTTP headers (e.g. Azure `api-key`). Cannot override `Host`, `Content-Length`, `Connection`, or `Authorization`. |
| `AllowInsecureTls` | Default `false`. Set `true` only for trusted self-signed HTTPS endpoints. |
| `AllowInsecureHttp` | Default `false`. Allows non-loopback `http://` when set `true`. SOCKS5 does not encrypt the HTTP payload. |

---

## Troubleshooting

Detailed errors are written to:

```text
<VRHandsFrame root>/VRHFOpenAIAdapter.log
```

The adapter does not log request headers or API keys and redacts configured secrets in upstream error bodies. Do not share logs publicly if an untrusted service might echo sensitive data differently.

| Symptom | Likely cause |
|---------|----------------|
| HTTP 401/403 | Wrong API key or custom auth header. |
| HTTP 404 | Incorrect `BaseUrl`; confirm OpenAI-compatible chat completions path. |
| Image / `image_url` not supported | `Vision.Model` is not a VLM, or the server lacks OpenAI Vision message format. |
| No OCR requests in log | Corner detection / perspective crop failed; check frame visibility and `Detail.FrameColorThreshold`. |
| MS OCR poor results | Unrelated if using patched Drive OCR (`TextRecognitionEngine = 1`); for MS OCR (`0`), check language packs and `msocr.exe`. |
| `SOCKS5 CONNECT failed` | Proxy rejected target or wrong host/port/type. |
| VRHF shows connection error | Check `VRHFOpenAIAdapter.log` for HTTP status and error body. |

---

## Rollback

The patcher backs up the original game assembly:

```text
VRHandsFrame_Data/Managed/Assembly-CSharp.dll.vrhf-original
```

**Windows** — from the VRHandsFrame root:

```bat
VRHFOpenAIAdapter\restore-original.bat
```

This restores `Assembly-CSharp.dll` from the backup and deletes `VRHF.OpenAIAdapter.dll`.

To apply the patch again after rollback, re-run:

```bash
bash VRHFOpenAIAdapter/build-and-patch.sh
```

Removing the adapter config file is optional; VRHF ignores it when the DLL is not loaded.

---

## What the patch changes

**Files added at runtime:**

| File | Purpose |
|------|---------|
| `VRHandsFrame_Data/Managed/VRHF.OpenAIAdapter.dll` | Loopback server + OpenAI client |
| `VRHandsFrame_Data/StreamingAssets/config/openai-translation.json` | Adapter settings (you copy/edit) |

**IL modifications in `Assembly-CSharp.dll`:**

- `HandsFrameClient.WebClient.GetGASUrl()` → calls `VRHF.OpenAIAdapter.Bridge.GetUrl()` (translation).
- `PostToDriveOCR` state machine → loads URL from `Bridge.GetOcrUrl()` instead of `HandsFrameConstants.DriveOCRUrl`; OCR UnityWebRequest timeout set to 300 s.
- MS OCR and Google Vision code paths are **not** modified.

---

## Development notes

- Build toolchain: Mono `mcs` (C# 7, .NET 4.7.2), references `Newtonsoft.Json.dll` and `netstandard.dll` from VRHF's `Managed` folder.
- Patcher source: `PatchAssembly.cs` (Mono.Cecil).
- Adapter source: `VRHFOpenAIAdapter.cs`.
- Final validation should be done on a Windows machine with SteamVR: one screenshot OCR run and one translation run against your chosen API.

See also: [README_zh-CN.md](README_zh-CN.md) (Chinese documentation).
