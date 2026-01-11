using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace ProtoTipAI.Editor
{
    public sealed class ProtoPlanTrackerWindow : EditorWindow
    {
        private const string FeatureRequestFolder = "Assets/Plan";
        private readonly List<ProtoFeatureRequest> _requests = new List<ProtoFeatureRequest>();
        private Vector2 _scroll;
        private string _search = string.Empty;
        private int _statusFilter;
        private int _typeFilter;

        private static readonly string[] StatusFilters =
        {
            "All",
            "todo",
            "in_progress",
            "done",
            "blocked"
        };

        private static readonly string[] TypeFilters =
        {
            "All",
            "folder",
            "script",
            "prefab",
            "scene",
            "material",
            "asset"
        };

        [MenuItem("Proto/Plan Tracking")]
        public static void ShowWindow()
        {
            var window = GetWindow<ProtoPlanTrackerWindow>("Proto Plan");
            window.minSize = new Vector2(520f, 300f);
            window.Refresh();
        }

        private void OnFocus()
        {
            Refresh();
        }

        private void OnGUI()
        {
            DrawToolbar();
            DrawList();
        }

        private void DrawToolbar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                if (GUILayout.Button("Refresh", EditorStyles.toolbarButton))
                {
                    Refresh();
                }

                if (GUILayout.Button("Open Folder", EditorStyles.toolbarButton))
                {
                    var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(FeatureRequestFolder);
                    if (asset != null)
                    {
                        EditorGUIUtility.PingObject(asset);
                        Selection.activeObject = asset;
                    }
                }

                GUILayout.FlexibleSpace();
                _statusFilter = EditorGUILayout.Popup(_statusFilter, StatusFilters, GUILayout.Width(120f));
                _typeFilter = EditorGUILayout.Popup(_typeFilter, TypeFilters, GUILayout.Width(120f));
                _search = GUILayout.TextField(_search, EditorStyles.toolbarSearchField, GUILayout.Width(200f));
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Reset All", GUILayout.Width(120f)))
                {
                    if (EditorUtility.DisplayDialog("Reset Feature Requests",
                        "Reset all feature requests to status 'todo'?",
                        "Reset",
                        "Cancel"))
                    {
                        ResetAllRequestsToTodo();
                    }
                }
            }
        }

        private void DrawList()
        {
            using (var scroll = new EditorGUILayout.ScrollViewScope(_scroll))
            {
                _scroll = scroll.scrollPosition;
                if (_requests.Count == 0)
                {
                    EditorGUILayout.HelpBox("No feature requests found.", MessageType.Info);
                    return;
                }

                foreach (var request in _requests)
                {
                    if (request == null)
                    {
                        continue;
                    }

                    if (!MatchesFilter(request))
                    {
                        continue;
                    }

                    using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                    {
                        EditorGUILayout.LabelField($"{request.name} ({request.type})", EditorStyles.boldLabel);
                        EditorGUILayout.LabelField($"Status: {request.status}");
                        if (!string.IsNullOrWhiteSpace(request.path))
                        {
                            EditorGUILayout.LabelField($"Path: {request.path}");
                        }
                        if (request.dependsOn != null && request.dependsOn.Length > 0)
                        {
                            EditorGUILayout.LabelField($"Depends On: {string.Join(", ", request.dependsOn)}");
                        }
                        if (!string.IsNullOrWhiteSpace(request.notes))
                        {
                            EditorGUILayout.LabelField($"Notes: {request.notes}");
                        }

                        using (new EditorGUILayout.HorizontalScope())
                        {
                            GUILayout.FlexibleSpace();
                            if (GUILayout.Button("Reset", GUILayout.Width(80f)))
                            {
                                ResetRequestToTodo(request);
                                Refresh();
                            }
                            if (GUILayout.Button("Re-do", GUILayout.Width(80f)))
                            {
                                ResetRequestToTodo(request);
                                ProtoChatWindow.ApplySingleRequestFromTracker(request);
                            }
                            if (GUILayout.Button("Open JSON", GUILayout.Width(120f)))
                            {
                                OpenRequestFile(request.id);
                            }
                        }
                    }
                }
            }
        }

        private bool MatchesFilter(ProtoFeatureRequest request)
        {
            if (_statusFilter > 0)
            {
                var status = StatusFilters[_statusFilter];
                if (!string.Equals(request.status, status, System.StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            if (_typeFilter > 0)
            {
                var type = TypeFilters[_typeFilter];
                if (!string.Equals(request.type, type, System.StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            if (!string.IsNullOrWhiteSpace(_search))
            {
                var query = _search.Trim().ToLowerInvariant();
                var haystack = $"{request.id} {request.name} {request.type} {request.path}".ToLowerInvariant();
                if (!haystack.Contains(query))
                {
                    return false;
                }
            }

            return true;
        }

        private void Refresh()
        {
            _requests.Clear();
            if (!AssetDatabase.IsValidFolder(FeatureRequestFolder))
            {
                return;
            }

            var guids = AssetDatabase.FindAssets("t:TextAsset", new[] { FeatureRequestFolder });
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.EndsWith(".json", System.StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var fullPath = Path.GetFullPath(path);
                if (!File.Exists(fullPath))
                {
                    continue;
                }

                try
                {
                    var json = File.ReadAllText(fullPath);
                    var request = JsonUtility.FromJson<ProtoFeatureRequest>(json);
                    if (request != null)
                    {
                        if (string.IsNullOrWhiteSpace(request.id))
                        {
                            request.id = Path.GetFileNameWithoutExtension(path);
                        }
                        _requests.Add(request);
                    }
                }
                catch
                {
                    // Ignore malformed json files.
                }
            }
        }

        private static void OpenRequestFile(string requestId)
        {
            if (string.IsNullOrWhiteSpace(requestId))
            {
                return;
            }

            var path = $"{FeatureRequestFolder}/{requestId}.json";
            var asset = AssetDatabase.LoadAssetAtPath<TextAsset>(path);
            if (asset != null)
            {
                Selection.activeObject = asset;
                EditorGUIUtility.PingObject(asset);
            }
        }

        private void ResetRequestToTodo(ProtoFeatureRequest request)
        {
            if (request == null)
            {
                return;
            }

            request.status = "todo";
            request.updatedAt = DateTime.UtcNow.ToString("o");
            ProtoFeatureRequestStore.SaveRequest(request);
        }

        private void ResetAllRequestsToTodo()
        {
            if (!AssetDatabase.IsValidFolder(FeatureRequestFolder))
            {
                return;
            }

            Refresh();
            var now = DateTime.UtcNow.ToString("o");
            ProtoFeatureRequestStore.EnsureFeatureRequestFolder();

            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var request in _requests)
                {
                    if (request == null || string.IsNullOrWhiteSpace(request.id))
                    {
                        continue;
                    }

                    request.status = "todo";
                    request.updatedAt = now;

                    var path = ProtoFeatureRequestStore.GetRequestPath(request.id);
                    var json = JsonUtility.ToJson(request, true);
                    File.WriteAllText(Path.GetFullPath(path), json);
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                AssetDatabase.Refresh();
            }
        }
    }
}
