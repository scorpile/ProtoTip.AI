using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace ProtoTipAI.Editor
{
    internal static class ProtoProviderClient
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

        public static Task<string> SendChatAsync(ProtoProviderSnapshot snapshot, ProtoChatMessage[] messages)
        {
            return SendChatAsync(snapshot, messages, 320, CancellationToken.None);
        }

        public static Task<string> SendChatAsync(ProtoProviderSnapshot snapshot, ProtoChatMessage[] messages, int timeoutSeconds)
        {
            return SendChatAsync(snapshot, messages, timeoutSeconds, CancellationToken.None);
        }

        public static async Task<string> SendChatAsync(ProtoProviderSnapshot snapshot, ProtoChatMessage[] messages, int timeoutSeconds, CancellationToken cancellationToken)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            if (string.IsNullOrWhiteSpace(snapshot.ApiKey))
            {
                throw new InvalidOperationException("Missing API key.");
            }

            if (messages == null)
            {
                throw new ArgumentNullException(nameof(messages));
            }

            var endpoint = BuildEndpoint(snapshot);
            var request = new ChatRequest
            {
                model = snapshot.Model,
                messages = messages
            };

            var json = JsonUtility.ToJson(request);
            using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", snapshot.ApiKey);

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);
            using var response = await Client.SendAsync(message, linkedCts.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Provider error {response.StatusCode}: {body}");
            }

            var parsed = JsonUtility.FromJson<ChatResponse>(body);
            if (parsed?.choices == null || parsed.choices.Length == 0 || parsed.choices[0].message == null)
            {
                throw new InvalidOperationException("Provider response missing choices.");
            }

            return parsed.choices[0].message.content ?? string.Empty;
        }

        private static string BuildEndpoint(ProtoProviderSnapshot snapshot)
        {
            var baseUrl = (snapshot.BaseUrl ?? string.Empty).TrimEnd('/');
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                baseUrl = (snapshot.Provider.BaseUrl ?? string.Empty).TrimEnd('/');
            }

            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                throw new InvalidOperationException("Provider base URL is not configured.");
            }

            return snapshot.Provider.Protocol == ProtoProviderProtocol.Anthropic
                ? $"{baseUrl}/messages"
                : $"{baseUrl}/chat/completions";
        }
    }
}
