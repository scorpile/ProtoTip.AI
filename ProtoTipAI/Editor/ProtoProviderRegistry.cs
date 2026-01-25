using System;
using System.Collections.Generic;
using System.Linq;

namespace ProtoTipAI.Editor
{
    internal enum ProtoProviderProtocol
    {
        OpenAI,
        OpenAICompatible,
        Anthropic
    }

    internal sealed class ProtoProviderDefinition
    {
        public ProtoProviderDefinition(
            string id,
            string displayName,
            string baseUrl,
            string defaultModel,
            ProtoProviderProtocol protocol,
            params string[] recommendedModels)
        {
            Id = id;
            DisplayName = displayName;
            BaseUrl = baseUrl;
            DefaultModel = defaultModel;
            Protocol = protocol;
            RecommendedModels = recommendedModels ?? Array.Empty<string>();
        }

        public string Id { get; }
        public string DisplayName { get; }
        public string BaseUrl { get; }
        public string DefaultModel { get; }
        public ProtoProviderProtocol Protocol { get; }
        public string[] RecommendedModels { get; }

        public string GetChatEndpoint()
        {
            var prefix = (BaseUrl ?? string.Empty).TrimEnd('/');
            return Protocol == ProtoProviderProtocol.Anthropic
                ? $"{prefix}/messages"
                : $"{prefix}/chat/completions";
        }
    }

    internal static class ProtoProviderRegistry
    {
        private static readonly List<ProtoProviderDefinition> Providers = new List<ProtoProviderDefinition>
        {
            new ProtoProviderDefinition(
                "openai",
                "OpenAI",
                "https://api.openai.com/v1",
                "gpt-5.2",
                ProtoProviderProtocol.OpenAI,
                "gpt-5.2",
                "gpt-5.2-codex",
                "gpt-4o-mini"),
            new ProtoProviderDefinition(
                "opencode",
                "OpenCode Zen (OpenAI-compatible)",
                "https://opencode.ai/zen/v1",
                "big-pickle",
                ProtoProviderProtocol.OpenAICompatible,
                "big-pickle",
                "grok-code",
                "glm-4.7",
                "minimax-m2.1-free")
        };

        public static IReadOnlyList<ProtoProviderDefinition> All => Providers;
        public static ProtoProviderDefinition Default => Providers[0];

        public static ProtoProviderDefinition Get(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return Default;
            }

            return Providers.Find(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Default;
        }

        public static string[] DisplayNames => Providers.Select(p => p.DisplayName).ToArray();

        public static int IndexOf(string id)
        {
            for (var i = 0; i < Providers.Count; i++)
            {
                if (string.Equals(Providers[i].Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return 0;
        }
    }
}
