using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace ProtoTipAI.Editor
{
    internal static class ProtoFeatureRequestStore
    {
        private const string FeatureRequestFolder = "Assets/Plan";

        public static string EnsureFeatureRequestFolder()
        {
            ProtoAssetUtility.TryEnsureFolder(FeatureRequestFolder, out var normalized, out _);
            return normalized;
        }

        public static string GetRequestPath(string id)
        {
            var safeId = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id;
            safeId = MakeSafeFileName(safeId);
            if (string.IsNullOrWhiteSpace(safeId))
            {
                safeId = Guid.NewGuid().ToString("N");
            }
            return $"{FeatureRequestFolder}/{safeId}.json";
        }

        public static void SaveRequest(ProtoFeatureRequest request)
        {
            if (request == null)
            {
                return;
            }

            EnsureFeatureRequestFolder();
            var path = GetRequestPath(request.id);

            var json = JsonUtility.ToJson(request, true);
            File.WriteAllText(Path.GetFullPath(path), json);
            AssetDatabase.Refresh();
        }

        private static string MakeSafeFileName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var chars = value.Trim().ToCharArray();
            for (var i = 0; i < chars.Length; i++)
            {
                var ch = chars[i];
                var safe = (ch >= 'a' && ch <= 'z')
                           || (ch >= 'A' && ch <= 'Z')
                           || (ch >= '0' && ch <= '9')
                           || ch == '_'
                           || ch == '-';
                if (!safe)
                {
                    chars[i] = '_';
                }
            }

            var sanitized = new string(chars);
            while (sanitized.Contains("__", StringComparison.Ordinal))
            {
                sanitized = sanitized.Replace("__", "_");
            }

            return sanitized.Trim('_');
        }
    }
}
