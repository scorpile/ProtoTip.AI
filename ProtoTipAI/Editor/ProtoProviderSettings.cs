#nullable enable
using UnityEditor;

namespace ProtoTipAI.Editor
{
    internal sealed class ProtoProviderSnapshot
    {
        public ProtoProviderSnapshot(ProtoProviderDefinition provider, string model, string apiKey, string baseUrl)
        {
            Provider = provider;
            Model = string.IsNullOrWhiteSpace(model) ? provider.DefaultModel : model.Trim();
            ApiKey = apiKey ?? string.Empty;
            BaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? provider.BaseUrl : baseUrl.Trim();
        }

        public ProtoProviderDefinition Provider { get; }
        public string Model { get; }
        public string ApiKey { get; }
        public string BaseUrl { get; }
    }

    internal static class ProtoProviderSettings
    {
        private const string ProviderIdKey = "ProtoTipAI.Provider.Id";
        private const string ModelKey = "ProtoTipAI.Provider.Model";
        private const string ApiKeyKey = "ProtoTipAI.Provider.Key";
        private const string BaseUrlKey = "ProtoTipAI.Provider.BaseUrl";

        public static string GetProviderId()
        {
            return EditorPrefs.GetString(ProviderIdKey, ProtoProviderRegistry.Default.Id);
        }

        public static void SetProviderId(string providerId)
        {
            EditorPrefs.SetString(ProviderIdKey, providerId ?? string.Empty);
        }

        public static string GetModel()
        {
            var model = EditorPrefs.GetString(ModelKey, string.Empty);
            return string.IsNullOrWhiteSpace(model) ? ProtoProviderRegistry.Default.DefaultModel : model;
        }

        public static void SetModel(string model)
        {
            EditorPrefs.SetString(ModelKey, model ?? string.Empty);
        }

        public static string GetApiKey()
        {
            return EditorPrefs.GetString(ApiKeyKey, string.Empty);
        }

        public static void SetApiKey(string apiKey)
        {
            EditorPrefs.SetString(ApiKeyKey, apiKey ?? string.Empty);
        }

        public static string GetCustomBaseUrl()
        {
            return EditorPrefs.GetString(BaseUrlKey, string.Empty);
        }

        public static void SetCustomBaseUrl(string baseUrl)
        {
            EditorPrefs.SetString(BaseUrlKey, baseUrl ?? string.Empty);
        }

        public static bool HasApiKey()
        {
            return !string.IsNullOrWhiteSpace(GetApiKey());
        }

        public static ProtoProviderSnapshot GetSnapshot()
        {
            var provider = ProtoProviderRegistry.Get(GetProviderId());
            return new ProtoProviderSnapshot(provider, GetModel(), GetApiKey(), GetCustomBaseUrl());
        }
    }
}
