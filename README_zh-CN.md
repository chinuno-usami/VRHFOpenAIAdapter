# VRHandsFrame 1.5.5：OpenAI 兼容翻译、VLM OCR 与 SOCKS5 适配器

本补丁复用 VRHF 原有的 **Google/GAS 翻译渠道**和已经失效风险较高的 **Drive OCR 渠道**，分别转换成 OpenAI 兼容的文字及图片 `POST /chat/completions` 请求。出站连接可选择直连或 SOCKS5；无需常驻额外程序，也不需要修改 VRHF 界面。

## 使用方法

1. 编辑 `VRHandsFrame_Data/StreamingAssets/config/openai-translation.json`。
2. 翻译渠道选择 Google/GAS，即 `Translation.TranslationEngine = 0`。
3. VLM OCR 选择原 Drive API OCR，即 `Translation.TextRecognitionEngine = 1`，并保持 `Detail.IsVisionApiEnabled = false`。交付的 `global.json` 已设为该值。
4. 按原方式启动 VRHF。首次翻译或 OCR 时，适配器会在当前进程中启动一个仅绑定 `127.0.0.1` 的随机端口。

最少需要设置：

```json
{
  "BaseUrl": "https://api.openai.com/v1",
  "ApiKey": "sk-...",
  "ApiKeyEnvironmentVariable": "OPENAI_API_KEY",
  "Model": "gpt-4o-mini"
}
```

`BaseUrl` 既可填 API 根地址（适配器会追加 `/chat/completions`），也可直接填写完整的 `.../chat/completions` 地址。使用 Ollama、LM Studio、vLLM、LiteLLM 或其他兼容服务时，替换 `BaseUrl` 和 `Model` 即可；不要求鉴权的本地服务可保持 `ApiKey` 为空。

API key 的读取顺序是：先用 JSON 中的 `ApiKey`；为空时再读取 `ApiKeyEnvironmentVariable` 指定的环境变量。通过 Steam/SteamVR 启动时，进程必须能继承该环境变量；不确定时可直接填写 `ApiKey`，但应注意该文件是明文配置，不要分享。

## VLM OCR

OCR 和翻译共用 `BaseUrl`、API key、请求头及 SOCKS5，但可以选择不同模型：

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

`Vision.Model` 必须是该服务中真正支持图片输入的模型。适配器按 OpenAI Vision 格式发送 `image_url`，内容是 `data:image/png;base64,...`。提示词要求只抄录、不翻译，并以 `<NO_TEXT>` 表示无文字；该标记会被适配器转换为空结果。图片会上传到你配置的 API 服务，请根据画面隐私和服务条款决定是否启用。

VRHF 在调用 VLM 前仍会执行原有流程：在二值图中寻找四个边框角点，对原图进行透视裁剪，转为灰度 PNG，再交给 VLM。VLM 能提升复杂字体和多语言识别，但**无法修复边框角点检测失败**，也拿不到裁剪区域之外的文字。

## SOCKS5

在同一配置文件中启用：

```json
"Socks5": {
  "Enabled": true,
  "Host": "127.0.0.1",
  "Port": 1080,
  "Username": "",
  "Password": ""
}
```

支持无认证及用户名/密码认证。目标 API 域名由 SOCKS5 服务端解析，可避免本地 DNS 限制。

**代理范围：** SOCKS5 作用于本补丁发出的 OpenAI 翻译和 VLM OCR 请求；原有 DeepL、Azure、MS OCR、Google Cloud Vision 等不会经过它。

## 其他配置

- `SystemPrompt`：翻译提示词，支持 `{source}`、`{target}` 占位符。
- `Temperature`：默认 `0.0`。
- `TimeoutSeconds`：OpenAI 出站超时，范围 1–300 秒；VLM 较慢时可提高。补丁已把 VLM OCR 的本地 UnityWebRequest 等待上限单独提高到 300 秒。
- `MaxInputCharacters`：单次最大翻译输入字符数。
- `Vision.Detail`：OpenAI 图片细节参数，可选 `auto`、`low`、`high`。
- `Vision.MaxTokens`：OCR 输出 token 上限，范围 1–32768。
- `Vision.MaxImageBytes`：解码后 PNG 大小上限，范围 1KB–20MB。
- `Headers`：附加请求头，例如某些服务所需的 `api-key`。不能覆盖 `Host`、`Content-Length`、`Connection`、`Authorization`。
- `AllowInsecureTls`：默认 `false`。仅对自己信任的自签名内网服务临时设为 `true`；它会关闭该 API 连接的证书校验。
- `AllowInsecureHttp`：默认 `false`，只允许 HTTPS 或 `localhost`/回环 IP。访问可信内网的明文 HTTP 服务时才显式设为 `true`；SOCKS5 本身不加密请求。
- 环境变量 `VRHF_OPENAI_CONFIG`：可指定另一份配置文件的绝对路径。

## 排错

详细错误写入程序根目录的 `VRHFOpenAIAdapter.log`。适配器不会主动记录请求头或 API key，并会对上游错误正文中与已配置密钥完全相同的内容脱敏；但不受信任的服务仍可能用其他形式回显敏感数据，因此不要公开分享日志。常见问题：

- HTTP 401/403：API key 或自定义鉴权头错误。
- HTTP 404：`BaseUrl` 路径不正确；确认服务提供 OpenAI chat completions 兼容接口。
- API 报图片/`image_url` 不支持：`Vision.Model` 不是 VLM，或该兼容服务没有实现 OpenAI Vision 消息格式。
- 完全没有 OCR 请求日志：通常是四角检测/透视裁剪先失败；检查 VRHF 图片预览、边框是否完整，以及 `Detail.FrameColorThreshold`。
- MS OCR 几乎无结果：确认 `msocr.exe` 未被杀毒软件删除、源语言对应的 Windows OCR language pack 已安装，并检查截图中文字像素是否过小。内置 OCR 调用默认没有启用已有的 1024px 上采样。
- `SOCKS5 CONNECT failed`：代理拒绝目标地址，或代理类型/端口填写错误。
- VRHF 显示连接失败：先查看上述日志，其中会保留 API 返回状态和部分错误正文。

## 改动与回滚

新增运行文件：

- `VRHandsFrame_Data/Managed/VRHF.OpenAIAdapter.dll`
- `VRHandsFrame_Data/StreamingAssets/config/openai-translation.json`

`Assembly-CSharp.dll` 的 IL 改动：

- 将 `HandsFrameClient.WebClient.GetGASUrl()` 改为翻译适配器入口。
- 将 `PostToDriveOCR` 使用的 `DriveOCRUrl` 改为 VLM OCR 入口，并将 OCR 超时上限提高至 300 秒。
- 修复 VRChat OSC Chatbox 延迟：在 `VRHandsFrameVariableFrame.<TakeHandsFrame>d__35` 中，翻译结果写入 `OSCText` 时同步将 `OSCTimer` 设为 `10f`。这消除了原程序硬编码的 10 秒倒计时，使得翻译画面一显示，Chatbox 消息立即同步发送到 VRChat（超过 140 字符的长文本仍按 10 秒翻页）。
- MS OCR 和 Google Vision 分支保持不变。

原文件已备份为：

`VRHandsFrame_Data/Managed/Assembly-CSharp.dll.vrhf-original`

Windows 下运行 `VRHFOpenAIAdapter/restore-original.bat` 可恢复原 DLL 并删除适配器 DLL。若要再次应用补丁，需要在 macOS/Linux 开发环境安装 Mono，然后执行：

```bash
bash VRHFOpenAIAdapter/build-and-patch.sh
```

## 已完成的验证

- 适配器和补丁器可由 Mono C# 编译器成功构建。
- 补丁后的 `GetGASUrl()` 调用 `Bridge.GetUrl()`；`PostToDriveOCR` 的 URL 加载调用 `Bridge.GetOcrUrl()`。
- 使用本地伪 OpenAI 服务完成了翻译回归，以及 PNG Base64 → Vision `image_url` → OCR 文本 → 原 Drive 响应格式的端到端测试。
- 验证了 VLM 模型选择、语言提示、Markdown 围栏清理和 `<NO_TEXT>` 处理。
- 使用带用户名/密码的本地 SOCKS5 服务完成了 VLM OCR 远端域名转发测试。

当前开发环境是 macOS，无法在这里启动 Windows SteamVR/VRHF 图形程序；最终仍需在实际 Windows VR 环境分别做一次截图 OCR 和翻译确认。
