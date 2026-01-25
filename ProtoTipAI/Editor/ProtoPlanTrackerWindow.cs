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
    private string _planSnapshotRaw = string.Empty;
    private ProtoPhasePlan _planSnapshotPhase;
    private ProtoGenerationPlan _planSnapshotSimple;
    private Vector2 _planSnapshotScroll;
    private bool _showPlanRaw;

        private static readonly string[] StatusFilters =
        {
            "All",
            ProtoAgentRequestStatus.Todo.ToNormalizedString(),
            ProtoAgentRequestStatus.InProgress.ToNormalizedString(),
            ProtoAgentRequestStatus.Done.ToNormalizedString(),
            ProtoAgentRequestStatus.Blocked.ToNormalizedString()
        };

        private static readonly ProtoAgentRequestStatus[] StatusFilterValues =
        {
            ProtoAgentRequestStatus.Todo,
            ProtoAgentRequestStatus.InProgress,
            ProtoAgentRequestStatus.Done,
            ProtoAgentRequestStatus.Blocked
        };

        private static readonly string[] TypeFilters =
        {
            "All",
            "folder",
            "script",
            "prefab",
            "prefab_component",
            "scene",
            "scene_prefab",
            "scene_manager",
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
            DrawPlanSnapshot();
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

        private void DrawPlanSnapshot()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Plan Snapshot", EditorStyles.boldLabel);
                if (string.IsNullOrWhiteSpace(_planSnapshotRaw))
                {
                    EditorGUILayout.LabelField("No plan snapshot available. Generate or apply a plan from Proto Chat to visualize the current phases.", EditorStyles.wordWrappedLabel);
                }
                else if (_planSnapshotPhase != null && _planSnapshotPhase.phases != null && _planSnapshotPhase.phases.Length > 0)
                {
                    var totalRequests = 0;
                    foreach (var phase in _planSnapshotPhase.phases)
                    {
                        if (phase?.featureRequests != null)
                        {
                            totalRequests += phase.featureRequests.Length;
                        }
                    }
                    EditorGUILayout.LabelField($"Phases: {_planSnapshotPhase.phases.Length}, Requests defined: {totalRequests}");

                    foreach (var phase in _planSnapshotPhase.phases)
                    {
                        if (phase == null)
                        {
                            continue;
                        }

                        var name = string.IsNullOrWhiteSpace(phase.name) ? phase.id : phase.name;
                        var requests = phase.featureRequests ?? Array.Empty<ProtoFeatureRequest>();
                        var folderCount = CountType(requests, "folder");
                        var scriptCount = CountType(requests, "script");
                        var sceneCount = CountType(requests, "scene");
                        var prefabCount = CountType(requests, "prefab");
                        var materialCount = CountType(requests, "material");
                        var assetCount = CountType(requests, "asset");

                        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                        {
                            EditorGUILayout.LabelField($"{name} ({requests.Length} requests)", EditorStyles.boldLabel);
                            if (!string.IsNullOrWhiteSpace(phase.overview))
                            {
                                EditorGUILayout.LabelField(phase.overview, EditorStyles.wordWrappedLabel);
                            }
                            EditorGUILayout.LabelField($"Folders: {folderCount}  Scripts: {scriptCount}  Scenes: {sceneCount}  Prefabs: {prefabCount}  Materials: {materialCount}  Assets: {assetCount}");
                        }
                    }
                }
                else if (_planSnapshotSimple?.featureRequests != null && _planSnapshotSimple.featureRequests.Length > 0)
                {
                    var planRequests = _planSnapshotSimple.featureRequests;
                    var folderCount = CountType(planRequests, "folder");
                    var scriptCount = CountType(planRequests, "script");
                    var sceneCount = CountType(planRequests, "scene");
                    var prefabCount = CountType(planRequests, "prefab");
                    var materialCount = CountType(planRequests, "material");
                    var assetCount = CountType(planRequests, "asset");

                    EditorGUILayout.LabelField($"Requests: {planRequests.Length}");
                    EditorGUILayout.LabelField($"Folders: {folderCount}  Scripts: {scriptCount}  Scenes: {sceneCount}  Prefabs: {prefabCount}  Materials: {materialCount}  Assets: {assetCount}");
                }
                else
                {
                    EditorGUILayout.LabelField("Plan JSON could not be parsed.", EditorStyles.wordWrappedLabel);
                }

                _showPlanRaw = EditorGUILayout.Foldout(_showPlanRaw, "Raw JSON");
                if (_showPlanRaw)
                {
                    using (var scroll = new EditorGUILayout.ScrollViewScope(_planSnapshotScroll, GUILayout.MinHeight(80f)))
                    {
                        _planSnapshotScroll = scroll.scrollPosition;
                        EditorGUILayout.TextArea(_planSnapshotRaw, GUILayout.ExpandHeight(false));
                    }
                }
            }

            DrawRequestStatusPanel(_requests);
        }

        private void DrawRequestStatusPanel(List<ProtoFeatureRequest> requests)
        {
            var safeRequests = requests ?? new List<ProtoFeatureRequest>();
            var counts = BuildStatusCounts(safeRequests);

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Request Status", EditorStyles.boldLabel);
                EditorGUILayout.LabelField($"Total requests: {safeRequests.Count}");

                using (new EditorGUILayout.HorizontalScope())
                {
                    DrawStatusCount("Todo", counts, ProtoAgentRequestStatus.Todo);
                    DrawStatusCount("In Progress", counts, ProtoAgentRequestStatus.InProgress);
                    DrawStatusCount("Done", counts, ProtoAgentRequestStatus.Done);
                    DrawStatusCount("Blocked", counts, ProtoAgentRequestStatus.Blocked);
                }

                var blocked = new List<ProtoFeatureRequest>();
                foreach (var request in safeRequests)
                {
                    if (request.HasStatus(ProtoAgentRequestStatus.Blocked))
                    {
                        blocked.Add(request);
                    }
                }

                if (blocked.Count > 0)
                {
                    EditorGUILayout.LabelField("Blocked requests:", EditorStyles.boldLabel);
                    for (var i = 0; i < blocked.Count && i < 3; i++)
                    {
                        var request = blocked[i];
                        if (request == null)
                        {
                            continue;
                        }

                        EditorGUILayout.LabelField($"- {request.name} ({request.type})");
                    }

                    if (blocked.Count > 3)
                    {
                        EditorGUILayout.LabelField($"...and {blocked.Count - 3} more");
                    }
                }
            }
        }

        private static void DrawStatusCount(string label, Dictionary<ProtoAgentRequestStatus, int> counts, ProtoAgentRequestStatus status)
        {
            var value = GetStatusCount(counts, status);
            EditorGUILayout.LabelField($"{label}: {value}");
        }

        private static Dictionary<ProtoAgentRequestStatus, int> BuildStatusCounts(List<ProtoFeatureRequest> requests)
        {
            var counts = new Dictionary<ProtoAgentRequestStatus, int>();
            if (requests == null)
            {
                return counts;
            }

            foreach (var request in requests)
            {
                if (request == null)
                {
                    continue;
                }

                var status = request.status.ToStatus();
                counts.TryGetValue(status, out var current);
                counts[status] = current + 1;
            }

            return counts;
        }

        private static int GetStatusCount(Dictionary<ProtoAgentRequestStatus, int> counts, ProtoAgentRequestStatus status)
        {
            return counts.TryGetValue(status, out var value) ? value : 0;
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
            if (_statusFilter > 0 && _statusFilter <= StatusFilterValues.Length)
            {
                var status = StatusFilterValues[_statusFilter - 1];
                if (!request.HasStatus(status))
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
            LoadPlanSnapshot();
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

            request.SetStatus(ProtoAgentRequestStatus.Todo);
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

                    request.SetStatus(ProtoAgentRequestStatus.Todo);
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

        private void LoadPlanSnapshot()
        {
            _planSnapshotRaw = string.Empty;
            _planSnapshotPhase = null;
            _planSnapshotSimple = null;

            var path = ProtoPlanStorage.GetPlanRawPath();
            var fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath))
            {
                return;
            }

            var json = File.ReadAllText(fullPath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return;
            }

            _planSnapshotRaw = json;
            try
            {
                _planSnapshotPhase = JsonUtility.FromJson<ProtoPhasePlan>(json);
            }
            catch
            {
                _planSnapshotPhase = null;
            }

            if (_planSnapshotPhase == null || _planSnapshotPhase.phases == null || _planSnapshotPhase.phases.Length == 0)
            {
                try
                {
                    _planSnapshotSimple = JsonUtility.FromJson<ProtoGenerationPlan>(json);
                }
                catch
                {
                    _planSnapshotSimple = null;
                }
            }
        }

        private static int CountType(ProtoFeatureRequest[] requests, string type)
        {
            if (requests == null)
            {
                return 0;
            }

            var count = 0;
            foreach (var request in requests)
            {
                if (request == null)
                {
                    continue;
                }

                if (string.Equals(request.type, type, System.StringComparison.OrdinalIgnoreCase))
                {
                    count++;
                }
            }

            return count;
        }
    }
}
