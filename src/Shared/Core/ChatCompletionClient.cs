using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AiSelectionToolbar.Core
{
    /// <summary>OpenAI-compatible /chat/completions SSE client. Caller owns cancellation and UI dispatch.</summary>
    public sealed class ChatCompletionClient : IDisposable
    {
        private readonly HttpClient client;
        private readonly bool ownsClient;

        public ChatCompletionClient() : this(new HttpClient(), true) { }
        public ChatCompletionClient(HttpClient client) : this(client, false) { }
        private ChatCompletionClient(HttpClient client, bool ownsClient)
        {
            this.client = client ?? throw new ArgumentNullException(nameof(client));
            this.ownsClient = ownsClient;
        }

        public async Task StreamAsync(AppSettings settings, string apiKey,
            IEnumerable<ChatMessage> messages, Func<ChatDelta, Task> onDelta,
            CancellationToken cancellationToken)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (!settings.IsConfigured) throw new InvalidOperationException("Configure the API endpoint and model before sending selected text.");
            if (messages == null) throw new ArgumentNullException(nameof(messages));
            if (onDelta == null) throw new ArgumentNullException(nameof(onDelta));
            if (string.IsNullOrWhiteSpace(settings.Model)) throw new ArgumentException("Model is required.", nameof(settings));
            if (settings.TimeoutSeconds < 1) throw new ArgumentOutOfRangeException(nameof(settings.TimeoutSeconds));

            var uri = CompletionUri(settings.BaseUrl);
            var requestBody = new RequestBody { Model = settings.Model, Stream = true };
            foreach (var message in messages)
            {
                if (message == null) throw new ArgumentException("Messages cannot contain null.", nameof(messages));
                requestBody.Messages.Add(new RequestMessage { Role = message.Role, Content = message.Content });
            }
            if (requestBody.Messages.Count == 0) throw new ArgumentException("At least one message is required.", nameof(messages));

            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            using (var request = new HttpRequestMessage(HttpMethod.Post, uri))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
                if (!string.IsNullOrWhiteSpace(apiKey))
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                request.Content = new ByteArrayContent(Serialize(requestBody));
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

                using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        var details = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        try
                        {
                            using (var buffer = new MemoryStream(Encoding.UTF8.GetBytes(details)))
                            {
                                var body = (ResponseBody)new DataContractJsonSerializer(typeof(ResponseBody)).ReadObject(buffer);
                                if (!string.IsNullOrWhiteSpace(body?.Error?.Message))
                                    details = body.Error.Message;
                            }
                        }
                        catch (SerializationException) { /* Keep the provider's original error text. */ }
                        if (details.Length > 2048) details = details.Substring(0, 2048);
                        throw new HttpRequestException("接口 " + uri.Host + " 返回 HTTP " +
                            (int)response.StatusCode + "（模型 " + settings.Model + "）：" + details);
                    }
                    using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (timeout.Token.Register(() => stream.Dispose()))
                    using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                    {
                        var data = new StringBuilder();
                        var sawFinishReason = false;
                        try
                        {
                            while (true)
                            {
                                timeout.Token.ThrowIfCancellationRequested();
                                var line = await reader.ReadLineAsync().ConfigureAwait(false);
                                if (line == null)
                                {
                                    if (data.Length != 0)
                                    {
                                        var signal = await DispatchAsync(data.ToString(), onDelta).ConfigureAwait(false);
                                        if (signal == CompletionSignal.Done) return;
                                        sawFinishReason |= signal == CompletionSignal.Finished;
                                    }
                                    timeout.Token.ThrowIfCancellationRequested();
                                    if (!sawFinishReason) throw new IOException("Chat stream ended before completion.");
                                    return;
                                }
                                if (line.Length == 0)
                                {
                                    if (data.Length != 0)
                                    {
                                        var signal = await DispatchAsync(data.ToString(), onDelta).ConfigureAwait(false);
                                        if (signal == CompletionSignal.Done) return;
                                        sawFinishReason |= signal == CompletionSignal.Finished;
                                    }
                                    data.Clear();
                                }
                                else if (line.StartsWith("data:", StringComparison.Ordinal))
                                {
                                    if (data.Length > 0) data.Append('\n');
                                    data.Append(line.Substring(5).TrimStart());
                                }
                            }
                        }
                        catch (Exception ex) when (timeout.IsCancellationRequested &&
                            (ex is IOException || ex is ObjectDisposedException || ex is HttpRequestException))
                        {
                            throw new OperationCanceledException("Chat stream canceled or timed out.", ex, timeout.Token);
                        }
                    }
                }
            }
        }

        private static Uri CompletionUri(string baseUrl)
        {
            Uri parsed;
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out parsed) ||
                (parsed.Scheme != Uri.UriSchemeHttps &&
                 (parsed.Scheme != Uri.UriSchemeHttp || !parsed.IsLoopback)))
                throw new ArgumentException("Remote APIs require HTTPS; HTTP is only allowed on loopback.", nameof(baseUrl));
            if (parsed.AbsolutePath.TrimEnd('/').EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) return parsed;
            return new Uri(baseUrl.TrimEnd('/') + "/chat/completions");
        }

        private static byte[] Serialize(object value)
        {
            using (var buffer = new MemoryStream())
            {
                new DataContractJsonSerializer(value.GetType()).WriteObject(buffer, value);
                return buffer.ToArray();
            }
        }

        private enum CompletionSignal { None, Finished, Done }

        private static async Task<CompletionSignal> DispatchAsync(string json, Func<ChatDelta, Task> callback)
        {
            if (json == "[DONE]") return CompletionSignal.Done;
            ResponseBody body;
            using (var buffer = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                body = (ResponseBody)new DataContractJsonSerializer(typeof(ResponseBody)).ReadObject(buffer);
            if (body.Error != null) throw new InvalidOperationException("Chat API error: " + body.Error.Message);
            if (body.Choices == null) return CompletionSignal.None;
            var finished = false;
            foreach (var choice in body.Choices)
            {
                if (choice == null) continue;
                if (!string.IsNullOrEmpty(choice.FinishReason)) finished = true;
                if ((choice.Delta != null &&
                    (!string.IsNullOrEmpty(choice.Delta.Content) || !string.IsNullOrEmpty(choice.Delta.ReasoningContent))) ||
                    !string.IsNullOrEmpty(choice.FinishReason))
                    await callback(new ChatDelta
                    {
                        Text = choice.Delta?.Content,
                        FinishReason = choice.FinishReason,
                        IsReasoning = !string.IsNullOrEmpty(choice.Delta?.ReasoningContent) &&
                            string.IsNullOrEmpty(choice.Delta?.Content)
                    }).ConfigureAwait(false);
            }
            return finished ? CompletionSignal.Finished : CompletionSignal.None;
        }

        public void Dispose() { if (ownsClient) client.Dispose(); }

        [DataContract]
        private sealed class RequestBody
        {
            [DataMember(Name = "model")] public string Model { get; set; }
            [DataMember(Name = "stream")] public bool Stream { get; set; }
            [DataMember(Name = "messages")] public List<RequestMessage> Messages { get; set; } = new List<RequestMessage>();
        }
        [DataContract]
        private sealed class RequestMessage
        {
            [DataMember(Name = "role")] public string Role { get; set; }
            [DataMember(Name = "content")] public string Content { get; set; }
        }
        [DataContract]
        private sealed class ResponseBody
        {
            [DataMember(Name = "choices")] public List<Choice> Choices { get; set; }
            [DataMember(Name = "error")] public ErrorBody Error { get; set; }
        }
        [DataContract]
        private sealed class Choice
        {
            [DataMember(Name = "delta")] public DeltaBody Delta { get; set; }
            [DataMember(Name = "finish_reason")] public string FinishReason { get; set; }
        }
        [DataContract]
        private sealed class DeltaBody
        {
            [DataMember(Name = "content")] public string Content { get; set; }
            [DataMember(Name = "reasoning_content")] public string ReasoningContent { get; set; }
        }
        [DataContract]
        private sealed class ErrorBody
        {
            [DataMember(Name = "message")] public string Message { get; set; }
        }
    }
}
