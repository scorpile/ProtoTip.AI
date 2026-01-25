using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ProtoTipAI.Editor
{
    internal static class ProtoProjectContext
    {
        private const int MaxSelectionEntries = 12;
        private const int MaxSceneRoots = 24;
        private const int MaxComponentsPerObject = 10;
        private const int MaxRecentAssets = 12;
        private const int MaxConsoleEntries = 10;

        public static string BuildSystemContext()
        {
            var builder = new StringBuilder(512);
            builder.AppendLine(ProtoPrompts.SystemContextLine1);
            builder.AppendLine(ProtoPrompts.SystemContextLine2);
            builder.AppendLine(ProtoPrompts.SystemContextLine3);
            return builder.ToString().Trim();
        }

        public static string BuildProjectSummary()
        {
            var builder = new StringBuilder(1024);
            builder.AppendLine($"UnityVersion: {Application.unityVersion}");
            builder.AppendLine($"ProductName: {PlayerSettings.productName}");
            builder.AppendLine($"CompanyName: {PlayerSettings.companyName}");

            var projectPath = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
            builder.AppendLine($"ProjectPath: {projectPath}");

            builder.AppendLine("AssetsFolders:");
            try
            {
                var assetsPath = Application.dataPath;
                var dirs = Directory.GetDirectories(assetsPath);
                Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
                foreach (var dir in dirs)
                {
                    builder.AppendLine($"- {Path.GetFileName(dir)}");
                }
            }
            catch (Exception ex)
            {
                builder.AppendLine($"- Error reading Assets folders: {ex.Message}");
            }

            builder.AppendLine("PackagesManifest:");
            var manifestPath = Path.Combine(projectPath, "Packages", "manifest.json");
            if (File.Exists(manifestPath))
            {
                AppendDependencyBlock(builder, manifestPath);
            }
            else
            {
                builder.AppendLine("- manifest.json not found.");
            }

            return builder.ToString().Trim();
        }

        public static string BuildDynamicContext(bool includeSelection, bool includeScene, bool includeRecentAssets, bool includeConsole)
        {
            var builder = new StringBuilder(2048);

            if (includeSelection)
            {
                AppendSelection(builder);
            }

            if (includeScene)
            {
                AppendScene(builder);
            }

            if (includeRecentAssets)
            {
                AppendRecentAssets(builder);
            }

            if (includeConsole)
            {
                AppendConsole(builder);
            }

            return builder.ToString().Trim();
        }

        private static void AppendSelection(StringBuilder builder)
        {
            builder.AppendLine("Selection:");
            var selection = Selection.objects;
            if (selection == null || selection.Length == 0)
            {
                builder.AppendLine("- (none)");
                return;
            }

            var count = Mathf.Min(selection.Length, MaxSelectionEntries);
            for (var i = 0; i < count; i++)
            {
                var obj = selection[i];
                if (obj == null)
                {
                    continue;
                }

                if (obj is GameObject gameObject)
                {
                    builder.AppendLine($"- GameObject: {gameObject.name}");
                    AppendComponents(builder, gameObject);
                }
                else
                {
                    var path = AssetDatabase.GetAssetPath(obj);
                    builder.AppendLine($"- Asset: {obj.name} ({obj.GetType().Name}) {path}");
                }
            }
        }

        private static void AppendComponents(StringBuilder builder, GameObject gameObject)
        {
            var components = gameObject.GetComponents<Component>();
            if (components == null || components.Length == 0)
            {
                builder.AppendLine("  Components: (none)");
                return;
            }

            builder.Append("  Components: ");
            var max = Mathf.Min(components.Length, MaxComponentsPerObject);
            for (var i = 0; i < max; i++)
            {
                var component = components[i];
                if (component == null)
                {
                    continue;
                }

                builder.Append(component.GetType().Name);
                if (i < max - 1)
                {
                    builder.Append(", ");
                }
            }

            if (components.Length > max)
            {
                builder.Append(", ...");
            }

            builder.AppendLine();
        }

        private static void AppendScene(StringBuilder builder)
        {
            var scene = SceneManager.GetActiveScene();
            if (!scene.IsValid())
            {
                builder.AppendLine("ActiveScene: (invalid)");
                return;
            }

            builder.AppendLine($"ActiveScene: {scene.name}");
            var roots = scene.GetRootGameObjects();
            var count = Mathf.Min(roots.Length, MaxSceneRoots);
            builder.AppendLine("SceneRoots:");
            for (var i = 0; i < count; i++)
            {
                var root = roots[i];
                if (root == null)
                {
                    continue;
                }

                builder.AppendLine($"- {root.name}");
                AppendComponents(builder, root);
            }

            if (roots.Length > count)
            {
                builder.AppendLine("- ...");
            }

            builder.AppendLine($"ScenePath: {scene.path}");
            builder.AppendLine($"SceneDirty: {scene.isDirty}");
        }

        private static void AppendRecentAssets(StringBuilder builder)
        {
            builder.AppendLine("RecentAssets:");
            try
            {
                var internalType = typeof(UnityEditorInternal.InternalEditorUtility);
                var property = internalType.GetProperty("recentlyUsedAssets", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
                var assets = property?.GetValue(null) as string[];
                if (assets == null || assets.Length == 0)
                {
                    builder.AppendLine("- (none)");
                    return;
                }

                var count = Mathf.Min(assets.Length, MaxRecentAssets);
                for (var i = 0; i < count; i++)
                {
                    var path = assets[i];
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        continue;
                    }
                    builder.AppendLine($"- {path}");
                }
            }
            catch (Exception ex)
            {
                builder.AppendLine($"- Error reading recent assets: {ex.Message}");
            }
        }

        private static void AppendConsole(StringBuilder builder)
        {
            builder.AppendLine("Console:");
            try
            {
                var logEntriesType = Type.GetType("UnityEditor.LogEntries, UnityEditor");
                var logEntryType = Type.GetType("UnityEditor.LogEntry, UnityEditor");
                if (logEntriesType == null || logEntryType == null)
                {
                    builder.AppendLine("- LogEntries reflection not available.");
                    return;
                }

                var getCount = logEntriesType.GetMethod("GetCount", BindingFlags.Public | BindingFlags.Static);
                var getEntry = logEntriesType.GetMethod("GetEntryInternal", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (getCount == null || getEntry == null)
                {
                    builder.AppendLine("- LogEntries API not found.");
                    return;
                }

                var count = (int)getCount.Invoke(null, null);
                if (count == 0)
                {
                    builder.AppendLine("- (empty)");
                    return;
                }

                var start = Mathf.Max(0, count - MaxConsoleEntries);
                var entry = Activator.CreateInstance(logEntryType);
                var conditionField = logEntryType.GetField("condition", BindingFlags.Instance | BindingFlags.Public);
                var stackField = logEntryType.GetField("stackTrace", BindingFlags.Instance | BindingFlags.Public);
                var modeField = logEntryType.GetField("mode", BindingFlags.Instance | BindingFlags.Public);

                for (var i = start; i < count; i++)
                {
                    getEntry.Invoke(null, new[] { i, entry });
                    var condition = conditionField?.GetValue(entry) as string ?? string.Empty;
                    var stack = stackField?.GetValue(entry) as string ?? string.Empty;
                    var mode = modeField?.GetValue(entry)?.ToString() ?? string.Empty;

                    builder.AppendLine($"- Mode:{mode} {condition}");
                    if (!string.IsNullOrWhiteSpace(stack))
                    {
                        builder.AppendLine(stack.Trim());
                    }
                }
            }
            catch (Exception ex)
            {
                builder.AppendLine($"- Error reading console: {ex.Message}");
            }
        }

        private static void AppendDependencyBlock(StringBuilder builder, string manifestPath)
        {
            try
            {
                var lines = File.ReadAllLines(manifestPath);
                var inDependencies = false;
                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("\"dependencies\"", StringComparison.OrdinalIgnoreCase))
                    {
                        inDependencies = true;
                    }

                    if (inDependencies)
                    {
                        builder.AppendLine(line);
                        if (trimmed.StartsWith("}", StringComparison.Ordinal))
                        {
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                builder.AppendLine($"- Error reading manifest.json: {ex.Message}");
            }
        }
    }
}
