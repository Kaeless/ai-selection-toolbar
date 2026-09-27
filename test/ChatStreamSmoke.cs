using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AiSelectionToolbar.Core;

internal static class ChatStreamSmoke
{
    private sealed class FakeStream : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            const string events =
                "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"thinking\"},\"finish_reason\":null}]}\n\n" +
                "data: {\"choices\":[{\"delta\":{\"content\":\"\\u7b54\\u6848\"},\"finish_reason\":null}]}\n\n" +
                "data: [DONE]\n\n";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(events, Encoding.UTF8, "text/event-stream")
            });
        }
    }

    private static void Main()
    {
        var deltas = new List<ChatDelta>();
        using (var client = new ChatCompletionClient(new HttpClient(new FakeStream())))
        {
            client.StreamAsync(new AppSettings { BaseUrl = "https://api.deepseek.com",
                Model = "deepseek-flash", TimeoutSeconds = 10 }, "test-key",
                new[] { new ChatMessage("user", "test") }, delta => {
                    deltas.Add(delta); return Task.CompletedTask;
                }, CancellationToken.None).GetAwaiter().GetResult();
        }
        if (!deltas.Any(delta => delta.IsReasoning) ||
            string.Concat(deltas.Select(delta => delta.Text)) != "\u7b54\u6848")
            throw new Exception("Reasoning/content stream was not parsed correctly.");
        Console.WriteLine("Reasoning and answer SSE parsing passed.");
    }
}
