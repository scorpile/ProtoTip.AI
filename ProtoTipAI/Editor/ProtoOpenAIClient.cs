using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace ProtoTipAI.Editor
{
    internal static class ProtoOpenAIClient
    {
        private static readonly HttpClient Client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(60)
        };

        [Serializable]
        private sealed class ChatRequest
        {
            public string model;
            public ProtoChatMessage[] messages;
            public float temperature = 0.7f;
        }

        [Serializable]
        private sealed class ChatResponse
        {
            public Choice[] choices;
        }

        [Serializable]
        private sealed class Choice
        {
            public ProtoChatMessage message;
        }

        public static Task<string> SendChatAsync(string apiKey, string model, ProtoChatMessage[] messages)
        {
            return SendChatAsync(apiKey, model, messages, 320);
        }

        public static Task<string> SendChatAsync(string apiKey, string model, ProtoChatMessage[] messages, int timeoutSeconds)
        {
            return SendChatAsync(apiKey, model, messages, timeoutSeconds, CancellationToken.None);
        }

        public static async Task<string> SendChatAsync(string apiKey, string model, ProtoChatMessage[] messages, int timeoutSeconds, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new InvalidOperationException("Missing OpenAI API key.");
            }

            var request = new ChatRequest
            {
                model = model,
                messages = messages
            };

            var json = JsonUtility.ToJson(request);
            using var message = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            message.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);
            using var response = await Client.SendAsync(message, linkedCts.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"OpenAI error {response.StatusCode}: {body}");
            }

            var parsed = JsonUtility.FromJson<ChatResponse>(body);
            if (parsed?.choices == null || parsed.choices.Length == 0 || parsed.choices[0].message == null)
            {
                throw new InvalidOperationException("OpenAI response missing choices.");
            }

            return parsed.choices[0].message.content ?? string.Empty;
        }
    }
}
