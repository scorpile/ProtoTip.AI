using System;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace ProtoTipAI.Editor
{
    internal static class ProtoAssetUtility
    {
        private static readonly Regex ScriptNameRegex = new Regex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

        public static bool TryEnsureFolder(string folderPath, out string normalizedPath, out string error)
        {
            normalizedPath = string.Empty;
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(folderPath))
            {
                error = "Folder path is empty.";
                return false;
            }

            var path = folderPath.Trim().Replace('\\', '/');
            if (!path.StartsWith("Assets", StringComparison.Ordinal))
            {
                error = "Folder path must start with 'Assets'.";
                return false;
            }

            normalizedPath = path;
            var parts = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                error = "Folder path is invalid.";
                return false;
            }

            var current = parts[0];
            for (var i = 1; i < parts.Length; i++)
            {
                var next = parts[i];
                var combined = $"{current}/{next}";
                if (!AssetDatabase.IsValidFolder(combined) && !Directory.Exists(GetAbsolutePath(combined)))
                {
                    var guid = AssetDatabase.CreateFolder(current, next);
                    if (string.IsNullOrEmpty(guid))
                    {
                        error = $"Failed to create folder: {combined}";
                        return false;
                    }
                }
                current = combined;
            }

            return true;
        }

        private static string GetAbsolutePath(string assetPath)
        {
            if (string.IsNullOrWhiteSpace(assetPath))
            {
                return string.Empty;
            }

            var normalized = assetPath.Trim().Replace('\\', '/');
            if (Path.IsPathRooted(normalized))
            {
                return Path.GetFullPath(normalized);
            }

            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Directory.GetCurrentDirectory();
            return Path.GetFullPath(Path.Combine(projectRoot, normalized));
        }

        public static bool TryWriteScript(string folderPath, string scriptName, string code, bool overwrite, bool refresh, out string assetPath, out string error)
        {
            assetPath = string.Empty;
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(scriptName))
            {
                error = "Script name is empty.";
                return false;
            }

            var trimmedName = scriptName.Trim();
            if (!ScriptNameRegex.IsMatch(trimmedName))
            {
                error = "Script name must be a valid C# identifier (letters, numbers, underscore).";
                return false;
            }

            if (!TryEnsureFolder(folderPath, out var normalizedPath, out error))
            {
                return false;
            }

            var fileName = $"{trimmedName}.cs";
            assetPath = $"{normalizedPath}/{fileName}";
            var fullPath = Path.GetFullPath(assetPath);

            if (File.Exists(fullPath) && !overwrite)
            {
                error = "Script already exists. Enable overwrite to replace.";
                return false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? normalizedPath);
            File.WriteAllText(fullPath, code ?? string.Empty);
            if (refresh)
            {
                AssetDatabase.Refresh();
            }
            return true;
        }
    }
}
