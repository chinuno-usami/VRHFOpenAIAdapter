using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace VRHF.OpenAIAdapter
{
    public static class Bridge
    {
        private static readonly object Sync = new object();
        private static LocalTranslationServer server;

        public static string GetUrl()
        {
            lock (Sync)
            {
                EnsureServer();
                return server.Url;
            }
        }

        public static string GetOcrUrl()
        {
            lock (Sync)
            {
                EnsureServer();
                return server.OcrUrl;
            }
        }

        private static void EnsureServer()
        {
            if (server != null) return;
            server = new LocalTranslationServer();
            server.Start();
            Log.Info("OpenAI adapter listening at " + server.Url + " and " + server.OcrUrl);
        }
    }

    internal sealed class LocalTranslationServer
    {
        private const int MaximumRequestBytes = 32 * 1024 * 1024;
        private TcpListener listener;
        private Thread acceptThread;

        public string Url { get; private set; }
        public string OcrUrl { get; private set; }

        public void Start()
        {
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            string baseUrl = "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture);
            Url = baseUrl + "/translate";
            OcrUrl = baseUrl + "/ocr";
            acceptThread = new Thread(AcceptLoop);
            acceptThread.IsBackground = true;
            acceptThread.Name = "VRHF OpenAI adapter";
            acceptThread.Start();
        }

        private void AcceptLoop()
        {
            while (true)
            {
                try
                {
                    TcpClient client = listener.AcceptTcpClient();
                    ThreadPool.QueueUserWorkItem(delegate { Handle(client); });
                }
                catch (Exception ex)
                {
                    Log.Error("Local adapter stopped: " + ex.Message);
                    return;
                }
            }
        }

        private static void Handle(TcpClient client)
        {
            using (client)
            {
                NetworkStream stream = null;
                try
                {
                    client.ReceiveTimeout = 30000;
                    client.SendTimeout = 30000;
                    stream = client.GetStream();
                    string requestLine = HttpWire.ReadAsciiLine(stream, 8192);
                    if (String.IsNullOrEmpty(requestLine))
                        return;

                    string[] requestParts = requestLine.Split(' ');
                    if (requestParts.Length < 2)
                        throw new InvalidDataException("Malformed local HTTP request line.");

                    Dictionary<string, string> headers = HttpWire.ReadHeaders(stream);
                    if (requestParts[0] == "GET" && requestParts[1] == "/health")
                    {
                        HttpWire.WriteResponse(stream, 200, "OK", "text/plain; charset=utf-8");
                        return;
                    }
                    if (requestParts[0] != "POST" || (requestParts[1] != "/translate" && requestParts[1] != "/ocr"))
                    {
                        HttpWire.WriteResponse(stream, 404, "Not found", "text/plain; charset=utf-8");
                        return;
                    }

                    string lengthText;
                    int contentLength;
                    if (!headers.TryGetValue("content-length", out lengthText) ||
                        !Int32.TryParse(lengthText, NumberStyles.None, CultureInfo.InvariantCulture, out contentLength) ||
                        contentLength < 0 || contentLength > MaximumRequestBytes)
                        throw new InvalidDataException("Missing or invalid Content-Length.");

                    string body = Encoding.UTF8.GetString(HttpWire.ReadExactly(stream, contentLength));
                    JObject request = JObject.Parse(body);
                    AdapterConfig config = AdapterConfig.Load();

                    if (requestParts[1] == "/ocr")
                    {
                        string encodedImage = JsonString(request, "encodedImage");
                        string language = JsonString(request, "language");
                        byte[] imageBytes;
                        try { imageBytes = Convert.FromBase64String(encodedImage); }
                        catch (FormatException) { throw new InvalidDataException("OCR image is not valid Base64."); }
                        if (imageBytes.Length == 0 || imageBytes.Length > config.Vision.MaxImageBytes)
                            throw new InvalidDataException("OCR image size is invalid or exceeds Vision.MaxImageBytes.");
                        if (!IsPng(imageBytes))
                            throw new InvalidDataException("OCR image is not a PNG file.");

                        string recognized = OpenAIClient.RecognizeText(config, encodedImage, language);
                        HttpWire.WriteResponse(stream, 200, WrapDriveOcrResponse(recognized), "text/plain; charset=utf-8");
                        return;
                    }

                    string text = JsonString(request, "text");
                    string source = JsonString(request, "source");
                    string target = JsonString(request, "target");
                    if (String.IsNullOrEmpty(text))
                        throw new InvalidDataException("Translation text is empty.");
                    if (text.Length > config.MaxInputCharacters)
                        throw new InvalidDataException("Input exceeds MaxInputCharacters (" + config.MaxInputCharacters + ").");

                    string translated = OpenAIClient.Translate(config, text, source, target);
                    HttpWire.WriteResponse(stream, 200, translated, "text/plain; charset=utf-8");
                }
                catch (Exception ex)
                {
                    Log.Error("Adapter request failed: " + ex.Message);
                    try
                    {
                        if (stream != null)
                            HttpWire.WriteResponse(stream, 502, "OpenAI adapter error: " + ex.Message, "text/plain; charset=utf-8");
                    }
                    catch { }
                }
                finally
                {
                    if (stream != null) stream.Dispose();
                }
            }
        }

        private static string JsonString(JObject value, string name)
        {
            JToken token = value[name];
            return token == null || token.Type == JTokenType.Null ? String.Empty : token.ToString();
        }

        private static bool IsPng(byte[] image)
        {
            byte[] signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
            if (image.Length < signature.Length) return false;
            for (int index = 0; index < signature.Length; index++)
                if (image[index] != signature[index]) return false;
            return true;
        }

        private static string WrapDriveOcrResponse(string text)
        {
            string normalized = (text ?? String.Empty).Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");
            return "________________\r\n\r\n" + normalized + "\r\n--batch";
        }
    }

    internal sealed class AdapterConfig
    {
        public string BaseUrl;
        public string ApiKey;
        public string Model;
        public string SystemPrompt;
        public double Temperature;
        public int TimeoutSeconds;
        public int MaxInputCharacters;
        public bool AllowInsecureTls;
        public bool AllowInsecureHttp;
        public Socks5Config Socks5;
        public Dictionary<string, string> Headers;
        public VisionConfig Vision;

        public static AdapterConfig Load()
        {
            string path = Environment.GetEnvironmentVariable("VRHF_OPENAI_CONFIG");
            if (String.IsNullOrEmpty(path))
            {
                path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    Path.Combine("VRHandsFrame_Data", Path.Combine("StreamingAssets", Path.Combine("config", "openai-translation.json"))));
            }
            if (!File.Exists(path))
                throw new FileNotFoundException("OpenAI adapter config not found", path);

            JObject json = JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
            AdapterConfig result = new AdapterConfig();
            result.BaseUrl = RequiredString(json, "BaseUrl");
            result.ApiKey = OptionalString(json, "ApiKey", String.Empty);
            string apiKeyVariable = OptionalString(json, "ApiKeyEnvironmentVariable", "OPENAI_API_KEY");
            if (String.IsNullOrEmpty(result.ApiKey) && !String.IsNullOrEmpty(apiKeyVariable))
                result.ApiKey = Environment.GetEnvironmentVariable(apiKeyVariable) ?? String.Empty;
            result.Model = RequiredString(json, "Model");
            result.SystemPrompt = OptionalString(json, "SystemPrompt",
                "Translate the user's text from {source} to {target}. Return only the translated text. Preserve meaning, tone, formatting, and line breaks.");
            result.Temperature = OptionalDouble(json, "Temperature", 0.0);
            result.TimeoutSeconds = OptionalInt(json, "TimeoutSeconds", 25, 1, 300);
            result.MaxInputCharacters = OptionalInt(json, "MaxInputCharacters", 12000, 1, 1000000);
            result.AllowInsecureTls = OptionalBool(json, "AllowInsecureTls", false);
            result.AllowInsecureHttp = OptionalBool(json, "AllowInsecureHttp", false);
            result.Socks5 = Socks5Config.FromJson(json["Socks5"] as JObject);
            result.Vision = VisionConfig.FromJson(json["Vision"] as JObject, result.Model);
            result.Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            JObject headers = json["Headers"] as JObject;
            if (headers != null)
            {
                foreach (JProperty property in headers.Properties())
                    result.Headers[property.Name] = property.Value.Type == JTokenType.Null ? String.Empty : property.Value.ToString();
            }
            return result;
        }

        private static string RequiredString(JObject json, string name)
        {
            string value = OptionalString(json, name, String.Empty);
            if (String.IsNullOrWhiteSpace(value))
                throw new InvalidDataException(name + " is required in openai-translation.json.");
            return value.Trim();
        }

        private static string OptionalString(JObject json, string name, string fallback)
        {
            JToken token = json[name];
            return token == null || token.Type == JTokenType.Null ? fallback : token.ToString();
        }

        private static int OptionalInt(JObject json, string name, int fallback, int minimum, int maximum)
        {
            JToken token = json[name];
            int value;
            if (token == null || !Int32.TryParse(token.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                value = fallback;
            if (value < minimum || value > maximum)
                throw new InvalidDataException(name + " must be between " + minimum + " and " + maximum + ".");
            return value;
        }

        private static double OptionalDouble(JObject json, string name, double fallback)
        {
            JToken token = json[name];
            double value;
            return token != null && Double.TryParse(token.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) ? value : fallback;
        }

        private static bool OptionalBool(JObject json, string name, bool fallback)
        {
            JToken token = json[name];
            bool value;
            return token != null && Boolean.TryParse(token.ToString(), out value) ? value : fallback;
        }
    }

    internal sealed class VisionConfig
    {
        public bool Enabled;
        public string Model;
        public string Prompt;
        public string Detail;
        public int MaxTokens;
        public int MaxImageBytes;

        public static VisionConfig FromJson(JObject json, string fallbackModel)
        {
            VisionConfig result = new VisionConfig();
            result.Enabled = json == null || json.Value<bool?>("Enabled").GetValueOrDefault(true);
            result.Model = json == null ? fallbackModel : (json.Value<string>("Model") ?? fallbackModel);
            if (String.IsNullOrWhiteSpace(result.Model)) result.Model = fallbackModel;
            result.Prompt = json == null ? null : json.Value<string>("Prompt");
            if (String.IsNullOrWhiteSpace(result.Prompt))
                result.Prompt = "Transcribe all readable text in the image exactly. Preserve reading order and line breaks. Do not translate, explain, summarize, or add Markdown. If there is no readable text, return exactly <NO_TEXT>.";
            result.Detail = json == null ? "high" : (json.Value<string>("Detail") ?? "high").ToLowerInvariant();
            if (result.Detail != "auto" && result.Detail != "low" && result.Detail != "high")
                throw new InvalidDataException("Vision.Detail must be auto, low, or high.");
            result.MaxTokens = json == null ? 2048 : json.Value<int?>("MaxTokens").GetValueOrDefault(2048);
            result.MaxImageBytes = json == null ? 10 * 1024 * 1024 : json.Value<int?>("MaxImageBytes").GetValueOrDefault(10 * 1024 * 1024);
            if (result.MaxTokens < 1 || result.MaxTokens > 32768)
                throw new InvalidDataException("Vision.MaxTokens must be between 1 and 32768.");
            if (result.MaxImageBytes < 1024 || result.MaxImageBytes > 20 * 1024 * 1024)
                throw new InvalidDataException("Vision.MaxImageBytes must be between 1024 and 20971520.");
            return result;
        }
    }

    internal sealed class Socks5Config
    {
        public bool Enabled;
        public string Host;
        public int Port;
        public string Username;
        public string Password;

        public static Socks5Config FromJson(JObject json)
        {
            Socks5Config result = new Socks5Config();
            result.Enabled = json != null && json.Value<bool?>("Enabled").GetValueOrDefault(false);
            result.Host = json == null ? "127.0.0.1" : (json.Value<string>("Host") ?? "127.0.0.1");
            result.Port = json == null ? 1080 : json.Value<int?>("Port").GetValueOrDefault(1080);
            result.Username = json == null ? String.Empty : (json.Value<string>("Username") ?? String.Empty);
            result.Password = json == null ? String.Empty : (json.Value<string>("Password") ?? String.Empty);
            if (result.Enabled && (String.IsNullOrWhiteSpace(result.Host) || result.Port < 1 || result.Port > 65535))
                throw new InvalidDataException("Socks5 Host/Port is invalid.");
            return result;
        }
    }

    internal static class OpenAIClient
    {
        public static string Translate(AdapterConfig config, string text, string source, string target)
        {
            Uri endpoint = BuildEndpoint(config.BaseUrl);
            if (endpoint.Scheme == "http" && !config.AllowInsecureHttp && !IsLoopbackHost(endpoint.Host))
                throw new InvalidOperationException("Remote plaintext HTTP is disabled. Use HTTPS or explicitly set AllowInsecureHttp to true.");
            string sourceName = String.IsNullOrWhiteSpace(source) ? "auto-detected language" : source;
            string targetName = String.IsNullOrWhiteSpace(target) ? "the requested target language" : target;
            string prompt = config.SystemPrompt.Replace("{source}", sourceName).Replace("{target}", targetName);

            JObject payload = new JObject();
            payload["model"] = config.Model;
            payload["temperature"] = config.Temperature;
            payload["stream"] = false;
            payload["messages"] = new JArray(
                new JObject(new JProperty("role", "system"), new JProperty("content", prompt)),
                new JObject(new JProperty("role", "user"), new JProperty("content", text)));

            string result = Complete(config, endpoint, payload);
            if (String.IsNullOrWhiteSpace(result))
                throw new InvalidDataException("API response contains no translated text.");
            return result.Trim();
        }

        public static string RecognizeText(AdapterConfig config, string encodedPng, string language)
        {
            if (!config.Vision.Enabled)
                throw new InvalidOperationException("VLM OCR is disabled in Vision.Enabled.");
            Uri endpoint = BuildEndpoint(config.BaseUrl);
            if (endpoint.Scheme == "http" && !config.AllowInsecureHttp && !IsLoopbackHost(endpoint.Host))
                throw new InvalidOperationException("Remote plaintext HTTP is disabled. Use HTTPS or explicitly set AllowInsecureHttp to true.");

            string hint = String.IsNullOrWhiteSpace(language) ? "unknown" : language;
            JArray userContent = new JArray(
                new JObject(new JProperty("type", "text"), new JProperty("text", "Expected language hint: " + hint)),
                new JObject(new JProperty("type", "image_url"),
                    new JProperty("image_url", new JObject(
                        new JProperty("url", "data:image/png;base64," + encodedPng),
                        new JProperty("detail", config.Vision.Detail)))));

            JObject payload = new JObject();
            payload["model"] = config.Vision.Model;
            payload["temperature"] = 0.0;
            payload["stream"] = false;
            payload["max_tokens"] = config.Vision.MaxTokens;
            payload["messages"] = new JArray(
                new JObject(new JProperty("role", "system"), new JProperty("content", config.Vision.Prompt)),
                new JObject(new JProperty("role", "user"), new JProperty("content", userContent)));

            string result = Complete(config, endpoint, payload).Trim();
            if (result.Equals("<NO_TEXT>", StringComparison.OrdinalIgnoreCase)) return String.Empty;
            if (result.StartsWith("```", StringComparison.Ordinal) && result.EndsWith("```", StringComparison.Ordinal))
            {
                int firstLine = result.IndexOf('\n');
                if (firstLine >= 0) result = result.Substring(firstLine + 1, result.Length - firstLine - 4).Trim();
            }
            return result;
        }

        private static string Complete(AdapterConfig config, Uri endpoint, JObject payload)
        {
            byte[] requestBody = Encoding.UTF8.GetBytes(payload.ToString(Formatting.None));
            HttpResponse response = Send(config, endpoint, requestBody);
            if (response.StatusCode < 200 || response.StatusCode >= 300)
                throw new InvalidOperationException("API returned HTTP " + response.StatusCode + ": " + Redact(Limit(response.Body, 2000), config));

            JObject json;
            try { json = JObject.Parse(response.Body); }
            catch (Exception ex) { throw new InvalidDataException("API response is not valid JSON: " + ex.Message); }
            JArray choices = json["choices"] as JArray;
            if (choices == null || choices.Count == 0)
                throw new InvalidDataException("API response contains no choices.");
            JToken content = choices[0]["message"] == null ? null : choices[0]["message"]["content"];
            if (content == null) content = choices[0]["text"];
            return ExtractContent(content);
        }

        private static Uri BuildEndpoint(string baseUrl)
        {
            Uri uri;
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
                throw new InvalidDataException("BaseUrl must be an absolute HTTP or HTTPS URL.");
            if (uri.AbsolutePath.TrimEnd('/').EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
                return uri;
            UriBuilder builder = new UriBuilder(uri);
            builder.Path = builder.Path.TrimEnd('/') + "/chat/completions";
            return builder.Uri;
        }

        private static bool IsLoopbackHost(string host)
        {
            if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
            IPAddress address;
            return IPAddress.TryParse(host, out address) && IPAddress.IsLoopback(address);
        }

        private static string Redact(string value, AdapterConfig config)
        {
            string result = value ?? String.Empty;
            if (!String.IsNullOrEmpty(config.ApiKey)) result = result.Replace(config.ApiKey, "[REDACTED]");
            foreach (string secret in config.Headers.Values)
                if (!String.IsNullOrEmpty(secret)) result = result.Replace(secret, "[REDACTED]");
            return result;
        }

        private static HttpResponse Send(AdapterConfig config, Uri endpoint, byte[] body)
        {
            int port = endpoint.IsDefaultPort ? (endpoint.Scheme == "https" ? 443 : 80) : endpoint.Port;
            TcpClient client;
            if (config.Socks5.Enabled)
                client = Socks5.Connect(config.Socks5, endpoint.Host, port, config.TimeoutSeconds);
            else
                client = Connect(endpoint.Host, port, config.TimeoutSeconds);

            using (client)
            {
                client.ReceiveTimeout = config.TimeoutSeconds * 1000;
                client.SendTimeout = config.TimeoutSeconds * 1000;
                Stream stream = client.GetStream();
                if (endpoint.Scheme == "https")
                {
                    RemoteCertificateValidationCallback callback = config.AllowInsecureTls
                        ? new RemoteCertificateValidationCallback(delegate { return true; })
                        : null;
                    SslStream ssl = new SslStream(stream, false, callback);
                    ssl.AuthenticateAsClient(endpoint.Host);
                    stream = ssl;
                }

                using (stream)
                {
                    StringBuilder request = new StringBuilder();
                    request.Append("POST ").Append(String.IsNullOrEmpty(endpoint.PathAndQuery) ? "/" : endpoint.PathAndQuery).Append(" HTTP/1.1\r\n");
                    request.Append("Host: ").Append(endpoint.Host);
                    if (!endpoint.IsDefaultPort) request.Append(':').Append(endpoint.Port.ToString(CultureInfo.InvariantCulture));
                    request.Append("\r\nContent-Type: application/json\r\nAccept: application/json\r\nAccept-Encoding: identity\r\n");
                    request.Append("User-Agent: VRHF-OpenAI-Adapter/1.0\r\nConnection: close\r\n");
                    if (!String.IsNullOrEmpty(config.ApiKey))
                        request.Append("Authorization: Bearer ").Append(config.ApiKey).Append("\r\n");
                    foreach (KeyValuePair<string, string> header in config.Headers)
                    {
                        if (!IsValidHeaderName(header.Key))
                            throw new InvalidDataException("Invalid custom HTTP header name: " + header.Key);
                        if (IsReservedHeader(header.Key)) continue;
                        request.Append(header.Key).Append(": ").Append(SanitizeHeaderValue(header.Value)).Append("\r\n");
                    }
                    request.Append("Content-Length: ").Append(body.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n\r\n");
                    byte[] headerBytes = Encoding.ASCII.GetBytes(request.ToString());
                    stream.Write(headerBytes, 0, headerBytes.Length);
                    stream.Write(body, 0, body.Length);
                    stream.Flush();
                    return HttpWire.ReadResponse(stream);
                }
            }
        }

        private static TcpClient Connect(string host, int port, int timeoutSeconds)
        {
            TcpClient client = new TcpClient();
            IAsyncResult operation = client.BeginConnect(host, port, null, null);
            try
            {
                if (!operation.AsyncWaitHandle.WaitOne(timeoutSeconds * 1000))
                    throw new TimeoutException("Connection timed out.");
                client.EndConnect(operation);
                return client;
            }
            catch
            {
                client.Close();
                throw;
            }
            finally { operation.AsyncWaitHandle.Close(); }
        }

        private static bool IsReservedHeader(string name)
        {
            return name.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("Authorization", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsValidHeaderName(string name)
        {
            if (String.IsNullOrEmpty(name)) return false;
            const string symbols = "!#$%&'*+-.^_`|~";
            foreach (char value in name)
            {
                bool alphaNumeric = (value >= 'a' && value <= 'z') || (value >= 'A' && value <= 'Z') ||
                    (value >= '0' && value <= '9');
                if (!alphaNumeric && symbols.IndexOf(value) < 0) return false;
            }
            return true;
        }

        private static string SanitizeHeaderValue(string value)
        {
            return (value ?? String.Empty).Replace("\r", String.Empty).Replace("\n", String.Empty);
        }

        private static string ExtractContent(JToken content)
        {
            if (content == null || content.Type == JTokenType.Null) return String.Empty;
            if (content.Type == JTokenType.String) return content.ToString();
            JArray parts = content as JArray;
            if (parts == null) return content.ToString();
            StringBuilder result = new StringBuilder();
            foreach (JToken part in parts)
            {
                JToken value = part["text"];
                if (value != null) result.Append(value.ToString());
            }
            return result.ToString();
        }

        private static string Limit(string value, int maximum)
        {
            if (String.IsNullOrEmpty(value) || value.Length <= maximum) return value;
            return value.Substring(0, maximum) + "...";
        }
    }

    internal static class Socks5
    {
        public static TcpClient Connect(Socks5Config config, string targetHost, int targetPort, int timeoutSeconds)
        {
            TcpClient client = ConnectTcp(config.Host, config.Port, timeoutSeconds);
            try
            {
                client.ReceiveTimeout = timeoutSeconds * 1000;
                client.SendTimeout = timeoutSeconds * 1000;
                NetworkStream stream = client.GetStream();
                bool authenticate = !String.IsNullOrEmpty(config.Username) || !String.IsNullOrEmpty(config.Password);
                byte[] greeting = authenticate ? new byte[] { 5, 2, 0, 2 } : new byte[] { 5, 1, 0 };
                stream.Write(greeting, 0, greeting.Length);
                byte[] response = HttpWire.ReadExactly(stream, 2);
                if (response[0] != 5 || response[1] == 255)
                    throw new IOException("SOCKS5 proxy rejected authentication methods.");
                if (response[1] == 2)
                    Authenticate(stream, config.Username, config.Password);
                else if (response[1] != 0)
                    throw new IOException("SOCKS5 proxy selected an unsupported authentication method.");

                byte[] hostBytes = Encoding.UTF8.GetBytes(targetHost);
                if (hostBytes.Length == 0 || hostBytes.Length > 255)
                    throw new IOException("SOCKS5 target host name is invalid.");
                using (MemoryStream command = new MemoryStream())
                {
                    command.WriteByte(5); command.WriteByte(1); command.WriteByte(0); command.WriteByte(3);
                    command.WriteByte((byte)hostBytes.Length);
                    command.Write(hostBytes, 0, hostBytes.Length);
                    command.WriteByte((byte)(targetPort >> 8)); command.WriteByte((byte)targetPort);
                    byte[] bytes = command.ToArray();
                    stream.Write(bytes, 0, bytes.Length);
                }

                byte[] head = HttpWire.ReadExactly(stream, 4);
                if (head[0] != 5 || head[1] != 0)
                    throw new IOException("SOCKS5 CONNECT failed (code " + head[1] + ").");
                int addressLength;
                if (head[3] == 1) addressLength = 4;
                else if (head[3] == 4) addressLength = 16;
                else if (head[3] == 3) addressLength = HttpWire.ReadExactly(stream, 1)[0];
                else throw new IOException("SOCKS5 proxy returned an invalid address type.");
                HttpWire.ReadExactly(stream, addressLength + 2);
                return client;
            }
            catch
            {
                client.Close();
                throw;
            }
        }

        private static void Authenticate(NetworkStream stream, string username, string password)
        {
            byte[] user = Encoding.UTF8.GetBytes(username ?? String.Empty);
            byte[] pass = Encoding.UTF8.GetBytes(password ?? String.Empty);
            if (user.Length > 255 || pass.Length > 255)
                throw new IOException("SOCKS5 username/password is too long.");
            using (MemoryStream request = new MemoryStream())
            {
                request.WriteByte(1); request.WriteByte((byte)user.Length); request.Write(user, 0, user.Length);
                request.WriteByte((byte)pass.Length); request.Write(pass, 0, pass.Length);
                byte[] bytes = request.ToArray();
                stream.Write(bytes, 0, bytes.Length);
            }
            byte[] response = HttpWire.ReadExactly(stream, 2);
            if (response[0] != 1 || response[1] != 0)
                throw new IOException("SOCKS5 username/password authentication failed.");
        }

        private static TcpClient ConnectTcp(string host, int port, int timeoutSeconds)
        {
            TcpClient client = new TcpClient();
            IAsyncResult operation = client.BeginConnect(host, port, null, null);
            try
            {
                if (!operation.AsyncWaitHandle.WaitOne(timeoutSeconds * 1000))
                    throw new TimeoutException("SOCKS5 proxy connection timed out.");
                client.EndConnect(operation);
                return client;
            }
            catch { client.Close(); throw; }
            finally { operation.AsyncWaitHandle.Close(); }
        }
    }

    internal sealed class HttpResponse
    {
        public int StatusCode;
        public string Body;
    }

    internal static class HttpWire
    {
        private const int MaximumResponseBytes = 10 * 1024 * 1024;

        public static Dictionary<string, string> ReadHeaders(Stream stream)
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            while (true)
            {
                string line = ReadAsciiLine(stream, 32768);
                if (line.Length == 0) return result;
                int colon = line.IndexOf(':');
                if (colon <= 0) throw new InvalidDataException("Malformed HTTP header.");
                result[line.Substring(0, colon).Trim().ToLowerInvariant()] = line.Substring(colon + 1).Trim();
            }
        }

        public static HttpResponse ReadResponse(Stream stream)
        {
            string statusLine = ReadAsciiLine(stream, 8192);
            string[] parts = statusLine.Split(' ');
            int status;
            if (parts.Length < 2 || !Int32.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out status))
                throw new InvalidDataException("Malformed API HTTP status line.");
            Dictionary<string, string> headers = ReadHeaders(stream);
            byte[] body;
            string transferEncoding;
            string contentLength;
            if (headers.TryGetValue("transfer-encoding", out transferEncoding) && transferEncoding.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0)
                body = ReadChunked(stream);
            else if (headers.TryGetValue("content-length", out contentLength))
            {
                int length;
                if (!Int32.TryParse(contentLength, NumberStyles.None, CultureInfo.InvariantCulture, out length) || length < 0 || length > MaximumResponseBytes)
                    throw new InvalidDataException("Invalid API Content-Length.");
                body = ReadExactly(stream, length);
            }
            else
                body = ReadToEnd(stream, MaximumResponseBytes);

            string encoding;
            if (headers.TryGetValue("content-encoding", out encoding) && encoding.IndexOf("gzip", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                using (MemoryStream input = new MemoryStream(body))
                using (GZipStream gzip = new GZipStream(input, CompressionMode.Decompress))
                    body = ReadToEnd(gzip, MaximumResponseBytes);
            }
            return new HttpResponse { StatusCode = status, Body = Encoding.UTF8.GetString(body) };
        }

        public static byte[] ReadExactly(Stream stream, int count)
        {
            byte[] result = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = stream.Read(result, offset, count - offset);
                if (read <= 0) throw new EndOfStreamException("Unexpected end of stream.");
                offset += read;
            }
            return result;
        }

        public static string ReadAsciiLine(Stream stream, int maximumBytes)
        {
            MemoryStream line = new MemoryStream();
            while (line.Length <= maximumBytes)
            {
                int value = stream.ReadByte();
                if (value < 0) throw new EndOfStreamException("Unexpected end of HTTP headers.");
                if (value == '\n')
                {
                    byte[] bytes = line.ToArray();
                    int length = bytes.Length > 0 && bytes[bytes.Length - 1] == '\r' ? bytes.Length - 1 : bytes.Length;
                    return Encoding.ASCII.GetString(bytes, 0, length);
                }
                line.WriteByte((byte)value);
            }
            throw new InvalidDataException("HTTP line is too long.");
        }

        public static void WriteResponse(Stream stream, int status, string body, string contentType)
        {
            byte[] bodyBytes = Encoding.UTF8.GetBytes(body ?? String.Empty);
            string reason = status == 200 ? "OK" : status == 404 ? "Not Found" : "Bad Gateway";
            string header = "HTTP/1.1 " + status.ToString(CultureInfo.InvariantCulture) + " " + reason + "\r\n" +
                "Content-Type: " + contentType + "\r\nContent-Length: " + bodyBytes.Length.ToString(CultureInfo.InvariantCulture) +
                "\r\nConnection: close\r\n\r\n";
            byte[] headerBytes = Encoding.ASCII.GetBytes(header);
            stream.Write(headerBytes, 0, headerBytes.Length);
            stream.Write(bodyBytes, 0, bodyBytes.Length);
            stream.Flush();
        }

        private static byte[] ReadChunked(Stream stream)
        {
            MemoryStream result = new MemoryStream();
            while (true)
            {
                string sizeLine = ReadAsciiLine(stream, 1024);
                int semicolon = sizeLine.IndexOf(';');
                if (semicolon >= 0) sizeLine = sizeLine.Substring(0, semicolon);
                int size;
                if (!Int32.TryParse(sizeLine.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out size) || size < 0)
                    throw new InvalidDataException("Invalid chunk size.");
                if (size == 0)
                {
                    while (ReadAsciiLine(stream, 32768).Length != 0) { }
                    return result.ToArray();
                }
                if (result.Length + size > MaximumResponseBytes)
                    throw new InvalidDataException("API response is too large.");
                byte[] chunk = ReadExactly(stream, size);
                result.Write(chunk, 0, chunk.Length);
                if (ReadAsciiLine(stream, 2).Length != 0)
                    throw new InvalidDataException("Invalid chunk terminator.");
            }
        }

        private static byte[] ReadToEnd(Stream stream, int maximumBytes)
        {
            MemoryStream result = new MemoryStream();
            byte[] buffer = new byte[8192];
            while (true)
            {
                int read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0) return result.ToArray();
                if (result.Length + read > maximumBytes)
                    throw new InvalidDataException("API response is too large.");
                result.Write(buffer, 0, read);
            }
        }
    }

    internal static class Log
    {
        private static readonly object Sync = new object();

        public static void Info(string message) { Write("INFO", message); }
        public static void Error(string message) { Write("ERROR", message); }

        private static void Write(string level, string message)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + " [" + level + "] " + message;
            lock (Sync)
            {
                try { File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "VRHFOpenAIAdapter.log"), line + Environment.NewLine, Encoding.UTF8); }
                catch { }
            }
        }
    }
}
