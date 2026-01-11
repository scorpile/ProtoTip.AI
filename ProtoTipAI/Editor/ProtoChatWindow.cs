using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine;

namespace ProtoTipAI.Editor
{
    public sealed class ProtoChatWindow : EditorWindow
    {
        private const string RoleUser = "user";
        private const string RoleAssistant = "assistant";
        private const string ProjectRoot = "Assets/Project";
        private const int MaxScriptIndexEntries = 40;
        private const int MaxMembersPerScript = 8;
        private const int MaxScriptIndexChars = 6000;
        private const int MaxPrefabIndexEntries = 50;
        private const int MaxSceneIndexEntries = 40;
        private const int MaxAssetIndexEntries = 60;
        private const string PlanRawFileName = "PlanRaw.json";

        private static readonly Regex NamespaceRegex = new Regex(@"^\s*namespace\s+([A-Za-z0-9_.]+)", RegexOptions.Compiled);
        private static readonly Regex TypeRegex = new Regex(@"\b(?:(public|internal)\s+)?(?:static\s+|abstract\s+|sealed\s+|partial\s+)*\b(class|struct|interface|enum)\s+([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);
        private static readonly Regex MethodRegex = new Regex(@"\bpublic\s+(?:static\s+|virtual\s+|override\s+|abstract\s+|sealed\s+|async\s+)*([A-Za-z0-9_<>,\[\]\s]+)\s+([A-Za-z_][A-Za-z0-9_]*)\s*\(([^)]*)\)", RegexOptions.Compiled);
        private static readonly Regex PropertyRegex = new Regex(@"\bpublic\s+([A-Za-z0-9_<>,\[\]\s]+)\s+([A-Za-z_][A-Za-z0-9_]*)\s*\{\s*(get|set)", RegexOptions.Compiled);
        private static readonly Regex FieldRegex = new Regex(@"\bpublic\s+([A-Za-z0-9_<>,\[\]\s]+)\s+([A-Za-z_][A-Za-z0-9_]*)\s*(=|;)", RegexOptions.Compiled);

        private readonly List<ProtoChatMessage> _messages = new List<ProtoChatMessage>();
        private Vector2 _scroll;
        private string _input = string.Empty;
        private bool _isSending;
        private string _status = string.Empty;
        private bool _includeSelection = true;
        private bool _includeScene = true;
        private bool _includeRecentAssets = true;
        private bool _includeConsole = true;
        private string _planPrompt = string.Empty;
        private string _lastPlanJson = string.Empty;
        private bool _showRawPlan;
        private Vector2 _planScroll;
        private bool _overwriteScript;
        private bool _isGeneratingPlan;
        private bool _isCreatingRequests;
        private bool _isApplyingPlan;
        private bool _isApplyingStage;
        private string _toolStatus = string.Empty;
        private ProtoGenerationPlan _cachedPlan;
        private ProtoPhasePlan _cachedPhasePlan;
        private string[] _phaseLabels = Array.Empty<string>();
        private int _selectedPhaseIndex;
        private bool _isApiWaiting;
        private string _apiWaitLabel = string.Empty;
        private double _apiWaitStarted;
        private bool _apiWaitHooked;
        private int _apiWaitTimeoutSeconds;
        private CancellationTokenSource _operationCts;
        private bool _cancelRequested;
        private bool _isAutoRefreshDeferred;
        private ScriptContractIndex _scriptIndexCache;
        private bool _scriptIndexDirty = true;
        private static readonly Queue<SceneLayoutJob> SceneLayoutQueue = new Queue<SceneLayoutJob>();
        private static bool _isSceneLayoutRunning;
        private bool _isFixingErrors;
        private int _fixPassIterations = 2;

        [MenuItem("Proto/Chat")]
        public static void ShowWindow()
        {
            var inspectorType = typeof(EditorWindow).Assembly.GetType("UnityEditor.InspectorWindow");
            if (inspectorType != null)
            {
                GetWindow<ProtoChatWindow>("Proto Chat", false, new[] { inspectorType });
            }
            else
            {
                GetWindow<ProtoChatWindow>("Proto Chat");
            }
        }

        private void OnEnable()
        {
            RestorePlanFromDisk();
        }

        private void OnGUI()
        {
            DrawHeader();
            DrawContextOptions();
            DrawAgentTools();
            DrawChatHistory();
            DrawInputArea();
        }

        private void DrawHeader()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label("OpenAI", EditorStyles.toolbarButton);
                GUILayout.FlexibleSpace();
                GUILayout.Label($"Model: {ProtoOpenAISettings.GetModel()}", EditorStyles.miniLabel);
                if (GUILayout.Button("Clear", EditorStyles.toolbarButton))
                {
                    _messages.Clear();
                    _status = string.Empty;
                }
            }

            if (!ProtoOpenAISettings.HasToken())
            {
                EditorGUILayout.HelpBox("Missing OpenAI token. Open Setup to configure.", MessageType.Warning);
                if (GUILayout.Button("Open Setup"))
                {
                    ProtoSetupWindow.ShowWindow();
                }
            }
        }

        private void DrawChatHistory()
        {
            using (var scroll = new EditorGUILayout.ScrollViewScope(_scroll))
            {
                _scroll = scroll.scrollPosition;

                foreach (var message in _messages)
                {
                    var title = message.role == RoleUser ? "You" : "Assistant";
                    EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
                    EditorGUILayout.LabelField(message.content, EditorStyles.wordWrappedLabel);
                    EditorGUILayout.Space(6f);
                }

                if (!string.IsNullOrWhiteSpace(_status))
                {
                    EditorGUILayout.HelpBox(_status, MessageType.Info);
                }
            }
        }

        private void DrawContextOptions()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Context", EditorStyles.boldLabel);
                _includeSelection = EditorGUILayout.ToggleLeft("Include Selection", _includeSelection);
                _includeScene = EditorGUILayout.ToggleLeft("Include Active Scene", _includeScene);
                _includeRecentAssets = EditorGUILayout.ToggleLeft("Include Recent Assets", _includeRecentAssets);
                _includeConsole = EditorGUILayout.ToggleLeft("Include Console", _includeConsole);

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Refresh Summary", GUILayout.Width(140f)))
                    {
                        var summary = ProtoProjectContext.BuildProjectSummary();
                        ProtoOpenAISettings.SetProjectSummary(summary);
                        _status = "Project summary refreshed.";
                    }

                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("Open Setup", GUILayout.Width(120f)))
                    {
                        ProtoSetupWindow.ShowWindow();
                    }
                }

                if (string.IsNullOrWhiteSpace(ProtoOpenAISettings.GetProjectGoal()))
                {
                    EditorGUILayout.HelpBox("Project Goal is empty. Set it in Setup for better context.", MessageType.Info);
                }
            }
        }

        private void DrawAgentTools()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Agent Tools", EditorStyles.boldLabel);
                EditorGUILayout.LabelField("Plan Prompt");
                _planPrompt = EditorGUILayout.TextArea(_planPrompt, GUILayout.MinHeight(50f));
                _overwriteScript = EditorGUILayout.ToggleLeft("Overwrite scripts if they exist", _overwriteScript);
                _fixPassIterations = EditorGUILayout.IntSlider("Fix Step Iterations", _fixPassIterations, 1, 5);

                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(_isGeneratingPlan || !ProtoOpenAISettings.HasToken()))
                    {
                        if (GUILayout.Button(_isGeneratingPlan ? "Planning..." : "Generate Plan", GUILayout.Width(140f)))
                        {
                            _ = GeneratePlanAsync();
                        }
                    }

                using (new EditorGUI.DisabledScope(_isCreatingRequests || string.IsNullOrWhiteSpace(_lastPlanJson)))
                {
                    var createLabel = _cachedPhasePlan != null && _cachedPhasePlan.phases != null && _cachedPhasePlan.phases.Length > 0
                        ? "Create Phase Requests"
                        : "Create Feature Requests";
                    if (GUILayout.Button(_isCreatingRequests ? "Creating..." : createLabel, GUILayout.Width(170f)))
                    {
                        _ = CreateFeatureRequestsAsync(_lastPlanJson);
                    }
                }

                    using (new EditorGUI.DisabledScope(_isApplyingPlan))
                    {
                        if (GUILayout.Button(_isApplyingPlan ? "Applying..." : "Apply Plan", GUILayout.Width(120f)))
                        {
                            _ = ApplyPlanAsync();
                        }
                    }

                    using (new EditorGUI.DisabledScope(_isFixingErrors || !ProtoOpenAISettings.HasToken()))
                    {
                        if (GUILayout.Button(_isFixingErrors ? "Fixing..." : "Fix Step", GUILayout.Width(120f)))
                        {
                            _ = RunFixPassAsync();
                        }
                    }

                    using (new EditorGUI.DisabledScope(!IsOperationActive()))
                    {
                        if (GUILayout.Button("Cancel", GUILayout.Width(90f)))
                        {
                            RequestCancel();
                        }
                    }

                    GUILayout.FlexibleSpace();
                    using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(_lastPlanJson)))
                    {
                        if (GUILayout.Button("Clear Plan", GUILayout.Width(120f)))
                        {
                            _lastPlanJson = string.Empty;
                            _cachedPlan = null;
                            _cachedPhasePlan = null;
                            _phaseLabels = Array.Empty<string>();
                            _selectedPhaseIndex = 0;
                            SavePlanRawToDisk(string.Empty);
                        }
                    }
                }

                DrawPlanPreview();

                DrawStageButtons();

                if (_phaseLabels != null && _phaseLabels.Length > 0)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField("Phase", GUILayout.Width(60f));
                        _selectedPhaseIndex = EditorGUILayout.Popup(_selectedPhaseIndex, _phaseLabels, GUILayout.Width(220f));
                    }
                }

                if (_isApiWaiting)
                {
                    var elapsed = EditorApplication.timeSinceStartup - _apiWaitStarted;
                    var timeoutLabel = _apiWaitTimeoutSeconds > 0 ? $" / {_apiWaitTimeoutSeconds}s" : string.Empty;
                    EditorGUILayout.HelpBox($"Waiting for OpenAI: {_apiWaitLabel} ({elapsed:0}s{timeoutLabel})", MessageType.Info);
                }

                if (!string.IsNullOrWhiteSpace(_toolStatus))
                {
                    EditorGUILayout.HelpBox(_toolStatus, MessageType.Info);
                }
            }
        }

        private void DrawStageButtons()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Plan Stages", EditorStyles.boldLabel);

                using (new EditorGUI.DisabledScope(_isApplyingStage))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button("Create Folders", GUILayout.Width(140f)))
                        {
                            _ = ApplyPlanStageAsync("folders", new[] { "folder" });
                        }
                        if (GUILayout.Button("Create Scripts", GUILayout.Width(140f)))
                        {
                            _ = ApplyPlanStageAsync("scripts", new[] { "script" });
                        }
                        if (GUILayout.Button("Create Materials", GUILayout.Width(140f)))
                        {
                            _ = ApplyPlanStageAsync("materials", new[] { "material" });
                        }
                    }

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button("Create Prefabs", GUILayout.Width(140f)))
                        {
                            _ = ApplyPlanStageAsync("prefabs", new[] { "prefab" });
                        }
                        if (GUILayout.Button("Create Scenes", GUILayout.Width(140f)))
                        {
                            _ = ApplyPlanStageAsync("scenes", new[] { "scene" });
                        }
                        if (GUILayout.Button("Create Assets", GUILayout.Width(140f)))
                        {
                            _ = ApplyPlanStageAsync("assets", new[] { "asset" });
                        }
                    }
                }
            }
        }

        private void DrawInputArea()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Message", EditorStyles.boldLabel);
                _input = EditorGUILayout.TextArea(_input, GUILayout.MinHeight(60f));

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.FlexibleSpace();
                    using (new EditorGUI.DisabledScope(_isSending || !ProtoOpenAISettings.HasToken() || string.IsNullOrWhiteSpace(_input)))
                    {
                        if (GUILayout.Button(_isSending ? "Sending..." : "Send", GUILayout.Width(120f)))
                        {
                            var text = _input.Trim();
                            _input = string.Empty;
                            _ = SendAsync(text);
                        }
                    }
                }
            }
        }

        private async Task SendAsync(string userText)
        {
            if (string.IsNullOrWhiteSpace(userText))
            {
                return;
            }

            if (IsReviewScriptCommand(userText))
            {
                await ReviewScriptAsync(userText).ConfigureAwait(false);
                return;
            }

            _isSending = true;
            _status = "Sending message...";
            _cancelRequested = false;
            var token = BeginOperation("chat");
            _apiWaitTimeoutSeconds = 320;
            _messages.Add(new ProtoChatMessage { role = RoleUser, content = userText });
            var requestMessages = _messages.ToArray();
            var assistantIndex = _messages.Count;
            _messages.Add(new ProtoChatMessage { role = RoleAssistant, content = "..." });
            Repaint();

            try
            {
                var settings = await RunOnMainThread(GetSettingsSnapshot).ConfigureAwait(false);
                await RunOnMainThread(() => BeginApiWait("chat response")).ConfigureAwait(false);
                var response = await ProtoOpenAIClient.SendChatAsync(settings.token, settings.model, BuildRequestMessages(requestMessages), _apiWaitTimeoutSeconds, token).ConfigureAwait(false);

                _messages[assistantIndex].content = response;
                _status = string.Empty;
            }
            catch (OperationCanceledException)
            {
                _messages[assistantIndex].content = "Canceled.";
                _status = "Canceled.";
            }
            catch (Exception ex)
            {
                _messages[assistantIndex].content = "Error getting response.";
                _status = ex.Message;
            }
            finally
            {
                await RunOnMainThread(EndApiWait).ConfigureAwait(false);
                EndOperation();
                _isSending = false;
                Repaint();
            }
        }

        private async Task ReviewScriptAsync(string userText)
        {
            _isSending = true;
            _status = "Reviewing script...";
            _cancelRequested = false;
            var token = BeginOperation("review");
            _apiWaitTimeoutSeconds = 320;

            _messages.Add(new ProtoChatMessage { role = RoleUser, content = userText });
            var assistantIndex = _messages.Count;
            _messages.Add(new ProtoChatMessage { role = RoleAssistant, content = "..." });
            Repaint();

            try
            {
                var settings = await RunOnMainThread(GetSettingsSnapshot).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(settings.token))
                {
                    _messages[assistantIndex].content = "Missing OpenAI token.";
                    _status = "Missing OpenAI token.";
                    return;
                }

                var resolution = await RunOnMainThread(() => ResolveScriptPath(userText)).ConfigureAwait(false);
                if (!resolution.success)
                {
                    _messages[assistantIndex].content = resolution.error;
                    _status = resolution.error;
                    return;
                }

                var assetPath = resolution.assetPath;
                var fullPath = Path.GetFullPath(assetPath);
                if (!File.Exists(fullPath))
                {
                    _messages[assistantIndex].content = $"Script not found at {assetPath}.";
                    _status = "Script not found.";
                    return;
                }

                var original = File.ReadAllText(fullPath);
                var baseMessages = await RunOnMainThread(() => BuildBaseContextMessages(false).ToArray()).ConfigureAwait(false);

                var messages = new List<ProtoChatMessage>(baseMessages ?? Array.Empty<ProtoChatMessage>());
                messages.Add(new ProtoChatMessage
                {
                    role = "user",
                    content = "Review the Unity C# script. If no changes are needed, respond with 'NO_CHANGES' and a brief reason. If changes are needed, first list up to 5 brief bullets, then output the full corrected file in a single ```csharp``` code block. Keep the class name and file intent consistent."
                });
                messages.Add(new ProtoChatMessage
                {
                    role = "user",
                    content = $"Script Path: {assetPath}\n\n{original}"
                });

                await RunOnMainThread(() => BeginApiWait($"review {Path.GetFileName(assetPath)}")).ConfigureAwait(false);
                var response = await ProtoOpenAIClient.SendChatAsync(settings.token, settings.model, messages.ToArray(), _apiWaitTimeoutSeconds, token).ConfigureAwait(false);
                await RunOnMainThread(EndApiWait).ConfigureAwait(false);

                var summary = ExtractReviewSummary(response);
                var code = ExtractCode(response);

                if (string.IsNullOrWhiteSpace(code))
                {
                    if (response.IndexOf("NO_CHANGES", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        _messages[assistantIndex].content = string.IsNullOrWhiteSpace(summary)
                            ? "NO_CHANGES."
                            : summary;
                        _status = string.Empty;
                        return;
                    }

                    _messages[assistantIndex].content = "Review failed: no code returned.";
                    _status = "Review failed.";
                    return;
                }

                var normalizedOriginal = NormalizeLineEndings(original);
                var normalizedNew = NormalizeLineEndings(code);
                if (!string.Equals(normalizedOriginal, normalizedNew, StringComparison.Ordinal))
                {
                    await RunAssetEditingAsync(() => File.WriteAllText(fullPath, code), true).ConfigureAwait(false);
                    _scriptIndexDirty = true;
                    if (string.IsNullOrWhiteSpace(summary))
                    {
                        summary = $"Updated {assetPath}.";
                    }
                    else
                    {
                        summary = $"{summary}\nUpdated {assetPath}.";
                    }
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(summary))
                    {
                        summary = "No changes needed.";
                    }
                    else
                    {
                        summary = $"{summary}\nNo changes applied.";
                    }
                }

                _messages[assistantIndex].content = summary;
                _status = string.Empty;
            }
            catch (OperationCanceledException)
            {
                _messages[assistantIndex].content = "Canceled.";
                _status = "Canceled.";
            }
            catch (Exception ex)
            {
                _messages[assistantIndex].content = "Error reviewing script.";
                _status = ex.Message;
            }
            finally
            {
                await RunOnMainThread(EndApiWait).ConfigureAwait(false);
                EndOperation();
                _isSending = false;
                Repaint();
            }
        }

        private List<ProtoChatMessage> BuildBaseContextMessages(bool includeDynamicContext)
        {
            var messages = new List<ProtoChatMessage>
            {
                new ProtoChatMessage
                {
                    role = "system",
                    content = ProtoProjectContext.BuildSystemContext()
                }
            };

            var projectGoal = ProtoOpenAISettings.GetProjectGoal();
            if (!string.IsNullOrWhiteSpace(projectGoal))
            {
                messages.Add(new ProtoChatMessage
                {
                    role = "system",
                    content = $"Project Goal:\n{projectGoal.Trim()}"
                });
            }

            var summary = ProtoOpenAISettings.GetProjectSummary();
            if (string.IsNullOrWhiteSpace(summary))
            {
                summary = ProtoProjectContext.BuildProjectSummary();
                ProtoOpenAISettings.SetProjectSummary(summary);
            }

            if (!string.IsNullOrWhiteSpace(summary))
            {
                messages.Add(new ProtoChatMessage
                {
                    role = "system",
                    content = $"Project Summary:\n{summary.Trim()}"
                });
            }

            if (includeDynamicContext)
            {
                var dynamicContext = ProtoProjectContext.BuildDynamicContext(_includeSelection, _includeScene, _includeRecentAssets, _includeConsole);
                if (!string.IsNullOrWhiteSpace(dynamicContext))
                {
                    messages.Add(new ProtoChatMessage
                    {
                        role = "user",
                        content = $"Context Snapshot:\n{dynamicContext.Trim()}"
                    });
                }
            }

            return messages;
        }

        private ProtoChatMessage[] BuildRequestMessages(ProtoChatMessage[] history)
        {
            var messages = BuildBaseContextMessages(true);
            messages.AddRange(history);
            return messages.ToArray();
        }

        private void RestorePlanFromDisk()
        {
            var fullPath = Path.GetFullPath(GetPlanRawPath());
            if (!File.Exists(fullPath))
            {
                return;
            }

            var json = File.ReadAllText(fullPath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return;
            }

            ApplyPlanJsonToState(json);
        }

        private void SavePlanRawToDisk(string json)
        {
            var fullPath = Path.GetFullPath(GetPlanRawPath());
            if (string.IsNullOrWhiteSpace(json))
            {
                if (File.Exists(fullPath))
                {
                    File.Delete(fullPath);
                    AssetDatabase.Refresh();
                }
                return;
            }

            File.WriteAllText(fullPath, json);
            AssetDatabase.Refresh();
        }

        private static string GetPlanRawPath()
        {
            var folder = ProtoFeatureRequestStore.EnsureFeatureRequestFolder();
            return $"{folder}/{PlanRawFileName}";
        }

        private void ApplyPlanJsonToState(string json)
        {
            var cleaned = json ?? string.Empty;
            if (string.IsNullOrWhiteSpace(cleaned))
            {
                return;
            }

            _lastPlanJson = cleaned;
            _cachedPlan = null;
            _cachedPhasePlan = null;
            _phaseLabels = Array.Empty<string>();
            _selectedPhaseIndex = 0;

            try
            {
                var phasePlan = JsonUtility.FromJson<ProtoPhasePlan>(cleaned);
                if (phasePlan != null && phasePlan.phases != null && phasePlan.phases.Length > 0)
                {
                    _cachedPhasePlan = phasePlan;
                    _phaseLabels = BuildPhaseLabels(phasePlan);
                    return;
                }
            }
            catch
            {
                // Ignore parse errors and fall back to non-phased plan.
            }

            var plan = ParsePlan(cleaned);
            if (plan != null && plan.featureRequests != null)
            {
                _cachedPlan = plan;
            }
        }

        private async Task GeneratePlanAsync()
        {
            _isGeneratingPlan = true;
            _toolStatus = "Generating plan...";
            _cancelRequested = false;
            var token = BeginOperation("plan");
            _apiWaitTimeoutSeconds = 320;
            Repaint();
            try
            {
                var settings = await RunOnMainThread(GetSettingsSnapshot).ConfigureAwait(false);
                var prompt = string.IsNullOrWhiteSpace(_planPrompt)
                    ? "Create the folder structure and list of Unity C# scripts needed first."
                    : _planPrompt.Trim();

                var scriptIndex = await RunOnMainThread(BuildScriptIndexMarkdown).ConfigureAwait(false);
                var prefabIndex = await RunOnMainThread(BuildPrefabIndexMarkdown).ConfigureAwait(false);
                var sceneIndex = await RunOnMainThread(BuildSceneIndexMarkdown).ConfigureAwait(false);
                var assetIndex = await RunOnMainThread(BuildAssetIndexMarkdown).ConfigureAwait(false);
                var baseMessages = await RunOnMainThread(() => BuildBaseContextMessages(true).ToArray()).ConfigureAwait(false);
                var phasePlan = await GeneratePhasedPlanAsync(settings.token, settings.model, baseMessages, prompt, token, scriptIndex, prefabIndex, sceneIndex, assetIndex).ConfigureAwait(false);

                EditorApplication.delayCall += () =>
                {
                    if (phasePlan == null || phasePlan.phases == null || phasePlan.phases.Length == 0)
                    {
                        _toolStatus = "Model returned empty phase plan.";
                    }
                    else
                    {
                        _cachedPhasePlan = phasePlan;
                        _cachedPlan = null;
                        _lastPlanJson = JsonUtility.ToJson(phasePlan, true);
                        SavePlanRawToDisk(_lastPlanJson);
                        _phaseLabels = BuildPhaseLabels(phasePlan);
                        _selectedPhaseIndex = 0;
                        _toolStatus = "Phase plan ready. Review and create phase requests.";
                    }

                    _isGeneratingPlan = false;
                    EndOperation();
                    Repaint();
                };
            }
            catch (OperationCanceledException)
            {
                EditorApplication.delayCall += () =>
                {
                    _toolStatus = "Canceled.";
                    _isGeneratingPlan = false;
                    EndOperation();
                    EndApiWait();
                    Repaint();
                };
            }
            catch (Exception ex)
            {
                EditorApplication.delayCall += () =>
                {
                    _toolStatus = ex.Message;
                    _isGeneratingPlan = false;
                    EndApiWait();
                    EndOperation();
                    Repaint();
                };
            }
        }

        private async Task CreateFeatureRequestsAsync(string json)
        {
            _isCreatingRequests = true;
            _toolStatus = "Creating feature requests...";
            Repaint();

            try
            {
                if (_cachedPhasePlan != null && _cachedPhasePlan.phases != null && _cachedPhasePlan.phases.Length > 0)
                {
                    if (_selectedPhaseIndex > _cachedPhasePlan.phases.Length)
                    {
                        _selectedPhaseIndex = 0;
                    }
                    var phaseRequests = CollectPhaseRequests(_cachedPhasePlan, _selectedPhaseIndex);
                    if (phaseRequests.Count == 0)
                    {
                        _toolStatus = "No phase requests found.";
                        _isCreatingRequests = false;
                        Repaint();
                        return;
                    }

                    var featureRequests = BuildFeatureRequestList(phaseRequests.ToArray());
                    featureRequests = FilterExistingScriptRequests(featureRequests);
                    featureRequests = PreflightRequestsForWrite(featureRequests);
                    await RunOnMainThread(() => WriteFeatureRequests(featureRequests)).ConfigureAwait(false);
                }
                else
                {
                    var plan = _cachedPlan ?? ParsePlan(json);
                    if (plan == null || plan.featureRequests == null)
                    {
                        _toolStatus = "Plan JSON could not be parsed.";
                        _isCreatingRequests = false;
                        Repaint();
                        return;
                    }

                    var featureRequests = BuildFeatureRequestList(plan.featureRequests);
                    featureRequests = FilterExistingScriptRequests(featureRequests);
                    featureRequests = PreflightRequestsForWrite(featureRequests);
                    await RunOnMainThread(() => WriteFeatureRequests(featureRequests)).ConfigureAwait(false);
                }

                await RunOnMainThread(() =>
                {
                    _toolStatus = "Feature requests created.";
                    _isCreatingRequests = false;
                    Repaint();
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                EditorApplication.delayCall += () =>
                {
                    _toolStatus = ex.Message;
                    _isCreatingRequests = false;
                    Repaint();
                };
            }
        }

        private async Task ApplyPlanAsync()
        {
            _isApplyingPlan = true;
            _toolStatus = "Applying feature requests...";
            _cancelRequested = false;
            var token = BeginOperation("apply plan");
            Repaint();

            try
            {
                var featureRequests = await RunOnMainThread(LoadFeatureRequestsFromDisk).ConfigureAwait(false);
                if (featureRequests == null || featureRequests.Count == 0)
                {
                    _toolStatus = "No feature requests found. Create them first.";
                    _isApplyingPlan = false;
                    EndOperation();
                    Repaint();
                    return;
                }

                featureRequests = await RunOnMainThread(() => PreflightRequestsForExecution(featureRequests)).ConfigureAwait(false);
                _scriptIndexDirty = true;
                var requestLookup = BuildRequestLookup(featureRequests);

                await RunOnMainThread(BeginAutoRefreshBlock).ConfigureAwait(false);
                try
                {
                    await ExecuteFeatureRequestsAsync(featureRequests, requestLookup, token).ConfigureAwait(false);
                    if (!token.IsCancellationRequested)
                    {
                        await RetryBlockedRequestsAsync(featureRequests, requestLookup, token).ConfigureAwait(false);
                    }
                }
                finally
                {
                    await RunOnMainThread(() => EndAutoRefreshBlock(true)).ConfigureAwait(false);
                }

                await RunOnMainThread(() => SchedulePrefabComponentAttachment(featureRequests, requestLookup)).ConfigureAwait(false);

                await RunOnMainThread(() =>
                {
                    _toolStatus = "Feature requests applied.";
                    _isApplyingPlan = false;
                    EndOperation();
                    Repaint();
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                EditorApplication.delayCall += () =>
                {
                    _toolStatus = "Canceled.";
                    _isApplyingPlan = false;
                    EndAutoRefreshBlock(true);
                    EndOperation();
                    Repaint();
                };
            }
            catch (Exception ex)
            {
                EditorApplication.delayCall += () =>
                {
                    _toolStatus = ex.Message;
                    _isApplyingPlan = false;
                    EndAutoRefreshBlock(true);
                    EndOperation();
                    Repaint();
                };
            }
        }

        private async Task RunFixPassAsync()
        {
            _isFixingErrors = true;
            _toolStatus = "Fix pass running...";
            _cancelRequested = false;
            var token = BeginOperation("fix pass");
            _apiWaitTimeoutSeconds = 320;
            Repaint();

            try
            {
                for (var iteration = 0; iteration < _fixPassIterations; iteration++)
                {
                    token.ThrowIfCancellationRequested();
                    var errors = await RunOnMainThread(GetConsoleErrorItems).ConfigureAwait(false);
                    if (errors.Count == 0)
                    {
                        _toolStatus = "Fix pass complete. No console errors.";
                        break;
                    }

                    _toolStatus = $"Fixing errors (pass {iteration + 1}/{_fixPassIterations})...";
                    EditorApplication.delayCall += Repaint;

                    var scriptIndex = await RunOnMainThread(BuildScriptIndexMarkdown).ConfigureAwait(false);
                    await RunOnMainThread(BeginAutoRefreshBlock).ConfigureAwait(false);
                    var settings = await RunOnMainThread(GetSettingsSnapshot).ConfigureAwait(false);
                    foreach (var error in errors)
                    {
                        token.ThrowIfCancellationRequested();
                        if (string.IsNullOrWhiteSpace(error.filePath))
                        {
                            continue;
                        }

                        await FixScriptErrorAsync(error, settings.token, settings.model, token, scriptIndex).ConfigureAwait(false);
                    }

                    await RunOnMainThread(() => EndAutoRefreshBlock(true)).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                _toolStatus = "Fix pass canceled.";
            }
            catch (Exception ex)
            {
                _toolStatus = $"Fix pass failed: {ex.Message}";
            }
            finally
            {
                _isFixingErrors = false;
                EndOperation();
                Repaint();
            }
        }

        private async Task FixScriptErrorAsync(ConsoleErrorItem error, string apiToken, string model, CancellationToken token, string scriptIndex)
        {
            var assetPath = NormalizeErrorAssetPath(error.filePath);
            if (string.IsNullOrWhiteSpace(assetPath))
            {
                return;
            }

            var fullPath = Path.GetFullPath(assetPath);
            if (!File.Exists(fullPath))
            {
                return;
            }

            var original = File.ReadAllText(fullPath);
            var baseMessages = await RunOnMainThread(() => BuildBaseContextMessages(false).ToArray()).ConfigureAwait(false);
            var messages = new List<ProtoChatMessage>(baseMessages);
            var clampedIndex = ClampScriptIndex(scriptIndex);
            AppendScriptIndexMessage(messages, clampedIndex);

            var missingType = ExtractMissingTypeName(error.message);
            if (!string.IsNullOrWhiteSpace(missingType))
            {
                var existsInIndex = ScriptIndexContainsType(clampedIndex, missingType);
                messages.Add(new ProtoChatMessage
                {
                    role = "system",
                    content = existsInIndex
                        ? $"Type '{missingType}' exists in Script Index. Use the existing type or correct the reference."
                        : $"Type '{missingType}' not found in Script Index. If needed, add a minimal definition in this file or replace with a valid type."
                });
            }

            messages.Add(new ProtoChatMessage
            {
                role = "system",
                content = "Unity constraints: attributes like [Header], [Tooltip], [Range], [SerializeField] only apply to fields/properties that Unity serializes (not events, methods, or local variables). Use UnityEvent for inspector-exposed events. MonoBehaviour class name must match the file name. Avoid constructors; use Awake/Start. Do not use Unity API from constructors or field initializers."
            });

            messages.Add(new ProtoChatMessage
            {
                role = "user",
                content = "Fix the Unity C# script for the provided compile error. Output only full corrected code in a single ```csharp``` block. Do not add explanations. Use the Script Index to resolve names; avoid inventing types. If you must introduce a missing type, prefer a minimal enum/class in this file."
            });
            messages.Add(new ProtoChatMessage
            {
                role = "user",
                content = $"Error: {error.message}\nFile: {assetPath}\nLine: {error.line}\n\n{original}"
            });

            await RunOnMainThread(() => BeginApiWait($"fix {Path.GetFileName(assetPath)}")).ConfigureAwait(false);
            var response = await ProtoOpenAIClient.SendChatAsync(
                apiToken,
                model,
                messages.ToArray(),
                _apiWaitTimeoutSeconds,
                token).ConfigureAwait(false);
            await RunOnMainThread(EndApiWait).ConfigureAwait(false);

            var code = ExtractCode(response);
            if (string.IsNullOrWhiteSpace(code))
            {
                return;
            }

            if (!string.Equals(NormalizeLineEndings(original), NormalizeLineEndings(code), StringComparison.Ordinal))
            {
                await RunAssetEditingAsync(() => File.WriteAllText(fullPath, code), true).ConfigureAwait(false);
                _scriptIndexDirty = true;
            }
        }

        private static string NormalizeErrorAssetPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            var normalized = path.Replace('\\', '/');
            if (normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                return normalized;
            }

            var assetsIndex = normalized.IndexOf("/Assets/", StringComparison.OrdinalIgnoreCase);
            if (assetsIndex >= 0)
            {
                return normalized.Substring(assetsIndex + 1);
            }

            return normalized.StartsWith("Assets", StringComparison.OrdinalIgnoreCase) ? normalized : string.Empty;
        }

        private static List<ConsoleErrorItem> GetConsoleErrorItems()
        {
            var results = new List<ConsoleErrorItem>();
            var logEntriesType = Type.GetType("UnityEditor.LogEntries, UnityEditor");
            var logEntryType = Type.GetType("UnityEditor.LogEntry, UnityEditor");
            if (logEntriesType == null || logEntryType == null)
            {
                return results;
            }

            var getCount = logEntriesType.GetMethod("GetCount", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            var getEntry = logEntriesType.GetMethod("GetEntryInternal", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            if (getCount == null || getEntry == null)
            {
                return results;
            }

            var count = (int)getCount.Invoke(null, null);
            if (count == 0)
            {
                return results;
            }

            var entry = Activator.CreateInstance(logEntryType);
            var conditionField = logEntryType.GetField("condition", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
            var fileField = logEntryType.GetField("file", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
            var lineField = logEntryType.GetField("line", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
            var modeField = logEntryType.GetField("mode", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
            var typeField = logEntryType.GetField("type", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);

            for (var i = 0; i < count; i++)
            {
                getEntry.Invoke(null, new[] { i, entry });
                var message = conditionField?.GetValue(entry) as string ?? string.Empty;
                var file = fileField?.GetValue(entry) as string ?? string.Empty;
                var line = 0;
                if (lineField != null)
                {
                    line = Convert.ToInt32(lineField.GetValue(entry));
                }

                results.Add(new ConsoleErrorItem
                {
                    message = message,
                    filePath = file,
                    line = line
                });
            }

            return results;
        }

        private struct ConsoleErrorItem
        {
            public string message;
            public string filePath;
            public int line;
        }

        private async Task ApplyPlanStageAsync(string label, string[] types)
        {
            _isApplyingStage = true;
            _toolStatus = $"Applying {label}...";
            _cancelRequested = false;
            var token = BeginOperation($"apply {label}");
            Repaint();

            try
            {
                var featureRequests = await RunOnMainThread(LoadFeatureRequestsFromDisk).ConfigureAwait(false);
                if (featureRequests == null || featureRequests.Count == 0)
                {
                    _toolStatus = "No feature requests found. Create them first.";
                    _isApplyingStage = false;
                    EndOperation();
                    Repaint();
                    return;
                }

                featureRequests = await RunOnMainThread(() => PreflightRequestsForExecution(featureRequests)).ConfigureAwait(false);
                _scriptIndexDirty = true;
                var requestLookup = BuildRequestLookup(featureRequests);
                var stageRequests = FilterRequestsByTypes(featureRequests, types);
                if (stageRequests.Count == 0)
                {
                    _toolStatus = $"No {label} requests found.";
                    _isApplyingStage = false;
                    EndOperation();
                    Repaint();
                    return;
                }

                await RunOnMainThread(BeginAutoRefreshBlock).ConfigureAwait(false);
                try
                {
                    await ExecuteFeatureRequestsAsync(stageRequests, requestLookup, token).ConfigureAwait(false);
                    if (!token.IsCancellationRequested)
                    {
                        await RetryBlockedRequestsAsync(stageRequests, requestLookup, token).ConfigureAwait(false);
                    }
                }
                finally
                {
                    await RunOnMainThread(() => EndAutoRefreshBlock(true)).ConfigureAwait(false);
                }

                if (TypesContain(types, "prefab") || TypesContain(types, "asset"))
                {
                    await RunOnMainThread(() => SchedulePrefabComponentAttachment(stageRequests, requestLookup)).ConfigureAwait(false);
                }

                await RunOnMainThread(() =>
                {
                    _toolStatus = $"{label} applied.";
                    _isApplyingStage = false;
                    EndOperation();
                    Repaint();
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                EditorApplication.delayCall += () =>
                {
                    _toolStatus = "Canceled.";
                    _isApplyingStage = false;
                    EndOperation();
                    Repaint();
                };
            }
            catch (Exception ex)
            {
                EditorApplication.delayCall += () =>
                {
                    _toolStatus = ex.Message;
                    _isApplyingStage = false;
                    EndOperation();
                    Repaint();
                };
            }
        }

        private static List<ProtoFeatureRequest> LoadFeatureRequestsFromDisk()
        {
            var results = new List<ProtoFeatureRequest>();
            var folder = ProtoFeatureRequestStore.EnsureFeatureRequestFolder();
            if (!AssetDatabase.IsValidFolder(folder))
            {
                return results;
            }

            var guids = AssetDatabase.FindAssets("t:TextAsset", new[] { folder });
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    var fullPath = Path.GetFullPath(path);
                    if (!File.Exists(fullPath))
                    {
                        continue;
                    }

                    var json = File.ReadAllText(fullPath);
                    var request = JsonUtility.FromJson<ProtoFeatureRequest>(json);
                    if (request != null)
                    {
                        if (string.IsNullOrWhiteSpace(request.id))
                        {
                            request.id = Path.GetFileNameWithoutExtension(path);
                        }
                        results.Add(request);
                    }
                }
                catch
                {
                    // Ignore malformed json files.
                }
            }

            return results;
        }

        private void DrawPlanPreview()
        {
            if (string.IsNullOrWhiteSpace(_lastPlanJson))
            {
                return;
            }

            if (_cachedPhasePlan != null && _cachedPhasePlan.phases != null && _cachedPhasePlan.phases.Length > 0)
            {
                DrawPhasePlanPreview(_cachedPhasePlan);
                return;
            }

            var plan = _cachedPlan ?? ParsePlan(_lastPlanJson);
            if (plan == null || plan.featureRequests == null)
            {
                EditorGUILayout.HelpBox("Plan preview unavailable. JSON could not be parsed. Check Raw JSON.", MessageType.Warning);
                _showRawPlan = EditorGUILayout.Foldout(_showRawPlan, "Raw JSON");
                if (_showRawPlan)
                {
                    EditorGUILayout.TextArea(_lastPlanJson, GUILayout.MinHeight(80f));
                }
                return;
            }

            var total = plan.featureRequests.Length;
            var folderCount = CountType(plan.featureRequests, "folder");
            var scriptCount = CountType(plan.featureRequests, "script");
            var sceneCount = CountType(plan.featureRequests, "scene");
            var assetCount = CountType(plan.featureRequests, "asset");
            var prefabCount = CountType(plan.featureRequests, "prefab");
            var materialCount = CountType(plan.featureRequests, "material");

            EditorGUILayout.LabelField("Plan Preview", EditorStyles.boldLabel);
            EditorGUILayout.LabelField($"Total: {total}  Folders: {folderCount}  Scripts: {scriptCount}  Scenes: {sceneCount}  Materials: {materialCount}  Assets: {assetCount}  Prefabs: {prefabCount}");

            using (var scroll = new EditorGUILayout.ScrollViewScope(_planScroll, GUILayout.MinHeight(120f)))
            {
                _planScroll = scroll.scrollPosition;

                foreach (var request in plan.featureRequests)
                {
                    if (request == null)
                    {
                        continue;
                    }

                    using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                    {
                        EditorGUILayout.LabelField($"{GetTypeIcon(request.type)} {request.name}", EditorStyles.boldLabel);
                        EditorGUILayout.LabelField($"Type: {request.type}  Status: {NormalizeStatus(request.status)}");
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
                    }
                }
            }

            _showRawPlan = EditorGUILayout.Foldout(_showRawPlan, "Raw JSON");
            if (_showRawPlan)
            {
                EditorGUILayout.TextArea(_lastPlanJson, GUILayout.MinHeight(80f));
            }
        }

        private void DrawPhasePlanPreview(ProtoPhasePlan phasePlan)
        {
            if (phasePlan == null || phasePlan.phases == null)
            {
                return;
            }

            var total = 0;
            foreach (var phase in phasePlan.phases)
            {
                if (phase?.featureRequests != null)
                {
                    total += phase.featureRequests.Length;
                }
            }

            EditorGUILayout.LabelField("Phase Plan Preview", EditorStyles.boldLabel);
            EditorGUILayout.LabelField($"Phases: {phasePlan.phases.Length}  Total Requests: {total}");

            using (var scroll = new EditorGUILayout.ScrollViewScope(_planScroll, GUILayout.MinHeight(120f)))
            {
                _planScroll = scroll.scrollPosition;

                for (var i = 0; i < phasePlan.phases.Length; i++)
                {
                    var phase = phasePlan.phases[i];
                    if (phase == null)
                    {
                        continue;
                    }

                    var requests = phase.featureRequests ?? Array.Empty<ProtoFeatureRequest>();
                    var folderCount = CountType(requests, "folder");
                    var scriptCount = CountType(requests, "script");
                    var sceneCount = CountType(requests, "scene");
                    var prefabCount = CountType(requests, "prefab");
                    var materialCount = CountType(requests, "material");
                    var assetCount = CountType(requests, "asset");

                    using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                    {
                        var title = string.IsNullOrWhiteSpace(phase.name) ? $"Phase {i + 1}" : $"Phase {i + 1}: {phase.name}";
                        EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
                        if (!string.IsNullOrWhiteSpace(phase.overview))
                        {
                            EditorGUILayout.LabelField(phase.overview, EditorStyles.wordWrappedLabel);
                        }
                        EditorGUILayout.LabelField($"Total: {requests.Length}  Folders: {folderCount}  Scripts: {scriptCount}  Scenes: {sceneCount}  Materials: {materialCount}  Assets: {assetCount}  Prefabs: {prefabCount}");
                    }
                }
            }

            _showRawPlan = EditorGUILayout.Foldout(_showRawPlan, "Raw JSON");
            if (_showRawPlan)
            {
                EditorGUILayout.TextArea(_lastPlanJson, GUILayout.MinHeight(80f));
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
                if (request != null && string.Equals(request.type, type, StringComparison.OrdinalIgnoreCase))
                {
                    count++;
                }
            }
            return count;
        }

        private static string GetTypeIcon(string type)
        {
            if (string.IsNullOrWhiteSpace(type))
            {
                return "[?]";
            }

            switch (type.Trim().ToLowerInvariant())
            {
                case "folder":
                    return "[Folder]";
                case "script":
                    return "[Script]";
                case "scene":
                    return "[Scene]";
                case "asset":
                    return "[Asset]";
                case "prefab":
                    return "[Prefab]";
                case "material":
                    return "[Material]";
                default:
                    return "[?]";
            }
        }

        private static string NormalizeStatus(string status)
        {
            return string.IsNullOrWhiteSpace(status) ? "todo" : status.Trim();
        }

        private async Task<bool> ApplyRequestAsync(ProtoFeatureRequest request, string token, string model, ProtoChatMessage[] baseMessages, Dictionary<string, ProtoFeatureRequest> requestLookup, CancellationToken cancellationToken)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.type))
            {
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();

            switch (request.type.Trim().ToLowerInvariant())
            {
                case "folder":
                    var folderError = string.Empty;
                    await RunAssetEditingAsync(() =>
                    {
                        if (!ProtoAssetUtility.TryEnsureFolder(request.path, out _, out folderError))
                        {
                            request.notes = AppendNote(request.notes, folderError);
                        }
                    }, false).ConfigureAwait(false);
                    return string.IsNullOrWhiteSpace(folderError);
                case "script":
                    if (string.IsNullOrWhiteSpace(token))
                    {
                        request.notes = AppendNote(request.notes, "Missing OpenAI token.");
                        return false;
                    }
                    return await GenerateScriptForRequestAsync(request, token, model, baseMessages, requestLookup, cancellationToken).ConfigureAwait(false);
                case "prefab":
                    var prefabError = string.Empty;
                    await RunAssetEditingAsync(() =>
                    {
                        if (!TryCreatePrefabForRequest(request, requestLookup, out prefabError))
                        {
                            request.notes = AppendNote(request.notes, prefabError);
                        }
                    }, false).ConfigureAwait(false);
                    return string.IsNullOrWhiteSpace(prefabError);
                case "material":
                    var materialError = string.Empty;
                    await RunAssetEditingAsync(() =>
                    {
                        if (!TryCreateMaterialForRequest(request, out materialError))
                        {
                            request.notes = AppendNote(request.notes, materialError);
                        }
                    }, false).ConfigureAwait(false);
                    return string.IsNullOrWhiteSpace(materialError);
                case "scene":
                    var sceneError = string.Empty;
                    var sceneName = GetSceneName(request);
                    var scenePath = BuildScenePath(request.path, sceneName, out _);
                    if (!string.IsNullOrWhiteSpace(token))
                    {
                        var suggestedNotes = await RequestSceneNotesAsync(request, requestLookup, token, model, cancellationToken).ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(suggestedNotes))
                        {
                            request.notes = suggestedNotes;
                            await RunOnMainThread(() => ProtoFeatureRequestStore.SaveRequest(request)).ConfigureAwait(false);
                        }
                    }
                    await RunAssetEditingAsync(() =>
                    {
                        if (!TryCreateSceneForRequest(request, requestLookup, out sceneError))
                        {
                            request.notes = AppendNote(request.notes, sceneError);
                        }
                    }, false).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(sceneError) && !string.IsNullOrWhiteSpace(token))
                    {
                        EnqueueSceneLayout(request, requestLookup, token, model, scenePath);
                    }
                    return string.IsNullOrWhiteSpace(sceneError);
                case "asset":
                    return await ApplyAssetFallbackAsync(request, requestLookup).ConfigureAwait(false);
                default:
                    request.notes = AppendNote(request.notes, "Type not supported by automation yet.");
                    return false;
            }
        }

        private async Task<bool> ApplyAssetFallbackAsync(ProtoFeatureRequest request, Dictionary<string, ProtoFeatureRequest> requestLookup)
        {
            if (request == null)
            {
                return false;
            }

            var error = string.Empty;
            var kind = DetectAssetKind(request);
            await RunAssetEditingAsync(() =>
            {
                var scriptableResult = TryCreateScriptableObjectAsset(request, requestLookup, out error);
                if (scriptableResult == ScriptableObjectAttemptResult.Success)
                {
                    return;
                }
                if (scriptableResult == ScriptableObjectAttemptResult.Failed)
                {
                    if (!string.IsNullOrWhiteSpace(error))
                    {
                        request.notes = AppendNote(request.notes, error);
                    }
                    return;
                }

                switch (kind)
                {
                    case AssetFallbackKind.Prefab:
                        TryCreatePrefabForRequest(request, requestLookup, out error);
                        break;
                    case AssetFallbackKind.Scene:
                        TryCreateSceneForRequest(request, requestLookup, out error);
                        break;
                    case AssetFallbackKind.Material:
                        TryCreateMaterialForRequest(request, out error);
                        break;
                    default:
                        error = "Asset type not supported by automation yet. Use type prefab/material/scene.";
                        break;
                }

                if (!string.IsNullOrWhiteSpace(error))
                {
                    request.notes = AppendNote(request.notes, error);
                }
            }, false).ConfigureAwait(false);

            return string.IsNullOrWhiteSpace(error);
        }

        private enum AssetFallbackKind
        {
            Unknown,
            Prefab,
            Scene,
            Material
        }

        private enum ScriptableObjectAttemptResult
        {
            NotApplicable,
            Success,
            Failed
        }

        private enum PrefabRecipe
        {
            Cube,
            Sphere,
            Capsule,
            Cylinder,
            Plane,
            Quad,
            CharacterController,
            Empty
        }

        private static bool TryCreatePrefabForRequest(ProtoFeatureRequest request, Dictionary<string, ProtoFeatureRequest> requestLookup, out string error)
        {
            error = string.Empty;
            if (request == null)
            {
                error = "Prefab request is missing.";
                return false;
            }

            var prefabName = GetPrefabName(request);
            var prefabPath = BuildPrefabPath(request.path, prefabName, out var folderPath);
            if (!ProtoAssetUtility.TryEnsureFolder(folderPath, out _, out var folderError))
            {
                error = folderError;
                return false;
            }

            var existingPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (existingPrefab != null)
            {
                if (!TryUpdateExistingPrefab(request, prefabName, prefabPath, out error))
                {
                    return false;
                }

                request.notes = AppendNote(request.notes, "Prefab exists; updated in place.");
                return true;
            }

            GameObject root;
            if (ShouldCreateUiPrefab(request))
            {
                root = CreateUiPrefabRoot(request, prefabName);
            }
            else
            {
                var recipe = GetPrefabRecipe(prefabName, request.notes);
                root = CreatePrefabRoot(recipe, prefabName);
            }
            if (root == null)
            {
                error = "Failed to create prefab root GameObject.";
                return false;
            }

            var savedPrefab = PrefabUtility.SaveAsPrefabAsset(root, prefabPath, out var success);
            UnityEngine.Object.DestroyImmediate(root);

            if (!success || savedPrefab == null)
            {
                error = "Failed to save prefab asset.";
                return false;
            }

            return true;
        }

        private static bool TryUpdateExistingPrefab(ProtoFeatureRequest request, string prefabName, string prefabPath, out string error)
        {
            error = string.Empty;
            GameObject root = null;
            try
            {
                root = PrefabUtility.LoadPrefabContents(prefabPath);
                if (root == null)
                {
                    error = "Failed to load prefab for update.";
                    return false;
                }

                if (ShouldCreateUiPrefab(request))
                {
                    EnsureUiPrefabContents(root, request);
                }
                else
                {
                    var recipe = GetPrefabRecipe(prefabName, request?.notes);
                    EnsurePrefabRecipe(root, recipe);
                }

                PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                return true;
            }
            catch (Exception ex)
            {
                error = $"Failed to update prefab: {ex.Message}";
                return false;
            }
            finally
            {
                if (root != null)
                {
                    PrefabUtility.UnloadPrefabContents(root);
                }
            }
        }

        private static void EnsurePrefabRecipe(GameObject root, PrefabRecipe recipe)
        {
            if (root == null)
            {
                return;
            }

            if (recipe == PrefabRecipe.Empty)
            {
                return;
            }

            if (recipe == PrefabRecipe.CharacterController)
            {
                if (root.GetComponent<CharacterController>() == null)
                {
                    root.AddComponent<CharacterController>();
                }
                return;
            }

            if (PrefabHasVisualContent(root))
            {
                return;
            }

            var visual = CreatePrefabRoot(recipe, "Visual");
            if (visual == null)
            {
                return;
            }

            visual.transform.SetParent(root.transform, false);
        }

        private static bool PrefabHasVisualContent(GameObject root)
        {
            if (root == null)
            {
                return false;
            }

            return root.GetComponentInChildren<Renderer>(true) != null
                || root.GetComponentInChildren<Collider>(true) != null
                || root.GetComponentInChildren<CharacterController>(true) != null
                || root.GetComponentInChildren<Canvas>(true) != null;
        }

        private static void EnsureUiPrefabContents(GameObject root, ProtoFeatureRequest request)
        {
            if (root == null)
            {
                return;
            }

            var canvas = root.GetComponent<Canvas>();
            if (canvas == null)
            {
                canvas = root.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            }
            if (root.GetComponent<CanvasScaler>() == null)
            {
                root.AddComponent<CanvasScaler>();
            }
            if (root.GetComponent<GraphicRaycaster>() == null)
            {
                root.AddComponent<GraphicRaycaster>();
            }

            var panel = FindChildByName(root.transform, "Panel")?.gameObject;
            if (panel == null)
            {
                panel = new GameObject("Panel");
                panel.transform.SetParent(root.transform, false);
            }

            var panelRect = panel.GetComponent<RectTransform>() ?? panel.AddComponent<RectTransform>();
            panelRect.anchorMin = Vector2.zero;
            panelRect.anchorMax = Vector2.one;
            panelRect.offsetMin = Vector2.zero;
            panelRect.offsetMax = Vector2.zero;

            if (panel.GetComponent<Image>() == null)
            {
                var panelImage = panel.AddComponent<Image>();
                panelImage.color = new Color(0f, 0f, 0f, 0.35f);
            }

            var title = FindChildByName(panel.transform, "Title")?.gameObject;
            if (title == null)
            {
                title = new GameObject("Title");
                title.transform.SetParent(panel.transform, false);
            }

            var titleRect = title.GetComponent<RectTransform>() ?? title.AddComponent<RectTransform>();
            titleRect.anchorMin = new Vector2(0.5f, 1f);
            titleRect.anchorMax = new Vector2(0.5f, 1f);
            titleRect.anchoredPosition = new Vector2(0f, -40f);
            titleRect.sizeDelta = new Vector2(400f, 60f);

            var titleText = title.GetComponent<Text>() ?? title.AddComponent<Text>();
            if (titleText.font == null)
            {
                var uiFont = GetBuiltinUiFont();
                if (uiFont != null)
                {
                    titleText.font = uiFont;
                }
            }
            titleText.alignment = TextAnchor.MiddleCenter;
            if (string.IsNullOrWhiteSpace(titleText.text))
            {
                titleText.text = BuildUiTitleText(request);
            }
            titleText.color = Color.white;

            if (ShouldCreateUiButton(request))
            {
                EnsureUiButton(panel.transform, BuildUiButtonLabel(request));
            }

            if (root.GetComponentInChildren<EventSystem>(true) == null)
            {
                var eventSystem = new GameObject("EventSystem");
                eventSystem.transform.SetParent(root.transform, false);
                eventSystem.AddComponent<EventSystem>();
                eventSystem.AddComponent<StandaloneInputModule>();
            }
        }

        private static async Task<string> RequestSceneNotesAsync(ProtoFeatureRequest request, Dictionary<string, ProtoFeatureRequest> requestLookup, string token, string model, CancellationToken cancellationToken)
        {
            if (request == null || string.IsNullOrWhiteSpace(token))
            {
                return string.Empty;
            }

            var prefabNames = new List<string>();
            var managerNames = new List<string>();
            if (requestLookup != null)
            {
                foreach (var pair in requestLookup)
                {
                    var dep = pair.Value;
                    if (dep == null)
                    {
                        continue;
                    }

                    if (string.Equals(dep.type, "prefab", StringComparison.OrdinalIgnoreCase) ||
                        (string.Equals(dep.type, "asset", StringComparison.OrdinalIgnoreCase) && LooksLikePrefabRequest(dep)))
                    {
                        if (!string.IsNullOrWhiteSpace(dep.name))
                        {
                            prefabNames.Add(dep.name.Trim());
                        }
                    }
                    else if (string.Equals(dep.type, "script", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!string.IsNullOrWhiteSpace(dep.name) &&
                            dep.name.IndexOf("manager", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            managerNames.Add(dep.name.Trim());
                        }
                    }
                }
            }

            var prefabsList = prefabNames.Count > 0 ? string.Join(", ", prefabNames) : "(none)";
            var managersList = managerNames.Count > 0 ? string.Join(", ", managerNames) : "(none)";

            var messages = new List<ProtoChatMessage>
            {
                new ProtoChatMessage
                {
                    role = "system",
                    content = "You create scene notes for Unity. Return ONLY a short text block with lines like: \"prefabs: A, B\" and \"managers: X, Y\" and optionally include \"ui\" and/or \"spawn\"."
                }
            };

            AppendPrefabIndexMessage(messages, ReadPrefabIndexMarkdown());

            messages.Add(new ProtoChatMessage
            {
                role = "user",
                content = $"Scene request: {request.name}\nAvailable prefabs: {prefabsList}\nAvailable manager-like scripts: {managersList}\nChoose the minimal set needed for a functional scene."
            });

            return await ProtoOpenAIClient.SendChatAsync(token, model, messages.ToArray(), 320, cancellationToken).ConfigureAwait(false);
        }

        private static bool TryCreateSceneForRequest(ProtoFeatureRequest request, Dictionary<string, ProtoFeatureRequest> requestLookup, out string error)
        {
            error = string.Empty;
            if (request == null)
            {
                error = "Scene request is missing.";
                return false;
            }

            var sceneName = GetSceneName(request);
            var scenePath = BuildScenePath(request.path, sceneName, out var folderPath);
            if (!ProtoAssetUtility.TryEnsureFolder(folderPath, out _, out var folderError))
            {
                error = folderError;
                return false;
            }

            var existingScene = AssetDatabase.LoadAssetAtPath<SceneAsset>(scenePath);
            if (existingScene != null)
            {
                if (!TryUpdateExistingSceneForRequest(request, requestLookup, scenePath, out error))
                {
                    return false;
                }

                request.notes = AppendNote(request.notes, "Scene exists; updated in place.");
                return true;
            }

            if (!EnsureAdditiveSceneCreationPossible(out var setupError))
            {
                error = setupError;
                return false;
            }

            var newScene = UnityEditor.SceneManagement.EditorSceneManager.NewScene(
                UnityEditor.SceneManagement.NewSceneSetup.EmptyScene,
                UnityEditor.SceneManagement.NewSceneMode.Additive);

            AddDefaultSceneContent(request, newScene);
            AddScenePrefabs(request, requestLookup, newScene);
            AddSceneManagers(request, requestLookup, newScene);
            AddSceneUi(request, requestLookup, newScene);
            AddSceneSpawnPoints(request, newScene);
            HydrateSceneReferences(request, requestLookup, newScene, scenePath);

            var saved = UnityEditor.SceneManagement.EditorSceneManager.SaveScene(newScene, scenePath);
            UnityEditor.SceneManagement.EditorSceneManager.CloseScene(newScene, true);

            if (!saved)
            {
                error = "Failed to save scene asset.";
                return false;
            }

            return true;
        }

        private static bool TryUpdateExistingSceneForRequest(ProtoFeatureRequest request, Dictionary<string, ProtoFeatureRequest> requestLookup, string scenePath, out string error)
        {
            error = string.Empty;

            if (!EnsureAdditiveSceneCreationPossible(out var setupError))
            {
                error = setupError;
                return false;
            }

            var scene = UnityEditor.SceneManagement.EditorSceneManager.OpenScene(scenePath, UnityEditor.SceneManagement.OpenSceneMode.Additive);
            if (!scene.IsValid())
            {
                error = $"Failed to open scene at {scenePath}.";
                return false;
            }

            AddDefaultSceneContent(request, scene);
            AddScenePrefabs(request, requestLookup, scene);
            AddSceneManagers(request, requestLookup, scene);
            AddSceneUi(request, requestLookup, scene);
            AddSceneSpawnPoints(request, scene);
            HydrateSceneReferences(request, requestLookup, scene, scenePath);

            var saved = UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene);
            UnityEditor.SceneManagement.EditorSceneManager.CloseScene(scene, true);

            if (!saved)
            {
                error = "Failed to save scene asset.";
                return false;
            }

            return true;
        }

        private static void EnqueueSceneLayout(ProtoFeatureRequest request, Dictionary<string, ProtoFeatureRequest> requestLookup, string token, string model, string scenePath)
        {
            if (request == null || string.IsNullOrWhiteSpace(scenePath))
            {
                return;
            }

            var targets = BuildSceneLayoutTargets(request, requestLookup);
            if (targets.Count == 0)
            {
                return;
            }

            SceneLayoutQueue.Enqueue(new SceneLayoutJob
            {
                scenePath = scenePath,
                token = token,
                model = model,
                targets = targets.ToArray(),
                notes = request.notes ?? string.Empty
            });

            if (!_isSceneLayoutRunning)
            {
                _isSceneLayoutRunning = true;
                EditorApplication.delayCall += () => _ = ProcessSceneLayoutQueueAsync();
            }
        }

        private static async Task ProcessSceneLayoutQueueAsync()
        {
            while (SceneLayoutQueue.Count > 0)
            {
                var job = SceneLayoutQueue.Dequeue();
                if (job == null || string.IsNullOrWhiteSpace(job.scenePath))
                {
                    continue;
                }

                try
                {
                    var response = await RequestSceneLayoutAsync(job).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(response))
                    {
                        continue;
                    }

                    var json = ExtractJson(response);
                    if (string.IsNullOrWhiteSpace(json))
                    {
                        continue;
                    }

                    var parsed = JsonUtility.FromJson<SceneLayoutResponse>(json);
                    if (parsed?.items == null || parsed.items.Length == 0)
                    {
                        continue;
                    }

                    await ApplySceneLayoutAsync(job.scenePath, parsed).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"Scene layout failed: {ex.Message}");
                }
            }

            _isSceneLayoutRunning = false;
        }

        private static async Task<string> RequestSceneLayoutAsync(SceneLayoutJob job)
        {
            var targets = job.targets ?? Array.Empty<string>();
            var list = string.Join(", ", targets);
            var notes = string.IsNullOrWhiteSpace(job.notes) ? string.Empty : job.notes.Trim();

            var messages = new List<ProtoChatMessage>
            {
                new ProtoChatMessage
                {
                    role = "system",
                    content = "You are arranging a Unity scene layout. Return ONLY JSON: {\"items\":[{\"name\":\"GameObjectName\",\"position\":[x,y,z],\"rotation\":[x,y,z],\"scale\":[x,y,z]}]}."
                },
                new ProtoChatMessage
                {
                    role = "user",
                    content = $"Scene targets: {list}.\nNotes: {notes}\nUse world positions. Keep UI and EventSystem at (0,0,0)."
                }
            };

            return await ProtoOpenAIClient.SendChatAsync(job.token, job.model, messages.ToArray(), 320).ConfigureAwait(false);
        }

        private static Task ApplySceneLayoutAsync(string scenePath, SceneLayoutResponse layout)
        {
            var tcs = new TaskCompletionSource<bool>();
            EditorApplication.delayCall += () =>
            {
                try
                {
                    if (layout == null || layout.items == null)
                    {
                        tcs.SetResult(false);
                        return;
                    }

                    if (!EnsureAdditiveSceneCreationPossible(out var setupError))
                    {
                        Debug.LogWarning($"Scene layout skipped: {setupError}");
                        tcs.SetResult(false);
                        return;
                    }

                    var scene = UnityEditor.SceneManagement.EditorSceneManager.OpenScene(scenePath, UnityEditor.SceneManagement.OpenSceneMode.Additive);
                    foreach (var item in layout.items)
                    {
                        if (item == null || string.IsNullOrWhiteSpace(item.name))
                        {
                            continue;
                        }

                        var target = FindInSceneByName(scene, item.name.Trim());
                        if (target == null)
                        {
                            continue;
                        }

                        if (item.position != null && item.position.Length >= 3)
                        {
                            target.transform.position = new Vector3(item.position[0], item.position[1], item.position[2]);
                        }

                        if (item.rotation != null && item.rotation.Length >= 3)
                        {
                            target.transform.rotation = Quaternion.Euler(item.rotation[0], item.rotation[1], item.rotation[2]);
                        }

                        if (item.scale != null && item.scale.Length >= 3)
                        {
                            target.transform.localScale = new Vector3(item.scale[0], item.scale[1], item.scale[2]);
                        }
                    }

                    UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene);
                    UnityEditor.SceneManagement.EditorSceneManager.CloseScene(scene, true);
                    tcs.SetResult(true);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            };
            return tcs.Task;
        }

        private static bool EnsureAdditiveSceneCreationPossible(out string error)
        {
            error = string.Empty;
            var untitledScenes = new List<UnityEngine.SceneManagement.Scene>();
            var sceneCount = UnityEngine.SceneManagement.SceneManager.sceneCount;
            for (var i = 0; i < sceneCount; i++)
            {
                var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                if (!scene.IsValid() || !scene.isLoaded)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(scene.path))
                {
                    untitledScenes.Add(scene);
                }
            }

            if (untitledScenes.Count == 0)
            {
                return true;
            }

            const string autosaveFolder = "Assets/Project/Scenes/Autosave";
            if (!ProtoAssetUtility.TryEnsureFolder(autosaveFolder, out var normalized, out var folderError))
            {
                error = folderError;
                return false;
            }

            for (var i = 0; i < untitledScenes.Count; i++)
            {
                var scene = untitledScenes[i];
                var fileName = $"AutoScene_{DateTime.UtcNow:yyyyMMdd_HHmmssfff}_{i + 1}.unity";
                var scenePath = $"{normalized}/{fileName}";
                var saved = UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene, scenePath);
                if (!saved)
                {
                    error = $"Failed to auto-save untitled scene to {scenePath}.";
                    return false;
                }
            }

            return true;
        }

        private static GameObject FindInSceneByName(UnityEngine.SceneManagement.Scene scene, string name)
        {
            if (!scene.IsValid() || string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            var roots = scene.GetRootGameObjects();
            foreach (var root in roots)
            {
                if (root == null)
                {
                    continue;
                }

                if (string.Equals(root.name, name, StringComparison.Ordinal))
                {
                    return root;
                }

                var found = FindChildByName(root.transform, name);
                if (found != null)
                {
                    return found.gameObject;
                }
            }

            return null;
        }

        private static Transform FindChildByName(Transform root, string name)
        {
            if (root == null)
            {
                return null;
            }

            for (var i = 0; i < root.childCount; i++)
            {
                var child = root.GetChild(i);
                if (string.Equals(child.name, name, StringComparison.Ordinal))
                {
                    return child;
                }

                var nested = FindChildByName(child, name);
                if (nested != null)
                {
                    return nested;
                }
            }

            return null;
        }

        private static List<string> BuildSceneLayoutTargets(ProtoFeatureRequest request, Dictionary<string, ProtoFeatureRequest> requestLookup)
        {
            var targets = new List<string>
            {
                "Main Camera",
                "Directional Light"
            };

            var prefabPaths = CollectScenePrefabPaths(request, requestLookup);
            foreach (var path in prefabPaths)
            {
                var name = Path.GetFileNameWithoutExtension(path);
                if (!string.IsNullOrWhiteSpace(name) && !targets.Contains(name))
                {
                    targets.Add(name);
                }
            }

            if (ShouldCreateSceneUi(request, requestLookup))
            {
                targets.Add("UI");
                targets.Add("EventSystem");
            }

            if (NotesContainAny(request.notes, new[] { "spawn", "spawnpoint", "spawn point", "respawn" }))
            {
                targets.Add("SpawnPoints");
                targets.Add("PlayerSpawn");
            }

            if (CollectSceneManagerComponents(request, requestLookup).Count > 0)
            {
                targets.Add("Managers");
            }

            return targets;
        }

        [Serializable]
        private sealed class SceneLayoutResponse
        {
            public SceneLayoutItem[] items;
        }

        [Serializable]
        private sealed class SceneLayoutItem
        {
            public string name;
            public float[] position;
            public float[] rotation;
            public float[] scale;
        }

        private sealed class SceneLayoutJob
        {
            public string scenePath;
            public string token;
            public string model;
            public string[] targets;
            public string notes;
        }

        private static bool TryCreateMaterialForRequest(ProtoFeatureRequest request, out string error)
        {
            error = string.Empty;
            if (request == null)
            {
                error = "Material request is missing.";
                return false;
            }

            var materialName = GetMaterialName(request);
            var materialPath = BuildMaterialPath(request.path, materialName, out var folderPath);
            if (!ProtoAssetUtility.TryEnsureFolder(folderPath, out _, out var folderError))
            {
                error = folderError;
                return false;
            }

            if (AssetDatabase.LoadAssetAtPath<Material>(materialPath) != null)
            {
                error = "Material already exists. Delete it or choose a new name.";
                return false;
            }

            var shader = GetMaterialShader(request.notes);
            if (shader == null)
            {
                error = "Material shader not found.";
                return false;
            }

            var material = new Material(shader);
            AssetDatabase.CreateAsset(material, materialPath);
            AssetDatabase.SaveAssets();
            return true;
        }

        private static string GetPrefabName(ProtoFeatureRequest request)
        {
            if (request == null)
            {
                return "Prefab";
            }

            if (!string.IsNullOrWhiteSpace(request.name))
            {
                return request.name.Trim();
            }

            if (!string.IsNullOrWhiteSpace(request.path))
            {
                var normalized = request.path.Trim().Replace('\\', '/');
                if (normalized.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                {
                    var fileName = Path.GetFileNameWithoutExtension(normalized);
                    if (!string.IsNullOrWhiteSpace(fileName))
                    {
                        return fileName;
                    }
                }
            }

            return "Prefab";
        }

        private static string GetSceneName(ProtoFeatureRequest request)
        {
            if (request == null)
            {
                return "NewScene";
            }

            if (!string.IsNullOrWhiteSpace(request.name))
            {
                return request.name.Trim();
            }

            if (!string.IsNullOrWhiteSpace(request.path))
            {
                var normalized = request.path.Trim().Replace('\\', '/');
                if (normalized.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                {
                    var fileName = Path.GetFileNameWithoutExtension(normalized);
                    if (!string.IsNullOrWhiteSpace(fileName))
                    {
                        return fileName;
                    }
                }
            }

            return "NewScene";
        }

        private static string GetMaterialName(ProtoFeatureRequest request)
        {
            if (request == null)
            {
                return "NewMaterial";
            }

            if (!string.IsNullOrWhiteSpace(request.name))
            {
                return request.name.Trim();
            }

            if (!string.IsNullOrWhiteSpace(request.path))
            {
                var normalized = request.path.Trim().Replace('\\', '/');
                if (normalized.EndsWith(".mat", StringComparison.OrdinalIgnoreCase))
                {
                    var fileName = Path.GetFileNameWithoutExtension(normalized);
                    if (!string.IsNullOrWhiteSpace(fileName))
                    {
                        return fileName;
                    }
                }
            }

            return "NewMaterial";
        }

        private static string BuildPrefabPath(string path, string name, out string folderPath)
        {
            var prefabName = string.IsNullOrWhiteSpace(name) ? "Prefab" : name.Trim();
            var normalized = EnsureProjectPath(string.IsNullOrWhiteSpace(path) ? $"{ProjectRoot}/Prefabs" : path);

            if (normalized.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                folderPath = Path.GetDirectoryName(normalized)?.Replace('\\', '/') ?? ProjectRoot;
                return normalized;
            }

            if (normalized.EndsWith("/", StringComparison.Ordinal))
            {
                normalized = normalized.TrimEnd('/');
            }

            if (AssetDatabase.IsValidFolder(normalized))
            {
                folderPath = normalized;
                return $"{normalized}/{prefabName}.prefab";
            }

            if (Path.HasExtension(normalized))
            {
                folderPath = Path.GetDirectoryName(normalized)?.Replace('\\', '/') ?? ProjectRoot;
                return $"{folderPath}/{prefabName}.prefab";
            }

            folderPath = normalized;
            return $"{normalized}/{prefabName}.prefab";
        }

        private static string BuildScenePath(string path, string name, out string folderPath)
        {
            var sceneName = string.IsNullOrWhiteSpace(name) ? "NewScene" : name.Trim();
            var normalized = EnsureProjectPath(string.IsNullOrWhiteSpace(path) ? $"{ProjectRoot}/Scenes" : path);

            if (normalized.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
            {
                folderPath = Path.GetDirectoryName(normalized)?.Replace('\\', '/') ?? ProjectRoot;
                return normalized;
            }

            if (normalized.EndsWith("/", StringComparison.Ordinal))
            {
                normalized = normalized.TrimEnd('/');
            }

            if (AssetDatabase.IsValidFolder(normalized))
            {
                folderPath = normalized;
                return $"{normalized}/{sceneName}.unity";
            }

            if (Path.HasExtension(normalized))
            {
                folderPath = Path.GetDirectoryName(normalized)?.Replace('\\', '/') ?? ProjectRoot;
                return $"{folderPath}/{sceneName}.unity";
            }

            folderPath = normalized;
            return $"{normalized}/{sceneName}.unity";
        }

        private static string BuildMaterialPath(string path, string name, out string folderPath)
        {
            var materialName = string.IsNullOrWhiteSpace(name) ? "NewMaterial" : name.Trim();
            var normalized = EnsureProjectPath(string.IsNullOrWhiteSpace(path) ? $"{ProjectRoot}/Materials" : path);

            if (normalized.EndsWith(".mat", StringComparison.OrdinalIgnoreCase))
            {
                folderPath = Path.GetDirectoryName(normalized)?.Replace('\\', '/') ?? ProjectRoot;
                return normalized;
            }

            if (normalized.EndsWith("/", StringComparison.Ordinal))
            {
                normalized = normalized.TrimEnd('/');
            }

            if (AssetDatabase.IsValidFolder(normalized))
            {
                folderPath = normalized;
                return $"{normalized}/{materialName}.mat";
            }

            if (Path.HasExtension(normalized))
            {
                folderPath = Path.GetDirectoryName(normalized)?.Replace('\\', '/') ?? ProjectRoot;
                return $"{folderPath}/{materialName}.mat";
            }

            folderPath = normalized;
            return $"{normalized}/{materialName}.mat";
        }

        private static Shader GetMaterialShader(string notes)
        {
            var hint = (notes ?? string.Empty).ToLowerInvariant();
            if (hint.Contains("hdrp"))
            {
                var hdrp = Shader.Find("HDRP/Lit");
                if (hdrp != null)
                {
                    return hdrp;
                }
            }

            if (hint.Contains("urp") || hint.Contains("universal"))
            {
                var urp = Shader.Find("Universal Render Pipeline/Lit");
                if (urp != null)
                {
                    return urp;
                }
            }

            var standard = Shader.Find("Standard");
            if (standard != null)
            {
                return standard;
            }

            return Shader.Find("Universal Render Pipeline/Lit");
        }

        private static GameObject CreatePrefabRoot(PrefabRecipe recipe, string name)
        {
            var safeName = string.IsNullOrWhiteSpace(name) ? "Prefab" : name.Trim();
            switch (recipe)
            {
                case PrefabRecipe.CharacterController:
                    var characterRoot = new GameObject(safeName);
                    characterRoot.AddComponent<CharacterController>();
                    return characterRoot;
                case PrefabRecipe.Empty:
                    return new GameObject(safeName);
                case PrefabRecipe.Sphere:
                    var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    sphere.name = safeName;
                    return sphere;
                case PrefabRecipe.Capsule:
                    var capsule = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                    capsule.name = safeName;
                    return capsule;
                case PrefabRecipe.Cylinder:
                    var cylinder = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                    cylinder.name = safeName;
                    return cylinder;
                case PrefabRecipe.Plane:
                    var plane = GameObject.CreatePrimitive(PrimitiveType.Plane);
                    plane.name = safeName;
                    return plane;
                case PrefabRecipe.Quad:
                    var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
                    quad.name = safeName;
                    return quad;
                case PrefabRecipe.Cube:
                default:
                    var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    cube.name = safeName;
                    return cube;
            }
        }

        private static bool ShouldCreateUiPrefab(ProtoFeatureRequest request)
        {
            if (request == null)
            {
                return false;
            }

            var name = request.name ?? string.Empty;
            var notes = request.notes ?? string.Empty;
            var path = request.path ?? string.Empty;
            var lower = $"{name} {notes} {path}".ToLowerInvariant();

            return lower.Contains("ui")
                || lower.Contains("canvas")
                || lower.Contains("hud")
                || lower.Contains("screen")
                || lower.Contains("/ui/");
        }

        private static GameObject CreateUiPrefabRoot(ProtoFeatureRequest request, string name)
        {
            var rootName = string.IsNullOrWhiteSpace(name) ? "UIPrefab" : name.Trim();
            var root = new GameObject(rootName);

            var canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            root.AddComponent<CanvasScaler>();
            root.AddComponent<GraphicRaycaster>();

            var panel = new GameObject("Panel");
            panel.transform.SetParent(root.transform, false);
            var panelRect = panel.AddComponent<RectTransform>();
            panelRect.anchorMin = Vector2.zero;
            panelRect.anchorMax = Vector2.one;
            panelRect.offsetMin = Vector2.zero;
            panelRect.offsetMax = Vector2.zero;
            var panelImage = panel.AddComponent<Image>();
            panelImage.color = new Color(0f, 0f, 0f, 0.35f);

            var title = new GameObject("Title");
            title.transform.SetParent(panel.transform, false);
            var titleRect = title.AddComponent<RectTransform>();
            titleRect.anchorMin = new Vector2(0.5f, 1f);
            titleRect.anchorMax = new Vector2(0.5f, 1f);
            titleRect.anchoredPosition = new Vector2(0f, -40f);
            titleRect.sizeDelta = new Vector2(400f, 60f);

            var titleText = title.AddComponent<Text>();
            var uiFont = GetBuiltinUiFont();
            if (uiFont != null)
            {
                titleText.font = uiFont;
            }
            titleText.alignment = TextAnchor.MiddleCenter;
            titleText.text = BuildUiTitleText(request);
            titleText.color = Color.white;

            if (ShouldCreateUiButton(request))
            {
                CreateUiButton(panel.transform, BuildUiButtonLabel(request));
            }

            var eventSystem = new GameObject("EventSystem");
            eventSystem.transform.SetParent(root.transform, false);
            eventSystem.AddComponent<EventSystem>();
            eventSystem.AddComponent<StandaloneInputModule>();

            return root;
        }

        private static string BuildUiTitleText(ProtoFeatureRequest request)
        {
            if (request == null)
            {
                return "UI";
            }

            var name = request.name ?? string.Empty;
            var notes = request.notes ?? string.Empty;
            var lower = notes.ToLowerInvariant();
            if (lower.Contains("win"))
            {
                return "You Win";
            }
            if (lower.Contains("lose") || lower.Contains("defeat"))
            {
                return "Defeat";
            }
            if (lower.Contains("level"))
            {
                return "Level Up";
            }
            if (lower.Contains("hud"))
            {
                return "HUD";
            }

            return string.IsNullOrWhiteSpace(name) ? "UI" : name.Trim();
        }

        private static bool ShouldCreateUiButton(ProtoFeatureRequest request)
        {
            if (request == null)
            {
                return false;
            }

            var notes = request.notes ?? string.Empty;
            var lower = notes.ToLowerInvariant();
            return lower.Contains("button")
                || lower.Contains("restart")
                || lower.Contains("next")
                || lower.Contains("play");
        }

        private static string BuildUiButtonLabel(ProtoFeatureRequest request)
        {
            if (request == null)
            {
                return "OK";
            }

            var notes = request.notes ?? string.Empty;
            var lower = notes.ToLowerInvariant();
            if (lower.Contains("restart"))
            {
                return "Restart";
            }
            if (lower.Contains("next"))
            {
                return "Next";
            }
            if (lower.Contains("play"))
            {
                return "Play";
            }

            return "OK";
        }

        private static void CreateUiButton(Transform parent, string label)
        {
            var buttonObject = new GameObject("Button");
            buttonObject.transform.SetParent(parent, false);
            var buttonRect = buttonObject.AddComponent<RectTransform>();
            buttonRect.anchorMin = new Vector2(0.5f, 0f);
            buttonRect.anchorMax = new Vector2(0.5f, 0f);
            buttonRect.anchoredPosition = new Vector2(0f, 60f);
            buttonRect.sizeDelta = new Vector2(200f, 50f);

            var image = buttonObject.AddComponent<Image>();
            image.color = new Color(1f, 1f, 1f, 0.9f);
            buttonObject.AddComponent<Button>();

            var labelObject = new GameObject("Label");
            labelObject.transform.SetParent(buttonObject.transform, false);
            var labelRect = labelObject.AddComponent<RectTransform>();
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = Vector2.zero;
            labelRect.offsetMax = Vector2.zero;

            var text = labelObject.AddComponent<Text>();
            var uiFont = GetBuiltinUiFont();
            if (uiFont != null)
            {
                text.font = uiFont;
            }
            text.alignment = TextAnchor.MiddleCenter;
            text.text = string.IsNullOrWhiteSpace(label) ? "OK" : label.Trim();
            text.color = Color.black;
        }

        private static void EnsureUiButton(Transform parent, string label)
        {
            if (parent == null)
            {
                return;
            }

            var existing = FindChildByName(parent, "Button");
            if (existing == null)
            {
                CreateUiButton(parent, label);
                return;
            }

            var buttonObject = existing.gameObject;
            var buttonRect = buttonObject.GetComponent<RectTransform>() ?? buttonObject.AddComponent<RectTransform>();
            buttonRect.anchorMin = new Vector2(0.5f, 0f);
            buttonRect.anchorMax = new Vector2(0.5f, 0f);
            buttonRect.anchoredPosition = new Vector2(0f, 60f);
            buttonRect.sizeDelta = new Vector2(200f, 50f);

            var image = buttonObject.GetComponent<Image>() ?? buttonObject.AddComponent<Image>();
            image.color = new Color(1f, 1f, 1f, 0.9f);
            if (buttonObject.GetComponent<Button>() == null)
            {
                buttonObject.AddComponent<Button>();
            }

            var labelTransform = FindChildByName(buttonObject.transform, "Label");
            GameObject labelObject;
            if (labelTransform == null)
            {
                labelObject = new GameObject("Label");
                labelObject.transform.SetParent(buttonObject.transform, false);
            }
            else
            {
                labelObject = labelTransform.gameObject;
            }

            var labelRect = labelObject.GetComponent<RectTransform>() ?? labelObject.AddComponent<RectTransform>();
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = Vector2.zero;
            labelRect.offsetMax = Vector2.zero;

            var text = labelObject.GetComponent<Text>() ?? labelObject.AddComponent<Text>();
            if (text.font == null)
            {
                var uiFont = GetBuiltinUiFont();
                if (uiFont != null)
                {
                    text.font = uiFont;
                }
            }
            text.alignment = TextAnchor.MiddleCenter;
            if (string.IsNullOrWhiteSpace(text.text))
            {
                text.text = string.IsNullOrWhiteSpace(label) ? "OK" : label.Trim();
            }
            text.color = Color.black;
        }

        private static Font GetBuiltinUiFont()
        {
            var major = ParseUnityMajorVersion(Application.unityVersion);
            if (major >= 2022)
            {
                return Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            }

            return Resources.GetBuiltinResource<Font>("Arial.ttf");
        }

        private static int ParseUnityMajorVersion(string version)
        {
            if (string.IsNullOrWhiteSpace(version))
            {
                return 0;
            }

            var dot = version.IndexOf('.');
            var majorText = dot > 0 ? version.Substring(0, dot) : version;
            if (int.TryParse(majorText, out var major))
            {
                return major;
            }

            return 0;
        }

        private static PrefabRecipe GetPrefabRecipe(string name, string notes)
        {
            var combined = $"{name} {notes}".ToLowerInvariant();
            if (combined.Contains("character controller") || combined.Contains("charactercontroller") || combined.Contains("character-controller"))
            {
                return PrefabRecipe.CharacterController;
            }

            if (combined.Contains("empty"))
            {
                return PrefabRecipe.Empty;
            }

            if (combined.Contains("sphere"))
            {
                return PrefabRecipe.Sphere;
            }

            if (combined.Contains("capsule"))
            {
                return PrefabRecipe.Capsule;
            }

            if (combined.Contains("cylinder"))
            {
                return PrefabRecipe.Cylinder;
            }

            if (combined.Contains("plane"))
            {
                return PrefabRecipe.Plane;
            }

            if (combined.Contains("quad"))
            {
                return PrefabRecipe.Quad;
            }

            if (combined.Contains("cube") || combined.Contains("box"))
            {
                return PrefabRecipe.Cube;
            }

            return PrefabRecipe.Cube;
        }

        private static bool LooksLikePrefabRequest(ProtoFeatureRequest request)
        {
            if (request == null)
            {
                return false;
            }

            var combined = $"{request.name} {request.notes}".ToLowerInvariant();
            if (combined.Contains("prefab"))
            {
                return true;
            }

            return combined.Contains("cube") || combined.Contains("box") ||
                   combined.Contains("sphere") || combined.Contains("capsule") ||
                   combined.Contains("cylinder") || combined.Contains("plane") ||
                   combined.Contains("quad") || combined.Contains("character controller") ||
                   combined.Contains("charactercontroller") || combined.Contains("character-controller") ||
                   combined.Contains("empty");
        }

        private static bool LooksLikeMaterialRequest(ProtoFeatureRequest request)
        {
            if (request == null)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(request.path) &&
                request.path.Trim().EndsWith(".mat", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var combined = $"{request.name} {request.notes}".ToLowerInvariant();
            return combined.Contains("material") || combined.Contains("mat ");
        }

        private static bool LooksLikeSceneRequest(ProtoFeatureRequest request)
        {
            if (request == null)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(request.path) &&
                request.path.Trim().EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var combined = $"{request.name} {request.notes}".ToLowerInvariant();
            return combined.Contains("scene") || combined.Contains("level");
        }

        private static AssetFallbackKind DetectAssetKind(ProtoFeatureRequest request)
        {
            if (request == null)
            {
                return AssetFallbackKind.Unknown;
            }

            if (LooksLikeSceneRequest(request))
            {
                return AssetFallbackKind.Scene;
            }

            if (LooksLikeMaterialRequest(request))
            {
                return AssetFallbackKind.Material;
            }

            if (LooksLikePrefabRequest(request))
            {
                return AssetFallbackKind.Prefab;
            }

            return AssetFallbackKind.Unknown;
        }

        private async Task<bool> GenerateScriptForRequestAsync(ProtoFeatureRequest request, string token, string model, ProtoChatMessage[] baseMessages, Dictionary<string, ProtoFeatureRequest> requestLookup, CancellationToken cancellationToken)
        {
            NormalizeScriptRequestName(request, true);
            if (string.IsNullOrWhiteSpace(request.name))
            {
                return false;
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var targetFolder = NormalizeFolderPath(request.path);
                var scriptPrompt = string.IsNullOrWhiteSpace(request.notes)
                    ? $"Create a MonoBehaviour named {request.name}."
                    : request.notes.Trim();

                var messages = new List<ProtoChatMessage>(baseMessages ?? Array.Empty<ProtoChatMessage>());
                var contractContext = BuildScriptContractContext(request, requestLookup);
                if (!string.IsNullOrWhiteSpace(contractContext))
                {
                    messages.Add(new ProtoChatMessage
                    {
                        role = "system",
                        content = contractContext
                    });
                }
                messages.Add(new ProtoChatMessage
                {
                    role = "user",
                    content = $"Generate a Unity C# MonoBehaviour. Output only code. Class name: {request.name}."
                });
                messages.Add(new ProtoChatMessage
                {
                    role = "user",
                    content = scriptPrompt
                });

                _apiWaitTimeoutSeconds = 320;
                await RunOnMainThread(() => BeginApiWait($"script {request.name}")).ConfigureAwait(false);
                var response = await ProtoOpenAIClient.SendChatAsync(token, model, messages.ToArray(), _apiWaitTimeoutSeconds, cancellationToken).ConfigureAwait(false);
                var code = ExtractCode(response);

                var writeError = string.Empty;
                await RunAssetEditingAsync(() =>
                {
                    if (!ProtoAssetUtility.TryWriteScript(targetFolder, request.name, code, _overwriteScript, false, out _, out writeError))
                    {
                        request.notes = AppendNote(request.notes, writeError);
                    }
                }, false).ConfigureAwait(false);
                _scriptIndexDirty = true;

                if (string.IsNullOrWhiteSpace(code))
                {
                    request.notes = AppendNote(request.notes, "Model returned empty code.");
                    return false;
                }

                return string.IsNullOrWhiteSpace(writeError);
        }
        finally
        {
            await RunOnMainThread(EndApiWait).ConfigureAwait(false);
        }
        }

        private async Task ExecuteFeatureRequestsAsync(List<ProtoFeatureRequest> featureRequests, Dictionary<string, ProtoFeatureRequest> requestLookup, CancellationToken cancellationToken)
        {
            var ordered = OrderRequests(featureRequests);
            var settings = await RunOnMainThread(GetSettingsSnapshot).ConfigureAwait(false);
            var baseMessages = await RunOnMainThread(() => BuildBaseContextMessages(true).ToArray()).ConfigureAwait(false);

            for (var i = 0; i < ordered.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var request = ordered[i];
                if (request == null)
                {
                    continue;
                }

                if (string.Equals(request.status, "done", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                await RunOnMainThread(() =>
                {
                    request.status = "in_progress";
                    request.updatedAt = DateTime.UtcNow.ToString("o");
                    ProtoFeatureRequestStore.SaveRequest(request);
                }).ConfigureAwait(false);

                _toolStatus = $"Applying {request.type} {i + 1}/{ordered.Count}: {request.name}";
                EditorApplication.delayCall += Repaint;

                var succeeded = false;
                try
                {
                    succeeded = await ApplyRequestAsync(request, settings.token, settings.model, baseMessages, requestLookup, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    request.notes = AppendNote(request.notes, ex.Message);
                    succeeded = false;
                }

                await RunOnMainThread(() =>
                {
                    request.status = succeeded ? "done" : "blocked";
                    request.updatedAt = DateTime.UtcNow.ToString("o");
                    ProtoFeatureRequestStore.SaveRequest(request);
                }).ConfigureAwait(false);
            }
        }

        private async Task RetryBlockedRequestsAsync(List<ProtoFeatureRequest> featureRequests, Dictionary<string, ProtoFeatureRequest> requestLookup, CancellationToken cancellationToken)
        {
            var blocked = new List<ProtoFeatureRequest>();
            foreach (var request in featureRequests)
            {
                if (request != null && string.Equals(request.status, "blocked", StringComparison.OrdinalIgnoreCase))
                {
                    blocked.Add(request);
                }
            }

            if (blocked.Count == 0)
            {
                return;
            }

            _toolStatus = $"Retrying blocked requests ({blocked.Count})...";
            EditorApplication.delayCall += Repaint;
            await ExecuteFeatureRequestsAsync(blocked, requestLookup, cancellationToken).ConfigureAwait(false);
        }

        private void SchedulePrefabComponentAttachment(List<ProtoFeatureRequest> featureRequests, Dictionary<string, ProtoFeatureRequest> requestLookup)
        {
            if (featureRequests == null || featureRequests.Count == 0)
            {
                return;
            }

            var requestById = requestLookup ?? new Dictionary<string, ProtoFeatureRequest>(StringComparer.OrdinalIgnoreCase);
            if (requestById.Count == 0)
            {
                foreach (var request in featureRequests)
                {
                    if (request != null && !string.IsNullOrWhiteSpace(request.id))
                    {
                        requestById[request.id] = request;
                    }
                }
            }

            var plans = new List<PrefabAttachmentPlan>();
            foreach (var request in featureRequests)
            {
                if (request == null)
                {
                    continue;
                }

                if (!string.Equals(request.type, "prefab", StringComparison.OrdinalIgnoreCase) &&
                    !(string.Equals(request.type, "asset", StringComparison.OrdinalIgnoreCase) && LooksLikePrefabRequest(request)))
                {
                    continue;
                }

                var componentNames = CollectPrefabComponentNames(request, requestById);
                if (componentNames.Count == 0)
                {
                    continue;
                }

                var prefabName = GetPrefabName(request);
                var prefabPath = BuildPrefabPath(request.path, prefabName, out _);
                plans.Add(new PrefabAttachmentPlan(request, prefabPath, componentNames));
            }

            if (plans.Count == 0)
            {
                return;
            }

            EditorApplication.delayCall += () => TryAttachPrefabComponents(plans, 0);
        }

        private void TryAttachPrefabComponents(List<PrefabAttachmentPlan> plans, int attempt)
        {
            if (plans == null || plans.Count == 0)
            {
                return;
            }

            if (EditorApplication.isCompiling)
            {
                if (attempt < 30)
                {
                    EditorApplication.delayCall += () => TryAttachPrefabComponents(plans, attempt + 1);
                }
                else
                {
                    foreach (var plan in plans)
                    {
                        if (plan.request == null)
                        {
                            continue;
                        }
                        plan.request.notes = AppendNote(plan.request.notes, "Prefab components pending compile.");
                        ProtoFeatureRequestStore.SaveRequest(plan.request);
                    }
                }
                return;
            }

            var retryPlans = new List<PrefabAttachmentPlan>();
            foreach (var plan in plans)
            {
                if (plan.request == null)
                {
                    continue;
                }

                var result = AttachComponentsToPrefab(plan.prefabPath, plan.componentNames);
                if (!string.IsNullOrWhiteSpace(result.error))
                {
                    if (result.shouldRetry && attempt < 30)
                    {
                        retryPlans.Add(plan);
                    }
                    else
                    {
                        plan.request.notes = AppendNote(plan.request.notes, result.error);
                        ProtoFeatureRequestStore.SaveRequest(plan.request);
                    }
                }
                else
                {
                    ProtoFeatureRequestStore.SaveRequest(plan.request);
                }
            }

            if (retryPlans.Count > 0)
            {
                EditorApplication.delayCall += () => TryAttachPrefabComponents(retryPlans, attempt + 1);
            }
        }

        private struct AttachmentResult
        {
            public string error;
            public bool shouldRetry;
        }

        private static AttachmentResult AttachComponentsToPrefab(string prefabPath, List<string> componentNames)
        {
            if (string.IsNullOrWhiteSpace(prefabPath))
            {
                return new AttachmentResult
                {
                    error = "Prefab path is missing."
                };
            }

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null)
            {
                return new AttachmentResult
                {
                    error = $"Prefab not found at {prefabPath}."
                };
            }

            if (componentNames == null || componentNames.Count == 0)
            {
                return new AttachmentResult();
            }

            var root = PrefabUtility.LoadPrefabContents(prefabPath);
            if (root == null)
            {
                return new AttachmentResult
                {
                    error = "Failed to load prefab contents."
                };
            }

            var missing = new List<string>();
            var addedAny = false;
            foreach (var name in componentNames)
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var type = FindTypeByName(name.Trim());
                if (type == null)
                {
                    missing.Add(name.Trim());
                    continue;
                }

                if (root.GetComponent(type) != null)
                {
                    continue;
                }

                root.AddComponent(type);
                addedAny = true;
            }

            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            PrefabUtility.UnloadPrefabContents(root);

            if (missing.Count > 0)
            {
                return new AttachmentResult
                {
                    error = $"Missing script types: {string.Join(", ", missing)}.",
                    shouldRetry = true
                };
            }

            if (!addedAny)
            {
                return new AttachmentResult
                {
                    error = "No prefab components added."
                };
            }

            return new AttachmentResult();
        }

        private static List<string> CollectPrefabComponentNames(ProtoFeatureRequest request, Dictionary<string, ProtoFeatureRequest> requestById)
        {
            var results = new List<string>();
            if (request == null)
            {
                return results;
            }

            var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (request.dependsOn != null && requestById != null)
            {
                foreach (var dep in request.dependsOn)
                {
                    if (string.IsNullOrWhiteSpace(dep))
                    {
                        continue;
                    }

                    if (!requestById.TryGetValue(dep, out var depRequest))
                    {
                        continue;
                    }

                    if (!string.Equals(depRequest.type, "script", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(depRequest.name) && unique.Add(depRequest.name.Trim()))
                    {
                        results.Add(depRequest.name.Trim());
                    }
                }
            }

            foreach (var name in ParseComponentNamesFromNotes(request.notes))
            {
                if (unique.Add(name))
                {
                    results.Add(name);
                }
            }

            return results;
        }

        private static IEnumerable<string> ParseComponentNamesFromNotes(string notes)
        {
            if (string.IsNullOrWhiteSpace(notes))
            {
                yield break;
            }

            var lines = notes.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                var lower = trimmed.ToLowerInvariant();
                var index = lower.IndexOf("scripts:", StringComparison.Ordinal);
                if (index < 0)
                {
                    index = lower.IndexOf("components:", StringComparison.Ordinal);
                }
                if (index < 0)
                {
                    index = lower.IndexOf("add scripts:", StringComparison.Ordinal);
                }
                if (index < 0)
                {
                    index = lower.IndexOf("add components:", StringComparison.Ordinal);
                }
                if (index < 0)
                {
                    index = lower.IndexOf("componentes:", StringComparison.Ordinal);
                }
                if (index < 0)
                {
                    index = lower.IndexOf("agrega scripts:", StringComparison.Ordinal);
                }
                if (index < 0)
                {
                    index = lower.IndexOf("agrega componentes:", StringComparison.Ordinal);
                }
                if (index < 0)
                {
                    continue;
                }

                var list = trimmed.Substring(index);
                var colon = list.IndexOf(':');
                if (colon < 0 || colon >= list.Length - 1)
                {
                    continue;
                }

                var items = list.Substring(colon + 1);
                foreach (var entry in items.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var name = entry.Trim();
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        yield return name;
                    }
                }
            }
        }

        private static Type FindTypeByName(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName))
            {
                return null;
            }

            var scriptType = FindTypeFromMonoScript(typeName);
            if (scriptType != null)
            {
                return scriptType;
            }

            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            foreach (var assembly in assemblies)
            {
                var type = assembly.GetType(typeName, false);
                if (type != null)
                {
                    return type;
                }

                var types = assembly.GetTypes();
                for (var i = 0; i < types.Length; i++)
                {
                    if (string.Equals(types[i].Name, typeName, StringComparison.Ordinal))
                    {
                        return types[i];
                    }
                }
            }

            return null;
        }

        private static Type FindTypeFromMonoScript(string typeName)
        {
            var guids = AssetDatabase.FindAssets($"t:MonoScript {typeName}");
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var script = AssetDatabase.LoadAssetAtPath<MonoScript>(path);
                if (script == null)
                {
                    continue;
                }

                var type = script.GetClass();
                if (type != null && string.Equals(type.Name, typeName, StringComparison.Ordinal))
                {
                    return type;
                }
            }

            return null;
        }

        private static T FindSceneComponent<T>(UnityEngine.SceneManagement.Scene scene) where T : Component
        {
            if (!scene.IsValid())
            {
                return null;
            }

            var roots = scene.GetRootGameObjects();
            foreach (var root in roots)
            {
                if (root == null)
                {
                    continue;
                }

                var component = root.GetComponentInChildren<T>(true);
                if (component != null)
                {
                    return component;
                }
            }

            return null;
        }

        private static bool SceneHasComponent<T>(UnityEngine.SceneManagement.Scene scene) where T : Component
        {
            return FindSceneComponent<T>(scene) != null;
        }

        private static bool SceneHasLight(UnityEngine.SceneManagement.Scene scene, LightType type)
        {
            if (!scene.IsValid())
            {
                return false;
            }

            var roots = scene.GetRootGameObjects();
            foreach (var root in roots)
            {
                if (root == null)
                {
                    continue;
                }

                var lights = root.GetComponentsInChildren<Light>(true);
                for (var i = 0; i < lights.Length; i++)
                {
                    if (lights[i] != null && lights[i].type == type)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static void AddDefaultSceneContent(ProtoFeatureRequest request, UnityEngine.SceneManagement.Scene scene)
        {
            if (!scene.IsValid())
            {
                return;
            }

            UnityEngine.SceneManagement.SceneManager.SetActiveScene(scene);

            var notes = request?.notes ?? string.Empty;
            var lower = notes.ToLowerInvariant();
            var cameraComponent = FindSceneComponent<Camera>(scene);
            var createdCamera = false;
            if (cameraComponent == null)
            {
                var camera = new GameObject("Main Camera");
                cameraComponent = camera.AddComponent<Camera>();
                camera.tag = "MainCamera";
                createdCamera = true;
            }

            if (cameraComponent != null)
            {
                if (!SceneHasComponent<AudioListener>(scene) && cameraComponent.GetComponent<AudioListener>() == null)
                {
                    cameraComponent.gameObject.AddComponent<AudioListener>();
                }

                if (lower.Contains("ortho") || lower.Contains("orthographic") || lower.Contains("ortograf"))
                {
                    cameraComponent.orthographic = true;
                    cameraComponent.orthographicSize = 6f;
                }

                if (createdCamera)
                {
                    cameraComponent.transform.position = new Vector3(0f, 10f, -10f);
                    cameraComponent.transform.rotation = Quaternion.Euler(30f, 0f, 0f);
                }
            }

            if (!SceneHasLight(scene, LightType.Directional))
            {
                var lightObject = new GameObject("Directional Light");
                var light = lightObject.AddComponent<Light>();
                light.type = LightType.Directional;
                lightObject.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            }
        }

        private static void AddSceneUi(ProtoFeatureRequest request, Dictionary<string, ProtoFeatureRequest> requestLookup, UnityEngine.SceneManagement.Scene scene)
        {
            if (request == null || !scene.IsValid())
            {
                return;
            }

            if (!ShouldCreateSceneUi(request, requestLookup))
            {
                return;
            }

            UnityEngine.SceneManagement.SceneManager.SetActiveScene(scene);

            if (FindSceneComponent<EventSystem>(scene) == null)
            {
                var eventSystem = new GameObject("EventSystem");
                eventSystem.AddComponent<EventSystem>();
                eventSystem.AddComponent<StandaloneInputModule>();
            }

            var existingCanvas = FindSceneComponent<Canvas>(scene);
            if (existingCanvas == null)
            {
                var canvasObject = FindInSceneByName(scene, "UI") ?? new GameObject("UI");
                var canvas = canvasObject.GetComponent<Canvas>() ?? canvasObject.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                if (canvasObject.GetComponent<CanvasScaler>() == null)
                {
                    canvasObject.AddComponent<CanvasScaler>();
                }
                if (canvasObject.GetComponent<GraphicRaycaster>() == null)
                {
                    canvasObject.AddComponent<GraphicRaycaster>();
                }
            }
            else
            {
                var canvasObject = existingCanvas.gameObject;
                if (canvasObject.GetComponent<CanvasScaler>() == null)
                {
                    canvasObject.AddComponent<CanvasScaler>();
                }
                if (canvasObject.GetComponent<GraphicRaycaster>() == null)
                {
                    canvasObject.AddComponent<GraphicRaycaster>();
                }
            }
        }

        private static void AddSceneSpawnPoints(ProtoFeatureRequest request, UnityEngine.SceneManagement.Scene scene)
        {
            if (request == null || !scene.IsValid())
            {
                return;
            }

            if (!NotesContainAny(request.notes, new[] { "spawn", "spawnpoint", "spawn point", "respawn" }))
            {
                return;
            }

            UnityEngine.SceneManagement.SceneManager.SetActiveScene(scene);

            var spawnRoot = FindInSceneByName(scene, "SpawnPoints") ?? new GameObject("SpawnPoints");
            if (spawnRoot.transform.Find("PlayerSpawn") == null)
            {
                var playerSpawn = new GameObject("PlayerSpawn");
                playerSpawn.transform.SetParent(spawnRoot.transform, false);
            }
        }

        private sealed class SceneHydrationReport
        {
            public readonly List<string> assigned = new List<string>();
            public readonly List<string> missing = new List<string>();
        }

        private sealed class SceneHydrationContext
        {
            public readonly List<GameObject> sceneObjects = new List<GameObject>();
            public readonly List<Component> sceneComponents = new List<Component>();
            public readonly Dictionary<string, List<GameObject>> objectsByName = new Dictionary<string, List<GameObject>>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, string> prefabPathsByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, GameObject> prefabAssetsByName = new Dictionary<string, GameObject>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<Type, UnityEngine.Object> scriptableByType = new Dictionary<Type, UnityEngine.Object>();
            public readonly List<string> prefabNameHints = new List<string>();
            public GameObject managersRoot;
        }

        private static readonly Dictionary<Type, FieldInfo[]> SerializedFieldCache = new Dictionary<Type, FieldInfo[]>();

        private static void HydrateSceneReferences(ProtoFeatureRequest request, Dictionary<string, ProtoFeatureRequest> requestLookup, UnityEngine.SceneManagement.Scene scene, string scenePath)
        {
            if (request == null || !scene.IsValid())
            {
                return;
            }

            var context = BuildSceneHydrationContext(request, requestLookup, scene);
            var report = new SceneHydrationReport();

            for (var i = 0; i < context.sceneComponents.Count; i++)
            {
                if (!(context.sceneComponents[i] is MonoBehaviour behaviour))
                {
                    continue;
                }

                HydrateComponentReferences(behaviour, context, report);
            }

            if (report.assigned.Count > 0 || report.missing.Count > 0)
            {
                AppendSceneBindingIndexEntry(scenePath, report);
            }

            if (report.missing.Count > 0)
            {
                request.notes = AppendNote(request.notes, "Scene references missing. See SceneBindingIndex.md.");
            }
        }

        private static SceneHydrationContext BuildSceneHydrationContext(ProtoFeatureRequest request, Dictionary<string, ProtoFeatureRequest> requestLookup, UnityEngine.SceneManagement.Scene scene)
        {
            var context = new SceneHydrationContext();
            context.managersRoot = FindInSceneByName(scene, "Managers");

            var roots = scene.GetRootGameObjects();
            foreach (var root in roots)
            {
                CollectSceneObjects(root.transform, context.sceneObjects);
            }

            for (var i = 0; i < context.sceneObjects.Count; i++)
            {
                var obj = context.sceneObjects[i];
                if (obj == null)
                {
                    continue;
                }

                var key = NormalizeNameKey(obj.name);
                if (!context.objectsByName.TryGetValue(key, out var list))
                {
                    list = new List<GameObject>();
                    context.objectsByName[key] = list;
                }
                list.Add(obj);

                var components = obj.GetComponents<Component>();
                if (components == null)
                {
                    continue;
                }

                for (var c = 0; c < components.Length; c++)
                {
                    if (components[c] != null)
                    {
                        context.sceneComponents.Add(components[c]);
                    }
                }
            }

            var prefabIndex = ReadPrefabIndexMarkdown();
            var prefabMap = ParseIndexNamePath(prefabIndex);
            foreach (var pair in prefabMap)
            {
                var key = NormalizeNameKey(pair.Key);
                if (!context.prefabPathsByName.ContainsKey(key))
                {
                    context.prefabPathsByName[key] = pair.Value;
                }
            }

            var prefabPaths = CollectScenePrefabPaths(request, requestLookup);
            foreach (var path in prefabPaths)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                var name = Path.GetFileNameWithoutExtension(path);
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var key = NormalizeNameKey(name);
                context.prefabPathsByName[key] = path;
                AddNameHint(context.prefabNameHints, name);
            }

            foreach (var name in ParseScenePrefabNamesFromNotes(request.notes))
            {
                AddNameHint(context.prefabNameHints, name);
            }

            return context;
        }

        private static void AddNameHint(List<string> list, string name)
        {
            if (string.IsNullOrWhiteSpace(name) || list == null)
            {
                return;
            }

            for (var i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i], name, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            list.Add(name.Trim());
        }

        private static void CollectSceneObjects(Transform root, List<GameObject> results)
        {
            if (root == null || results == null)
            {
                return;
            }

            results.Add(root.gameObject);
            for (var i = 0; i < root.childCount; i++)
            {
                CollectSceneObjects(root.GetChild(i), results);
            }
        }

        private static void HydrateComponentReferences(MonoBehaviour component, SceneHydrationContext context, SceneHydrationReport report)
        {
            if (component == null || context == null || report == null)
            {
                return;
            }

            var fields = GetSerializedObjectFields(component.GetType());
            if (fields.Length == 0)
            {
                return;
            }

            var serialized = new SerializedObject(component);
            var modified = false;

            foreach (var field in fields)
            {
                if (field == null)
                {
                    continue;
                }

                if (TryAssignCollectionField(component, field, serialized, context, report))
                {
                    modified = true;
                    continue;
                }

                if (!typeof(UnityEngine.Object).IsAssignableFrom(field.FieldType))
                {
                    continue;
                }

                var prop = serialized.FindProperty(field.Name);
                if (prop == null || prop.propertyType != SerializedPropertyType.ObjectReference)
                {
                    continue;
                }

                if (prop.objectReferenceValue != null)
                {
                    continue;
                }

                var resolved = ResolveSceneReference(field, component, context);
                if (resolved != null)
                {
                    prop.objectReferenceValue = resolved;
                    modified = true;
                    report.assigned.Add($"{component.GetType().Name}.{field.Name} -> {DescribeObject(resolved)}");
                }
                else
                {
                    report.missing.Add($"{component.GetType().Name}.{field.Name} ({field.FieldType.Name})");
                }
            }

            if (modified)
            {
                serialized.ApplyModifiedProperties();
                EditorUtility.SetDirty(component);
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(component.gameObject.scene);
            }
        }

        private static bool TryAssignCollectionField(MonoBehaviour component, FieldInfo field, SerializedObject serialized, SceneHydrationContext context, SceneHydrationReport report)
        {
            if (field == null || serialized == null)
            {
                return false;
            }

            var elementType = GetCollectionElementType(field.FieldType);
            if (elementType == null || !typeof(UnityEngine.Object).IsAssignableFrom(elementType))
            {
                return false;
            }

            var prop = serialized.FindProperty(field.Name);
            if (prop == null || !prop.isArray)
            {
                return false;
            }

            if (prop.arraySize > 0)
            {
                return false;
            }

            var candidates = ResolveCollectionCandidates(field.Name, elementType, context);
            if (candidates.Count == 0)
            {
                report.missing.Add($"{component.GetType().Name}.{field.Name} ({elementType.Name}[])");
                return false;
            }

            prop.arraySize = candidates.Count;
            for (var i = 0; i < candidates.Count; i++)
            {
                var element = prop.GetArrayElementAtIndex(i);
                if (element != null)
                {
                    element.objectReferenceValue = candidates[i];
                }
            }

            report.assigned.Add($"{component.GetType().Name}.{field.Name} -> {DescribeObjectList(candidates)}");
            return true;
        }

        private static UnityEngine.Object ResolveSceneReference(FieldInfo field, MonoBehaviour component, SceneHydrationContext context)
        {
            if (field == null || context == null)
            {
                return null;
            }

            var fieldType = field.FieldType;
            var fieldName = field.Name ?? string.Empty;
            var nameCandidates = BuildFieldNameCandidates(fieldName);
            nameCandidates.Add(fieldType.Name);

            if (fieldType == typeof(GameObject))
            {
                return ResolveGameObjectReference(fieldName, nameCandidates, context);
            }

            if (fieldType == typeof(Transform))
            {
                var go = FindSceneObjectByCandidates(nameCandidates, context);
                return go != null ? go.transform : null;
            }

            if (typeof(Component).IsAssignableFrom(fieldType))
            {
                return ResolveComponentReference(fieldType, fieldName, nameCandidates, context);
            }

            if (typeof(ScriptableObject).IsAssignableFrom(fieldType))
            {
                return ResolveScriptableObjectReference(fieldType, context);
            }

            return null;
        }

        private static UnityEngine.Object ResolveGameObjectReference(string fieldName, List<string> nameCandidates, SceneHydrationContext context)
        {
            var preferPrefab = FieldNameSuggestsPrefab(fieldName);
            if (!preferPrefab)
            {
                var sceneObject = FindSceneObjectByCandidates(nameCandidates, context);
                if (sceneObject != null)
                {
                    return sceneObject;
                }
            }

            var prefab = FindPrefabByCandidates(nameCandidates, context);
            if (prefab != null)
            {
                return prefab;
            }

            if (preferPrefab)
            {
                return FindSceneObjectByCandidates(nameCandidates, context);
            }

            return null;
        }

        private static UnityEngine.Object ResolveComponentReference(Type fieldType, string fieldName, List<string> nameCandidates, SceneHydrationContext context)
        {
            var candidates = FindSceneComponentsOfType(fieldType, context);
            if (candidates.Count == 1)
            {
                return candidates[0];
            }

            var nameMatch = FindComponentByNameMatch(candidates, nameCandidates);
            if (nameMatch != null)
            {
                return nameMatch;
            }

            if (FieldNameSuggestsManager(fieldName) || FieldNameSuggestsManager(fieldType.Name))
            {
                var managerMatch = FindComponentUnderRoot(candidates, context.managersRoot);
                if (managerMatch != null)
                {
                    return managerMatch;
                }
            }

            var prefabComponent = FindPrefabComponentByCandidates(fieldType, nameCandidates, context);
            if (prefabComponent != null)
            {
                return prefabComponent;
            }

            return null;
        }

        private static UnityEngine.Object ResolveScriptableObjectReference(Type fieldType, SceneHydrationContext context)
        {
            if (context.scriptableByType.TryGetValue(fieldType, out var cached))
            {
                return cached;
            }

            var guids = AssetDatabase.FindAssets($"t:{fieldType.Name}", new[] { ProjectRoot });
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var asset = AssetDatabase.LoadAssetAtPath(path, fieldType);
                if (asset != null)
                {
                    context.scriptableByType[fieldType] = asset;
                    return asset;
                }
            }

            context.scriptableByType[fieldType] = null;
            return null;
        }

        private static List<UnityEngine.Object> ResolveCollectionCandidates(string fieldName, Type elementType, SceneHydrationContext context)
        {
            var results = new List<UnityEngine.Object>();
            if (context == null || elementType == null)
            {
                return results;
            }

            if (!FieldNameSuggestsCollection(fieldName))
            {
                return results;
            }

            if (context.prefabNameHints.Count == 0)
            {
                return results;
            }

            for (var i = 0; i < context.prefabNameHints.Count; i++)
            {
                var hint = context.prefabNameHints[i];
                if (string.IsNullOrWhiteSpace(hint))
                {
                    continue;
                }

                if (elementType == typeof(GameObject))
                {
                    var prefab = GetPrefabAssetByName(context, hint);
                    if (prefab != null)
                    {
                        AddUniqueObject(results, prefab);
                        continue;
                    }

                    var sceneObject = FindSceneObjectByCandidates(new List<string> { hint }, context);
                    if (sceneObject != null)
                    {
                        AddUniqueObject(results, sceneObject);
                    }
                }
                else if (typeof(Component).IsAssignableFrom(elementType))
                {
                    var prefab = GetPrefabAssetByName(context, hint);
                    if (prefab != null)
                    {
                        var component = prefab.GetComponent(elementType);
                        if (component != null)
                        {
                            AddUniqueObject(results, component);
                            continue;
                        }
                    }

                    var candidates = FindSceneComponentsOfType(elementType, context);
                    var nameMatch = FindComponentByNameMatch(candidates, new List<string> { hint });
                    if (nameMatch != null)
                    {
                        AddUniqueObject(results, nameMatch);
                    }
                }
            }

            return results;
        }

        private static void AddUniqueObject(List<UnityEngine.Object> list, UnityEngine.Object obj)
        {
            if (obj == null || list == null)
            {
                return;
            }

            if (!list.Contains(obj))
            {
                list.Add(obj);
            }
        }

        private static GameObject FindSceneObjectByCandidates(List<string> nameCandidates, SceneHydrationContext context)
        {
            if (context == null || nameCandidates == null)
            {
                return null;
            }

            for (var i = 0; i < nameCandidates.Count; i++)
            {
                var key = NormalizeNameKey(nameCandidates[i]);
                if (context.objectsByName.TryGetValue(key, out var list) && list.Count > 0)
                {
                    return list[0];
                }
            }

            for (var i = 0; i < nameCandidates.Count; i++)
            {
                var key = NormalizeNameKey(nameCandidates[i]);
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                foreach (var pair in context.objectsByName)
                {
                    if (pair.Value.Count == 0)
                    {
                        continue;
                    }

                    if (pair.Key.Contains(key) || key.Contains(pair.Key))
                    {
                        return pair.Value[0];
                    }
                }
            }

            return null;
        }

        private static List<Component> FindSceneComponentsOfType(Type fieldType, SceneHydrationContext context)
        {
            var results = new List<Component>();
            if (fieldType == null || context == null)
            {
                return results;
            }

            for (var i = 0; i < context.sceneComponents.Count; i++)
            {
                var component = context.sceneComponents[i];
                if (component == null)
                {
                    continue;
                }

                var type = component.GetType();
                if (fieldType.IsAssignableFrom(type))
                {
                    results.Add(component);
                }
            }

            return results;
        }

        private static Component FindComponentByNameMatch(List<Component> candidates, List<string> nameCandidates)
        {
            if (candidates == null || nameCandidates == null)
            {
                return null;
            }

            for (var i = 0; i < nameCandidates.Count; i++)
            {
                var key = NormalizeNameKey(nameCandidates[i]);
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                for (var c = 0; c < candidates.Count; c++)
                {
                    var candidate = candidates[c];
                    if (candidate == null)
                    {
                        continue;
                    }

                    if (NamesMatch(candidate.gameObject.name, key) || NamesMatch(candidate.GetType().Name, key))
                    {
                        return candidate;
                    }
                }
            }

            return null;
        }

        private static Component FindComponentUnderRoot(List<Component> candidates, GameObject root)
        {
            if (root == null || candidates == null)
            {
                return null;
            }

            for (var i = 0; i < candidates.Count; i++)
            {
                var candidate = candidates[i];
                if (candidate == null)
                {
                    continue;
                }

                if (IsUnderRoot(candidate.transform, root.transform))
                {
                    return candidate;
                }
            }

            return null;
        }

        private static bool IsUnderRoot(Transform transform, Transform root)
        {
            if (transform == null || root == null)
            {
                return false;
            }

            var current = transform;
            while (current != null)
            {
                if (current == root)
                {
                    return true;
                }
                current = current.parent;
            }

            return false;
        }

        private static Component FindPrefabComponentByCandidates(Type componentType, List<string> nameCandidates, SceneHydrationContext context)
        {
            if (componentType == null || nameCandidates == null || context == null)
            {
                return null;
            }

            for (var i = 0; i < nameCandidates.Count; i++)
            {
                var prefab = GetPrefabAssetByName(context, nameCandidates[i]);
                if (prefab == null)
                {
                    continue;
                }

                var component = prefab.GetComponent(componentType);
                if (component != null)
                {
                    return component;
                }
            }

            return null;
        }

        private static GameObject FindPrefabByCandidates(List<string> nameCandidates, SceneHydrationContext context)
        {
            if (context == null || nameCandidates == null)
            {
                return null;
            }

            for (var i = 0; i < nameCandidates.Count; i++)
            {
                var prefab = GetPrefabAssetByName(context, nameCandidates[i]);
                if (prefab != null)
                {
                    return prefab;
                }
            }

            return null;
        }

        private static GameObject GetPrefabAssetByName(SceneHydrationContext context, string name)
        {
            if (context == null || string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            var key = NormalizeNameKey(name);
            if (context.prefabAssetsByName.TryGetValue(key, out var cached))
            {
                return cached;
            }

            if (!context.prefabPathsByName.TryGetValue(key, out var path))
            {
                path = ResolvePrefabPathByName(name);
            }

            GameObject prefab = null;
            if (!string.IsNullOrWhiteSpace(path))
            {
                prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            }

            context.prefabAssetsByName[key] = prefab;
            return prefab;
        }

        private static FieldInfo[] GetSerializedObjectFields(Type type)
        {
            if (type == null)
            {
                return Array.Empty<FieldInfo>();
            }

            if (SerializedFieldCache.TryGetValue(type, out var cached))
            {
                return cached;
            }

            var fields = new List<FieldInfo>();
            var current = type;
            while (current != null && current != typeof(MonoBehaviour))
            {
                var declared = current.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                for (var i = 0; i < declared.Length; i++)
                {
                    var field = declared[i];
                    if (field == null || field.IsStatic)
                    {
                        continue;
                    }

                    if (field.IsDefined(typeof(NonSerializedAttribute), true))
                    {
                        continue;
                    }

                    var isSerialized = field.IsPublic || field.IsDefined(typeof(SerializeField), true);
                    if (!isSerialized)
                    {
                        continue;
                    }

                    if (IsSerializableObjectReferenceFieldType(field.FieldType))
                    {
                        fields.Add(field);
                    }
                }

                current = current.BaseType;
            }

            var result = fields.ToArray();
            SerializedFieldCache[type] = result;
            return result;
        }

        private static bool IsSerializableObjectReferenceFieldType(Type type)
        {
            if (type == null)
            {
                return false;
            }

            if (typeof(UnityEngine.Object).IsAssignableFrom(type))
            {
                return true;
            }

            var elementType = GetCollectionElementType(type);
            return elementType != null && typeof(UnityEngine.Object).IsAssignableFrom(elementType);
        }

        private static Type GetCollectionElementType(Type type)
        {
            if (type == null)
            {
                return null;
            }

            if (type.IsArray)
            {
                return type.GetElementType();
            }

            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
            {
                var args = type.GetGenericArguments();
                if (args.Length == 1)
                {
                    return args[0];
                }
            }

            return null;
        }

        private static List<string> BuildFieldNameCandidates(string fieldName)
        {
            var results = new List<string>();
            if (string.IsNullOrWhiteSpace(fieldName))
            {
                return results;
            }

            results.Add(fieldName);
            var trimmed = fieldName.Trim();
            if (trimmed.StartsWith("m_", StringComparison.OrdinalIgnoreCase))
            {
                results.Add(trimmed.Substring(2));
            }
            if (trimmed.StartsWith("_", StringComparison.OrdinalIgnoreCase))
            {
                results.Add(trimmed.Substring(1));
            }

            var suffixes = new[] { "Prefab", "Template", "Ref", "Reference", "Object", "Go", "Transform" };
            for (var i = 0; i < suffixes.Length; i++)
            {
                var suffix = suffixes[i];
                if (trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && trimmed.Length > suffix.Length)
                {
                    results.Add(trimmed.Substring(0, trimmed.Length - suffix.Length));
                }
            }

            return results;
        }

        private static bool FieldNameSuggestsPrefab(string fieldName)
        {
            if (string.IsNullOrWhiteSpace(fieldName))
            {
                return false;
            }

            var lower = fieldName.ToLowerInvariant();
            return lower.Contains("prefab") || lower.Contains("template");
        }

        private static bool FieldNameSuggestsManager(string fieldName)
        {
            if (string.IsNullOrWhiteSpace(fieldName))
            {
                return false;
            }

            return fieldName.IndexOf("manager", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool FieldNameSuggestsCollection(string fieldName)
        {
            if (string.IsNullOrWhiteSpace(fieldName))
            {
                return false;
            }

            var lower = fieldName.ToLowerInvariant();
            return lower.Contains("prefab")
                || lower.Contains("template")
                || lower.Contains("list")
                || lower.Contains("array")
                || lower.Contains("rooms")
                || lower.Contains("spawns")
                || lower.Contains("points");
        }

        private static string NormalizeNameKey(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var builder = new StringBuilder(value.Length);
            foreach (var ch in value)
            {
                if (char.IsLetterOrDigit(ch))
                {
                    builder.Append(char.ToLowerInvariant(ch));
                }
            }

            return builder.ToString();
        }

        private static bool NamesMatch(string candidate, string normalizedTarget)
        {
            if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(normalizedTarget))
            {
                return false;
            }

            var candidateKey = NormalizeNameKey(candidate);
            return candidateKey == normalizedTarget ||
                   candidateKey.Contains(normalizedTarget) ||
                   normalizedTarget.Contains(candidateKey);
        }

        private static Dictionary<string, string> ParseIndexNamePath(string content)
        {
            var results = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(content))
            {
                return results;
            }

            var lines = content.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("- ", StringComparison.Ordinal))
                {
                    continue;
                }

                var open = trimmed.LastIndexOf('(');
                var close = trimmed.LastIndexOf(')');
                if (open < 0 || close <= open)
                {
                    continue;
                }

                var name = trimmed.Substring(2, open - 2).Trim();
                var path = trimmed.Substring(open + 1, close - open - 1).Trim();
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                if (!results.ContainsKey(name))
                {
                    results[name] = path;
                }
            }

            return results;
        }

        private static string DescribeObject(UnityEngine.Object obj)
        {
            if (obj == null)
            {
                return "null";
            }

            if (obj is GameObject gameObject)
            {
                return $"{gameObject.name} (GameObject)";
            }

            if (obj is Component component)
            {
                return $"{component.GetType().Name} on {component.gameObject.name}";
            }

            return $"{obj.name} ({obj.GetType().Name})";
        }

        private static string DescribeObjectList(List<UnityEngine.Object> list)
        {
            if (list == null || list.Count == 0)
            {
                return "(none)";
            }

            var builder = new StringBuilder();
            var count = Math.Min(list.Count, 6);
            for (var i = 0; i < count; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }
                builder.Append(DescribeObject(list[i]));
            }

            if (list.Count > count)
            {
                builder.Append($" (+{list.Count - count} more)");
            }

            return builder.ToString();
        }

        private static void AppendSceneBindingIndexEntry(string scenePath, SceneHydrationReport report)
        {
            if (report == null || (report.assigned.Count == 0 && report.missing.Count == 0))
            {
                return;
            }

            var folder = ProtoFeatureRequestStore.EnsureFeatureRequestFolder();
            var path = $"{folder}/SceneBindingIndex.md";
            var fullPath = Path.GetFullPath(path);
            var existing = File.Exists(fullPath)
                ? File.ReadAllText(fullPath)
                : "# Scene Binding Index\n";

            var sceneName = string.IsNullOrWhiteSpace(scenePath) ? "Scene" : Path.GetFileNameWithoutExtension(scenePath);
            var builder = new StringBuilder();
            builder.AppendLine();
            builder.AppendLine($"## {sceneName} ({scenePath})");
            builder.AppendLine($"Generated: {DateTime.UtcNow:o}");
            builder.AppendLine();
            builder.AppendLine("### Bound");
            if (report.assigned.Count == 0)
            {
                builder.AppendLine("- (none)");
            }
            else
            {
                foreach (var entry in report.assigned)
                {
                    builder.AppendLine($"- {entry}");
                }
            }

            builder.AppendLine();
            builder.AppendLine("### Missing");
            if (report.missing.Count == 0)
            {
                builder.AppendLine("- (none)");
            }
            else
            {
                foreach (var entry in report.missing)
                {
                    builder.AppendLine($"- {entry}");
                }
            }

            var updated = existing.TrimEnd() + builder.ToString().TrimEnd() + "\n";
            File.WriteAllText(fullPath, updated);
        }

        private static bool ShouldCreateSceneUi(ProtoFeatureRequest request, Dictionary<string, ProtoFeatureRequest> requestLookup)
        {
            if (NotesContainAny(request.notes, new[] { "ui", "hud", "canvas", "eventsystem" }))
            {
                return true;
            }

            if (requestLookup != null && request.dependsOn != null)
            {
                foreach (var depId in request.dependsOn)
                {
                    if (string.IsNullOrWhiteSpace(depId))
                    {
                        continue;
                    }

                    if (!requestLookup.TryGetValue(depId, out var depRequest) || depRequest == null)
                    {
                        continue;
                    }

                    var name = depRequest.name ?? string.Empty;
                    if (name.IndexOf("HUD", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("UI", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool NotesContainAny(string notes, string[] keywords)
        {
            if (string.IsNullOrWhiteSpace(notes) || keywords == null || keywords.Length == 0)
            {
                return false;
            }

            var lower = notes.ToLowerInvariant();
            foreach (var keyword in keywords)
            {
                if (!string.IsNullOrWhiteSpace(keyword) && lower.Contains(keyword.ToLowerInvariant()))
                {
                    return true;
                }
            }

            return false;
        }

        private static void AddScenePrefabs(ProtoFeatureRequest request, Dictionary<string, ProtoFeatureRequest> requestLookup, UnityEngine.SceneManagement.Scene scene)
        {
            if (request == null || !scene.IsValid())
            {
                return;
            }

            var prefabPaths = CollectScenePrefabPaths(request, requestLookup);
            if (prefabPaths.Count == 0)
            {
                return;
            }

            UnityEngine.SceneManagement.SceneManager.SetActiveScene(scene);

            foreach (var path in prefabPaths)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null)
                {
                    continue;
                }

                if (FindInSceneByName(scene, prefab.name) != null)
                {
                    continue;
                }

                var instance = PrefabUtility.InstantiatePrefab(prefab, scene) as GameObject;
                if (instance != null)
                {
                    instance.name = prefab.name;
                }
            }
        }

        private static void AddSceneManagers(ProtoFeatureRequest request, Dictionary<string, ProtoFeatureRequest> requestLookup, UnityEngine.SceneManagement.Scene scene)
        {
            if (request == null || !scene.IsValid())
            {
                return;
            }

            var componentNames = CollectSceneManagerComponents(request, requestLookup);
            if (componentNames.Count == 0)
            {
                return;
            }

            UnityEngine.SceneManagement.SceneManager.SetActiveScene(scene);
            var managersRoot = FindInSceneByName(scene, "Managers") ?? new GameObject("Managers");

            var missing = new List<string>();
            foreach (var name in componentNames)
            {
                var type = FindTypeByName(name);
                if (type == null)
                {
                    missing.Add(name);
                    continue;
                }

                if (managersRoot.GetComponent(type) != null)
                {
                    continue;
                }

                managersRoot.AddComponent(type);
            }

            if (missing.Count > 0)
            {
                request.notes = AppendNote(request.notes, $"Missing manager scripts: {string.Join(", ", missing)}.");
            }
        }

        private static List<string> CollectSceneManagerComponents(ProtoFeatureRequest request, Dictionary<string, ProtoFeatureRequest> requestLookup)
        {
            var results = new List<string>();
            var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (requestLookup != null && request.dependsOn != null)
            {
                foreach (var depId in request.dependsOn)
                {
                    if (string.IsNullOrWhiteSpace(depId))
                    {
                        continue;
                    }

                    if (!requestLookup.TryGetValue(depId, out var depRequest))
                    {
                        continue;
                    }

                    if (depRequest == null || !string.Equals(depRequest.type, "script", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var name = depRequest.name?.Trim();
                    if (!string.IsNullOrWhiteSpace(name) && unique.Add(name))
                    {
                        results.Add(name);
                    }
                }
            }

            foreach (var name in ParseSceneManagerNamesFromNotes(request.notes))
            {
                if (unique.Add(name))
                {
                    results.Add(name);
                }
            }

            return results;
        }

        private static IEnumerable<string> ParseSceneManagerNamesFromNotes(string notes)
        {
            if (string.IsNullOrWhiteSpace(notes))
            {
                yield break;
            }

            var lines = notes.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                var lower = trimmed.ToLowerInvariant();
                var index = lower.IndexOf("managers:", StringComparison.Ordinal);
                if (index < 0)
                {
                    index = lower.IndexOf("manager:", StringComparison.Ordinal);
                }
                if (index < 0)
                {
                    index = lower.IndexOf("gestores:", StringComparison.Ordinal);
                }
                if (index < 0)
                {
                    continue;
                }

                var list = trimmed.Substring(index);
                var colon = list.IndexOf(':');
                if (colon < 0 || colon >= list.Length - 1)
                {
                    continue;
                }

                var items = list.Substring(colon + 1);
                foreach (var entry in items.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var name = entry.Trim();
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        yield return name;
                    }
                }
            }
        }

        private static List<string> CollectScenePrefabPaths(ProtoFeatureRequest request, Dictionary<string, ProtoFeatureRequest> requestLookup)
        {
            var results = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (requestLookup != null && request.dependsOn != null)
            {
                foreach (var depId in request.dependsOn)
                {
                    if (string.IsNullOrWhiteSpace(depId))
                    {
                        continue;
                    }

                    if (!requestLookup.TryGetValue(depId, out var depRequest))
                    {
                        continue;
                    }

                    if (depRequest == null)
                    {
                        continue;
                    }

                    var isPrefab = string.Equals(depRequest.type, "prefab", StringComparison.OrdinalIgnoreCase)
                        || (string.Equals(depRequest.type, "asset", StringComparison.OrdinalIgnoreCase) && LooksLikePrefabRequest(depRequest));
                    if (!isPrefab)
                    {
                        continue;
                    }

                    var prefabName = GetPrefabName(depRequest);
                    var prefabPath = BuildPrefabPath(depRequest.path, prefabName, out _);
                    if (!string.IsNullOrWhiteSpace(prefabPath) && seen.Add(prefabPath))
                    {
                        results.Add(prefabPath);
                    }
                }
            }

            foreach (var name in ParseScenePrefabNamesFromNotes(request.notes))
            {
                var path = ResolvePrefabPathByName(name);
                if (!string.IsNullOrWhiteSpace(path) && seen.Add(path))
                {
                    results.Add(path);
                }
            }

            return results;
        }

        private static IEnumerable<string> ParseScenePrefabNamesFromNotes(string notes)
        {
            if (string.IsNullOrWhiteSpace(notes))
            {
                yield break;
            }

            var lines = notes.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                var lower = trimmed.ToLowerInvariant();
                var index = lower.IndexOf("prefabs:", StringComparison.Ordinal);
                if (index < 0)
                {
                    index = lower.IndexOf("assets:", StringComparison.Ordinal);
                }
                if (index < 0)
                {
                    index = lower.IndexOf("includes:", StringComparison.Ordinal);
                }
                if (index < 0)
                {
                    index = lower.IndexOf("prefabs en escena:", StringComparison.Ordinal);
                }
                if (index < 0)
                {
                    index = lower.IndexOf("incluye:", StringComparison.Ordinal);
                }
                if (index < 0)
                {
                    continue;
                }

                var list = trimmed.Substring(index);
                var colon = list.IndexOf(':');
                if (colon < 0 || colon >= list.Length - 1)
                {
                    continue;
                }

                var items = list.Substring(colon + 1);
                foreach (var entry in items.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var name = entry.Trim();
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        yield return name;
                    }
                }
            }
        }

        private static string ResolvePrefabPathByName(string prefabName)
        {
            if (string.IsNullOrWhiteSpace(prefabName))
            {
                return string.Empty;
            }

            var search = $"{prefabName} t:Prefab";
            var guids = AssetDatabase.FindAssets(search);
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileNameWithoutExtension(path).Equals(prefabName, StringComparison.OrdinalIgnoreCase))
                {
                    return path;
                }
            }

            return string.Empty;
        }

        private sealed class PrefabAttachmentPlan
        {
            public readonly ProtoFeatureRequest request;
            public readonly string prefabPath;
            public readonly List<string> componentNames;

            public PrefabAttachmentPlan(ProtoFeatureRequest request, string prefabPath, List<string> componentNames)
            {
                this.request = request;
                this.prefabPath = prefabPath;
                this.componentNames = componentNames;
            }
        }

        private static string AppendNote(string notes, string addition)
        {
            var cleanAddition = addition?.Trim();
            if (string.IsNullOrWhiteSpace(cleanAddition))
            {
                return notes ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(notes))
            {
                return cleanAddition;
            }

            var cleanNotes = notes.Trim();
            if (cleanNotes.IndexOf(cleanAddition, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return cleanNotes;
            }

            return $"{cleanNotes} {cleanAddition}";
        }

        private static Task RunOnMainThread(Action action)
        {
            var tcs = new TaskCompletionSource<bool>();
            EditorApplication.delayCall += () =>
            {
                try
                {
                    action();
                    tcs.SetResult(true);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            };
            return tcs.Task;
        }

        private static bool IsReviewScriptCommand(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            var lower = text.ToLowerInvariant();
            var hasReview = lower.Contains("review") || lower.Contains("revisa") || lower.Contains("revisar") || lower.Contains("revise");
            var hasTarget = lower.Contains(".cs") || lower.Contains("script") || lower.Contains("archivo");
            return hasReview && hasTarget;
        }

        private static ScriptResolveResult ResolveScriptPath(string userText)
        {
            var result = new ScriptResolveResult();
            if (string.IsNullOrWhiteSpace(userText))
            {
                result.error = "No script specified.";
                return result;
            }

            var query = ExtractScriptQuery(userText);
            if (string.IsNullOrWhiteSpace(query))
            {
                result.error = "Script name not found in the message.";
                return result;
            }

            query = query.Trim().Trim('"', '\'');
            var normalizedQuery = query.Replace('\\', '/');
            if (normalizedQuery.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                var path = normalizedQuery.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                    ? normalizedQuery
                    : $"{normalizedQuery}.cs";
                if (AssetDatabase.LoadAssetAtPath<MonoScript>(path) != null)
                {
                    result.success = true;
                    result.assetPath = path;
                    return result;
                }
            }

            var name = Path.GetFileNameWithoutExtension(normalizedQuery);
            if (string.IsNullOrWhiteSpace(name))
            {
                result.error = "Script name not found in the message.";
                return result;
            }

            var search = $"{name} t:MonoScript";
            var guids = AssetDatabase.FindAssets(search);
            var matches = new List<string>();
            var withPathHint = normalizedQuery.Contains("/") ? normalizedQuery : string.Empty;
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!Path.GetFileNameWithoutExtension(path).Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(withPathHint) &&
                    !path.EndsWith(withPathHint, StringComparison.OrdinalIgnoreCase) &&
                    !path.EndsWith($"{withPathHint}.cs", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                matches.Add(path);
            }

            if (matches.Count == 1)
            {
                result.success = true;
                result.assetPath = matches[0];
                return result;
            }

            if (matches.Count == 0)
            {
                result.error = $"Script '{name}' not found.";
                return result;
            }

            var preview = matches.Count > 5 ? string.Join(", ", matches.GetRange(0, 5)) + ", ..." : string.Join(", ", matches);
            result.error = $"Multiple scripts found: {preview}.";
            return result;
        }

        private static string ExtractScriptQuery(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            var match = Regex.Match(text, @"[A-Za-z0-9_\-\\/\.]+\.cs", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                return match.Value;
            }

            match = Regex.Match(text, @"\b(script|archivo|file)\s+([A-Za-z0-9_\-\\/\.]+)", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                return match.Groups[2].Value;
            }

            return string.Empty;
        }

        private static string ExtractReviewSummary(string response)
        {
            if (string.IsNullOrWhiteSpace(response))
            {
                return string.Empty;
            }

            var trimmed = response.Trim();
            if (trimmed.StartsWith("NO_CHANGES", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed;
            }

            var fence = "```";
            var start = trimmed.IndexOf(fence, StringComparison.Ordinal);
            if (start >= 0)
            {
                return trimmed.Substring(0, start).Trim();
            }

            return trimmed;
        }

        private static string NormalizeLineEndings(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            return text.Replace("\r\n", "\n").Replace("\r", "\n");
        }

        private string BuildScriptContractContext(ProtoFeatureRequest request, Dictionary<string, ProtoFeatureRequest> requestLookup)
        {
            var index = GetScriptContractIndex();
            var dependencySummary = BuildDependencySummary(request, requestLookup, index);
            var indexSummary = BuildScriptIndexSummary(index);

            if (string.IsNullOrWhiteSpace(dependencySummary) && string.IsNullOrWhiteSpace(indexSummary))
            {
                return string.Empty;
            }

            var builder = new StringBuilder(2048);
            builder.AppendLine("Use only the public members listed below when calling other scripts. Do not invent method or field names.");
            builder.AppendLine("If you need new API, add it in your own script or avoid calling it.");

            if (!string.IsNullOrWhiteSpace(dependencySummary))
            {
                builder.AppendLine("Dependency API:");
                builder.AppendLine(dependencySummary);
            }

            if (!string.IsNullOrWhiteSpace(indexSummary))
            {
                builder.AppendLine("Project API (subset):");
                builder.AppendLine(indexSummary);
            }

            return builder.ToString().Trim();
        }

        private ScriptContractIndex GetScriptContractIndex()
        {
            if (_scriptIndexCache != null && !_scriptIndexDirty)
            {
                return _scriptIndexCache;
            }

            _scriptIndexCache = BuildScriptContractIndex();
            _scriptIndexDirty = false;
            return _scriptIndexCache;
        }

        private ScriptContractIndex BuildScriptContractIndex()
        {
            var index = new ScriptContractIndex();
            var assetsPath = Application.dataPath;
            if (string.IsNullOrWhiteSpace(assetsPath) || !Directory.Exists(assetsPath))
            {
                return index;
            }

            var files = Directory.GetFiles(assetsPath, "*.cs", SearchOption.AllDirectories);
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);

            foreach (var file in files)
            {
                var assetPath = ToAssetPath(file, assetsPath);
                if (string.IsNullOrWhiteSpace(assetPath))
                {
                    continue;
                }

                var entries = ParseScriptFile(file, assetPath);
                foreach (var entry in entries)
                {
                    if (entry == null || string.IsNullOrWhiteSpace(entry.name))
                    {
                        continue;
                    }

                    index.entries.Add(entry);
                    if (!index.byName.ContainsKey(entry.name) ||
                        entry.path.StartsWith(ProjectRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        index.byName[entry.name] = entry;
                    }
                }
            }

            index.totalCount = index.entries.Count;
            return index;
        }

        private static string BuildScriptIndexSummary(ScriptContractIndex index)
        {
            if (index == null || index.entries.Count == 0)
            {
                return string.Empty;
            }

            var ordered = new List<ScriptContractEntry>(index.entries);
            ordered.Sort((a, b) => string.Compare(a.path, b.path, StringComparison.OrdinalIgnoreCase));

            var builder = new StringBuilder(2048);
            var count = 0;
            foreach (var entry in ordered)
            {
                if (count >= MaxScriptIndexEntries || builder.Length >= MaxScriptIndexChars)
                {
                    break;
                }

                builder.AppendLine($"- {entry.DisplayName} ({entry.path})");
                var memberCount = 0;
                foreach (var member in entry.members)
                {
                    if (memberCount >= MaxMembersPerScript)
                    {
                        break;
                    }

                    builder.AppendLine($"  - {member}");
                    memberCount++;
                }
                count++;
            }

            if (index.totalCount > count)
            {
                builder.AppendLine($"(Truncated: showing {count} of {index.totalCount} scripts)");
            }

            return builder.ToString().Trim();
        }

        private string BuildScriptIndexMarkdown()
        {
            var index = GetScriptContractIndex();
            var summary = BuildScriptIndexSummary(index);
            var builder = new StringBuilder(2048);
            builder.AppendLine("# Script Index");
            builder.AppendLine();
            builder.AppendLine($"Generated: {DateTime.UtcNow:o}");
            builder.AppendLine();
            builder.AppendLine("## Existing Scripts");
            if (string.IsNullOrWhiteSpace(summary))
            {
                builder.AppendLine("(No script entries found)");
            }
            else
            {
                builder.AppendLine(summary);
            }

            var content = builder.ToString().TrimEnd() + "\n";
            return WriteScriptIndexMarkdownContent(content);
        }

        private string WriteScriptIndexMarkdownContent(string content)
        {
            var folder = ProtoFeatureRequestStore.EnsureFeatureRequestFolder();
            var path = $"{folder}/ScriptIndex.md";
            var fullPath = Path.GetFullPath(path);
            File.WriteAllText(fullPath, content ?? string.Empty);
            AssetDatabase.Refresh();
            return content ?? string.Empty;
        }

        private static string ReadScriptIndexMarkdown()
        {
            var fullPath = Path.GetFullPath("Assets/Plan/ScriptIndex.md");
            if (!File.Exists(fullPath))
            {
                return string.Empty;
            }

            var content = File.ReadAllText(fullPath);
            return ClampScriptIndex(content);
        }

        private static string ClampScriptIndex(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return string.Empty;
            }

            if (content.Length > MaxScriptIndexChars)
            {
                return content.Substring(0, MaxScriptIndexChars);
            }

            return content;
        }

        private static string ClampPlanIndex(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return string.Empty;
            }

            if (content.Length > MaxScriptIndexChars)
            {
                return content.Substring(0, MaxScriptIndexChars);
            }

            return content;
        }

        private string BuildPrefabIndexMarkdown()
        {
            var lines = BuildPrefabIndexLines(out var totalCount);
            var content = BuildIndexMarkdown("Prefab Index", "Existing Prefabs", lines, totalCount, "No prefabs found");
            return WritePrefabIndexMarkdownContent(content);
        }

        private string BuildSceneIndexMarkdown()
        {
            var lines = BuildSceneIndexLines(out var totalCount);
            var content = BuildIndexMarkdown("Scene Index", "Existing Scenes", lines, totalCount, "No scenes found");
            return WriteSceneIndexMarkdownContent(content);
        }

        private string BuildAssetIndexMarkdown()
        {
            var lines = BuildAssetIndexLines(out var totalCount);
            var content = BuildIndexMarkdown("Asset Index", "Existing Assets", lines, totalCount, "No assets found");
            return WriteAssetIndexMarkdownContent(content);
        }

        private static string BuildIndexMarkdown(string title, string sectionTitle, List<string> lines, int totalCount, string emptyMessage)
        {
            var builder = new StringBuilder(2048);
            builder.AppendLine($"# {title}");
            builder.AppendLine();
            builder.AppendLine($"Generated: {DateTime.UtcNow:o}");
            builder.AppendLine();
            builder.AppendLine($"## {sectionTitle}");
            if (lines == null || lines.Count == 0)
            {
                builder.AppendLine($"({emptyMessage})");
            }
            else
            {
                foreach (var line in lines)
                {
                    builder.AppendLine(line);
                }
            }

            if (totalCount > (lines?.Count ?? 0))
            {
                builder.AppendLine($"(Truncated: showing {lines?.Count ?? 0} of {totalCount} items)");
            }

            return builder.ToString().TrimEnd() + "\n";
        }

        private static List<string> BuildPrefabIndexLines(out int totalCount)
        {
            var results = new List<string>();
            totalCount = 0;
            var guids = AssetDatabase.FindAssets("t:Prefab", new[] { ProjectRoot });
            if (guids == null || guids.Length == 0)
            {
                return results;
            }

            var paths = new List<string>(guids.Length);
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrWhiteSpace(path))
                {
                    paths.Add(path);
                }
            }

            paths.Sort(StringComparer.OrdinalIgnoreCase);
            totalCount = paths.Count;

            foreach (var path in paths)
            {
                if (results.Count >= MaxPrefabIndexEntries)
                {
                    break;
                }

                var name = Path.GetFileNameWithoutExtension(path);
                results.Add($"- {name} ({path})");
            }

            return results;
        }

        private static List<string> BuildSceneIndexLines(out int totalCount)
        {
            var results = new List<string>();
            totalCount = 0;
            var guids = AssetDatabase.FindAssets("t:Scene", new[] { ProjectRoot });
            if (guids == null || guids.Length == 0)
            {
                return results;
            }

            var paths = new List<string>(guids.Length);
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrWhiteSpace(path))
                {
                    paths.Add(path);
                }
            }

            paths.Sort(StringComparer.OrdinalIgnoreCase);
            totalCount = paths.Count;

            foreach (var path in paths)
            {
                if (results.Count >= MaxSceneIndexEntries)
                {
                    break;
                }

                var name = Path.GetFileNameWithoutExtension(path);
                results.Add($"- {name} ({path})");
            }

            return results;
        }

        private static List<string> BuildAssetIndexLines(out int totalCount)
        {
            var results = new List<string>();
            totalCount = 0;
            var guids = AssetDatabase.FindAssets(string.Empty, new[] { ProjectRoot });
            if (guids == null || guids.Length == 0)
            {
                return results;
            }

            var paths = new List<string>(guids.Length);
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrWhiteSpace(path) || AssetDatabase.IsValidFolder(path))
                {
                    continue;
                }

                var extension = Path.GetExtension(path);
                if (extension.Equals(".cs", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".unity", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".prefab", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".meta", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                paths.Add(path);
            }

            paths.Sort(StringComparer.OrdinalIgnoreCase);
            totalCount = paths.Count;

            foreach (var path in paths)
            {
                if (results.Count >= MaxAssetIndexEntries)
                {
                    break;
                }

                var name = Path.GetFileNameWithoutExtension(path);
                var type = AssetDatabase.GetMainAssetTypeAtPath(path);
                var typeName = type != null ? type.Name : "Asset";
                results.Add($"- {name} [{typeName}] ({path})");
            }

            return results;
        }

        private string WritePrefabIndexMarkdownContent(string content)
        {
            var folder = ProtoFeatureRequestStore.EnsureFeatureRequestFolder();
            var path = $"{folder}/PrefabIndex.md";
            var fullPath = Path.GetFullPath(path);
            File.WriteAllText(fullPath, content ?? string.Empty);
            AssetDatabase.Refresh();
            return content ?? string.Empty;
        }

        private string WriteSceneIndexMarkdownContent(string content)
        {
            var folder = ProtoFeatureRequestStore.EnsureFeatureRequestFolder();
            var path = $"{folder}/SceneIndex.md";
            var fullPath = Path.GetFullPath(path);
            File.WriteAllText(fullPath, content ?? string.Empty);
            AssetDatabase.Refresh();
            return content ?? string.Empty;
        }

        private string WriteAssetIndexMarkdownContent(string content)
        {
            var folder = ProtoFeatureRequestStore.EnsureFeatureRequestFolder();
            var path = $"{folder}/AssetIndex.md";
            var fullPath = Path.GetFullPath(path);
            File.WriteAllText(fullPath, content ?? string.Empty);
            AssetDatabase.Refresh();
            return content ?? string.Empty;
        }

        private static string ReadPrefabIndexMarkdown()
        {
            var fullPath = Path.GetFullPath("Assets/Plan/PrefabIndex.md");
            if (!File.Exists(fullPath))
            {
                return string.Empty;
            }

            var content = File.ReadAllText(fullPath);
            return ClampPlanIndex(content);
        }

        private static string ReadSceneIndexMarkdown()
        {
            var fullPath = Path.GetFullPath("Assets/Plan/SceneIndex.md");
            if (!File.Exists(fullPath))
            {
                return string.Empty;
            }

            var content = File.ReadAllText(fullPath);
            return ClampPlanIndex(content);
        }

        private static string ReadAssetIndexMarkdown()
        {
            var fullPath = Path.GetFullPath("Assets/Plan/AssetIndex.md");
            if (!File.Exists(fullPath))
            {
                return string.Empty;
            }

            var content = File.ReadAllText(fullPath);
            return ClampPlanIndex(content);
        }

        private static string ExtractMissingTypeName(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return string.Empty;
            }

            var match = Regex.Match(message, @"type or namespace name '([^']+)'", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                return match.Groups[1].Value.Trim();
            }

            match = Regex.Match(message, @"type or namespace name `([^`]+)`", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                return match.Groups[1].Value.Trim();
            }

            return string.Empty;
        }

        private static bool ScriptIndexContainsType(string scriptIndex, string typeName)
        {
            if (string.IsNullOrWhiteSpace(scriptIndex) || string.IsNullOrWhiteSpace(typeName))
            {
                return false;
            }

            var pattern = $@"^-\s+.*\b{Regex.Escape(typeName)}\b.*\(";
            return Regex.IsMatch(scriptIndex, pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline);
        }

        private static string AppendPlannedScriptsToIndex(string currentIndex, ProtoFeatureRequest[] requests, string phaseLabel, HashSet<string> plannedScripts)
        {
            if (requests == null || requests.Length == 0)
            {
                return currentIndex ?? string.Empty;
            }

            var plannedLines = CollectPlannedScriptLines(requests, plannedScripts);
            if (plannedLines.Count == 0)
            {
                return currentIndex ?? string.Empty;
            }

            var existing = currentIndex ?? string.Empty;
            var builder = new StringBuilder(2048);
            if (string.IsNullOrWhiteSpace(existing))
            {
                builder.AppendLine("# Script Index");
                builder.AppendLine();
                builder.AppendLine($"Generated: {DateTime.UtcNow:o}");
                builder.AppendLine();
                builder.AppendLine("## Existing Scripts");
                builder.AppendLine("(No script entries found)");
                builder.AppendLine();
            }
            else
            {
                builder.Append(existing.TrimEnd());
                builder.AppendLine();
            }

            if (existing.IndexOf("## Planned Scripts", StringComparison.OrdinalIgnoreCase) < 0)
            {
                builder.AppendLine("## Planned Scripts");
            }

            if (!string.IsNullOrWhiteSpace(phaseLabel))
            {
                builder.AppendLine($"### {phaseLabel}");
            }

            foreach (var line in plannedLines)
            {
                builder.AppendLine($"- {line}");
            }

            var content = builder.ToString().TrimEnd() + "\n";
            return ClampScriptIndex(content);
        }

        private static List<string> CollectPlannedScriptLines(ProtoFeatureRequest[] requests, HashSet<string> plannedScripts)
        {
            var results = new List<string>();
            if (requests == null || plannedScripts == null)
            {
                return results;
            }

            foreach (var request in requests)
            {
                if (request == null)
                {
                    continue;
                }

                if (!string.Equals(request.type, "script", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var name = ExtractPlannedScriptName(request);
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                if (!plannedScripts.Add(name))
                {
                    continue;
                }

                var folder = NormalizeFolderPath(request.path);
                var plannedPath = string.IsNullOrWhiteSpace(folder) ? string.Empty : $"{folder}/{name}.cs";
                results.Add(string.IsNullOrWhiteSpace(plannedPath) ? name : $"{name} ({plannedPath})");
            }

            return results;
        }

        private static string ExtractPlannedScriptName(ProtoFeatureRequest request)
        {
            if (request == null)
            {
                return string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(request.name))
            {
                return request.name.Trim();
            }

            if (string.IsNullOrWhiteSpace(request.path))
            {
                return string.Empty;
            }

            var normalized = request.path.Replace('\\', '/').TrimEnd('/');
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return string.Empty;
            }

            var fileName = Path.GetFileNameWithoutExtension(normalized);
            return string.IsNullOrWhiteSpace(fileName) ? string.Empty : fileName.Trim();
        }

        private static string AppendPlannedPrefabsToIndex(string currentIndex, ProtoFeatureRequest[] requests, string phaseLabel, HashSet<string> plannedPrefabs)
        {
            var plannedLines = CollectPlannedPrefabLines(requests, plannedPrefabs);
            return AppendPlannedItemsToIndex(currentIndex, "Prefab Index", "Existing Prefabs", "Planned Prefabs", phaseLabel, plannedLines);
        }

        private static string AppendPlannedScenesToIndex(string currentIndex, ProtoFeatureRequest[] requests, string phaseLabel, HashSet<string> plannedScenes)
        {
            var plannedLines = CollectPlannedSceneLines(requests, plannedScenes);
            return AppendPlannedItemsToIndex(currentIndex, "Scene Index", "Existing Scenes", "Planned Scenes", phaseLabel, plannedLines);
        }

        private static string AppendPlannedAssetsToIndex(string currentIndex, ProtoFeatureRequest[] requests, string phaseLabel, HashSet<string> plannedAssets)
        {
            var plannedLines = CollectPlannedAssetLines(requests, plannedAssets);
            return AppendPlannedItemsToIndex(currentIndex, "Asset Index", "Existing Assets", "Planned Assets", phaseLabel, plannedLines);
        }

        private static string AppendPlannedItemsToIndex(string currentIndex, string title, string existingSection, string plannedSection, string phaseLabel, List<string> plannedLines)
        {
            if (plannedLines == null || plannedLines.Count == 0)
            {
                return currentIndex ?? string.Empty;
            }

            var existing = currentIndex ?? string.Empty;
            var builder = new StringBuilder(2048);
            if (string.IsNullOrWhiteSpace(existing))
            {
                builder.AppendLine($"# {title}");
                builder.AppendLine();
                builder.AppendLine($"Generated: {DateTime.UtcNow:o}");
                builder.AppendLine();
                builder.AppendLine($"## {existingSection}");
                builder.AppendLine("(No entries found)");
                builder.AppendLine();
            }
            else
            {
                builder.Append(existing.TrimEnd());
                builder.AppendLine();
            }

            if (existing.IndexOf($"## {plannedSection}", StringComparison.OrdinalIgnoreCase) < 0)
            {
                builder.AppendLine($"## {plannedSection}");
            }

            if (!string.IsNullOrWhiteSpace(phaseLabel))
            {
                builder.AppendLine($"### {phaseLabel}");
            }

            foreach (var line in plannedLines)
            {
                builder.AppendLine($"- {line}");
            }

            var content = builder.ToString().TrimEnd() + "\n";
            return ClampPlanIndex(content);
        }

        private static List<string> CollectPlannedPrefabLines(ProtoFeatureRequest[] requests, HashSet<string> plannedPrefabs)
        {
            var results = new List<string>();
            if (requests == null || plannedPrefabs == null)
            {
                return results;
            }

            foreach (var request in requests)
            {
                if (request == null)
                {
                    continue;
                }

                if (!string.Equals(request.type, "prefab", StringComparison.OrdinalIgnoreCase) &&
                    !(string.Equals(request.type, "asset", StringComparison.OrdinalIgnoreCase) && LooksLikePrefabRequest(request)))
                {
                    continue;
                }

                var name = GetPrefabName(request);
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var path = BuildPlannedPrefabPath(request.path, name);
                var key = string.IsNullOrWhiteSpace(path) ? name : path;
                if (!plannedPrefabs.Add(key))
                {
                    continue;
                }

                results.Add(string.IsNullOrWhiteSpace(path) ? name : $"{name} ({path})");
            }

            return results;
        }

        private static List<string> CollectPlannedSceneLines(ProtoFeatureRequest[] requests, HashSet<string> plannedScenes)
        {
            var results = new List<string>();
            if (requests == null || plannedScenes == null)
            {
                return results;
            }

            foreach (var request in requests)
            {
                if (request == null)
                {
                    continue;
                }

                if (!string.Equals(request.type, "scene", StringComparison.OrdinalIgnoreCase) &&
                    !(string.Equals(request.type, "asset", StringComparison.OrdinalIgnoreCase) && LooksLikeSceneRequest(request)))
                {
                    continue;
                }

                var name = GetSceneName(request);
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var path = BuildPlannedScenePath(request.path, name);
                var key = string.IsNullOrWhiteSpace(path) ? name : path;
                if (!plannedScenes.Add(key))
                {
                    continue;
                }

                results.Add(string.IsNullOrWhiteSpace(path) ? name : $"{name} ({path})");
            }

            return results;
        }

        private static List<string> CollectPlannedAssetLines(ProtoFeatureRequest[] requests, HashSet<string> plannedAssets)
        {
            var results = new List<string>();
            if (requests == null || plannedAssets == null)
            {
                return results;
            }

            foreach (var request in requests)
            {
                if (request == null)
                {
                    continue;
                }

                var isMaterial = string.Equals(request.type, "material", StringComparison.OrdinalIgnoreCase);
                if (!isMaterial && !string.Equals(request.type, "asset", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var name = string.IsNullOrWhiteSpace(request.name) ? ExtractNameFromPath(request.path) : request.name.Trim();
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var path = isMaterial
                    ? BuildPlannedMaterialPath(request.path, name)
                    : BuildPlannedAssetPath(request.path, name);
                var key = string.IsNullOrWhiteSpace(path) ? name : path;
                if (!plannedAssets.Add(key))
                {
                    continue;
                }

                var typeLabel = isMaterial ? "material" : "asset";
                var line = string.IsNullOrWhiteSpace(path) ? $"{name} [{typeLabel}]" : $"{name} [{typeLabel}] ({path})";
                results.Add(line);
            }

            return results;
        }

        private static string BuildPlannedPrefabPath(string path, string name)
        {
            var prefabName = string.IsNullOrWhiteSpace(name) ? "Prefab" : name.Trim();
            var normalized = EnsureProjectPath(string.IsNullOrWhiteSpace(path) ? $"{ProjectRoot}/Prefabs" : path);

            if (normalized.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                return normalized;
            }

            if (normalized.EndsWith("/", StringComparison.Ordinal))
            {
                normalized = normalized.TrimEnd('/');
            }

            if (Path.HasExtension(normalized))
            {
                var folder = Path.GetDirectoryName(normalized)?.Replace('\\', '/') ?? ProjectRoot;
                return $"{folder}/{prefabName}.prefab";
            }

            return $"{normalized}/{prefabName}.prefab";
        }

        private static string BuildPlannedScenePath(string path, string name)
        {
            var sceneName = string.IsNullOrWhiteSpace(name) ? "NewScene" : name.Trim();
            var normalized = EnsureProjectPath(string.IsNullOrWhiteSpace(path) ? $"{ProjectRoot}/Scenes" : path);

            if (normalized.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
            {
                return normalized;
            }

            if (normalized.EndsWith("/", StringComparison.Ordinal))
            {
                normalized = normalized.TrimEnd('/');
            }

            if (Path.HasExtension(normalized))
            {
                var folder = Path.GetDirectoryName(normalized)?.Replace('\\', '/') ?? ProjectRoot;
                return $"{folder}/{sceneName}.unity";
            }

            return $"{normalized}/{sceneName}.unity";
        }

        private static string BuildPlannedMaterialPath(string path, string name)
        {
            var materialName = string.IsNullOrWhiteSpace(name) ? "NewMaterial" : name.Trim();
            var normalized = EnsureProjectPath(string.IsNullOrWhiteSpace(path) ? $"{ProjectRoot}/Materials" : path);

            if (normalized.EndsWith(".mat", StringComparison.OrdinalIgnoreCase))
            {
                return normalized;
            }

            if (normalized.EndsWith("/", StringComparison.Ordinal))
            {
                normalized = normalized.TrimEnd('/');
            }

            if (Path.HasExtension(normalized))
            {
                var folder = Path.GetDirectoryName(normalized)?.Replace('\\', '/') ?? ProjectRoot;
                return $"{folder}/{materialName}.mat";
            }

            return $"{normalized}/{materialName}.mat";
        }

        private static string BuildPlannedAssetPath(string path, string name)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            var normalized = EnsureProjectPath(path);
            if (normalized.EndsWith("/", StringComparison.Ordinal))
            {
                normalized = normalized.TrimEnd('/');
            }

            if (Path.HasExtension(normalized))
            {
                return normalized;
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                return normalized;
            }

            return $"{normalized}/{name.Trim()}";
        }

        private static string ExtractNameFromPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            var normalized = path.Replace('\\', '/').TrimEnd('/');
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return string.Empty;
            }

            var fileName = Path.GetFileNameWithoutExtension(normalized);
            return string.IsNullOrWhiteSpace(fileName) ? string.Empty : fileName.Trim();
        }

        private static void AppendScriptIndexMessage(List<ProtoChatMessage> messages, string scriptIndex)
        {
            if (messages == null || string.IsNullOrWhiteSpace(scriptIndex))
            {
                return;
            }

            messages.Add(new ProtoChatMessage
            {
                role = "system",
                content = $"Script Index:\n{scriptIndex}"
            });
        }

        private static void AppendPrefabIndexMessage(List<ProtoChatMessage> messages, string prefabIndex)
        {
            AppendIndexMessage(messages, "Prefab Index", prefabIndex);
        }

        private static void AppendSceneIndexMessage(List<ProtoChatMessage> messages, string sceneIndex)
        {
            AppendIndexMessage(messages, "Scene Index", sceneIndex);
        }

        private static void AppendAssetIndexMessage(List<ProtoChatMessage> messages, string assetIndex)
        {
            AppendIndexMessage(messages, "Asset Index", assetIndex);
        }

        private static void AppendIndexMessage(List<ProtoChatMessage> messages, string title, string content)
        {
            if (messages == null || string.IsNullOrWhiteSpace(content))
            {
                return;
            }

            messages.Add(new ProtoChatMessage
            {
                role = "system",
                content = $"{title}:\n{content}"
            });
        }

        private static void AppendPlanIndexMessages(List<ProtoChatMessage> messages, string scriptIndex, string prefabIndex, string sceneIndex, string assetIndex)
        {
            AppendScriptIndexMessage(messages, ClampScriptIndex(scriptIndex));
            AppendPrefabIndexMessage(messages, ClampPlanIndex(prefabIndex));
            AppendSceneIndexMessage(messages, ClampPlanIndex(sceneIndex));
            AppendAssetIndexMessage(messages, ClampPlanIndex(assetIndex));
        }

        private static string BuildDependencySummary(ProtoFeatureRequest request, Dictionary<string, ProtoFeatureRequest> requestLookup, ScriptContractIndex index)
        {
            if (request == null || request.dependsOn == null || request.dependsOn.Length == 0)
            {
                return string.Empty;
            }

            var dependencyEntries = GetDependencyEntries(request, requestLookup, index);
            if (dependencyEntries.Count == 0)
            {
                return string.Empty;
            }

            var builder = new StringBuilder(1024);
            foreach (var entry in dependencyEntries)
            {
                builder.AppendLine($"- {entry.DisplayName} ({entry.path})");
                var memberCount = 0;
                foreach (var member in entry.members)
                {
                    if (memberCount >= MaxMembersPerScript)
                    {
                        break;
                    }
                    builder.AppendLine($"  - {member}");
                    memberCount++;
                }
            }

            return builder.ToString().Trim();
        }

        private static List<ScriptContractEntry> GetDependencyEntries(ProtoFeatureRequest request, Dictionary<string, ProtoFeatureRequest> requestLookup, ScriptContractIndex index)
        {
            var results = new List<ScriptContractEntry>();
            if (request == null || request.dependsOn == null || requestLookup == null)
            {
                return results;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var depId in request.dependsOn)
            {
                if (string.IsNullOrWhiteSpace(depId))
                {
                    continue;
                }

                if (!requestLookup.TryGetValue(depId, out var depRequest))
                {
                    continue;
                }

                if (!string.Equals(depRequest.type, "script", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var depName = depRequest.name?.Trim();
                if (string.IsNullOrWhiteSpace(depName) || !seen.Add(depName))
                {
                    continue;
                }

                if (index != null && index.byName.TryGetValue(depName, out var entry))
                {
                    results.Add(entry);
                    continue;
                }

                var expectedPath = $"{NormalizeFolderPath(depRequest.path)}/{depName}.cs";
                var fullPath = Path.GetFullPath(expectedPath);
                if (!File.Exists(fullPath))
                {
                    continue;
                }

                var parsed = ParseScriptFile(fullPath, expectedPath);
                if (parsed.Count > 0)
                {
                    results.AddRange(parsed);
                }
            }

            return results;
        }

        private static string ToAssetPath(string fullPath, string assetsPath)
        {
            if (string.IsNullOrWhiteSpace(fullPath) || string.IsNullOrWhiteSpace(assetsPath))
            {
                return string.Empty;
            }

            var normalizedFull = fullPath.Replace('\\', '/');
            var normalizedAssets = assetsPath.Replace('\\', '/');
            if (!normalizedFull.StartsWith(normalizedAssets, StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            var relative = normalizedFull.Substring(normalizedAssets.Length).TrimStart('/');
            return string.IsNullOrWhiteSpace(relative) ? "Assets" : $"Assets/{relative}";
        }

        private static List<ScriptContractEntry> ParseScriptFile(string fullPath, string assetPath)
        {
            var results = new List<ScriptContractEntry>();
            if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath))
            {
                return results;
            }

            var currentNamespace = string.Empty;
            ScriptContractEntry currentEntry = null;

            foreach (var rawLine in File.ReadAllLines(fullPath))
            {
                var line = rawLine;
                var commentIndex = line.IndexOf("//", StringComparison.Ordinal);
                if (commentIndex >= 0)
                {
                    line = line.Substring(0, commentIndex);
                }

                var trimmed = line.Trim();
                if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("[", StringComparison.Ordinal))
                {
                    continue;
                }

                var namespaceMatch = NamespaceRegex.Match(trimmed);
                if (namespaceMatch.Success)
                {
                    currentNamespace = namespaceMatch.Groups[1].Value.Trim();
                    continue;
                }

                var typeMatch = TypeRegex.Match(trimmed);
                if (typeMatch.Success)
                {
                    var typeName = typeMatch.Groups[3].Value.Trim();
                    var entry = new ScriptContractEntry
                    {
                        name = typeName,
                        fullName = string.IsNullOrWhiteSpace(currentNamespace) ? typeName : $"{currentNamespace}.{typeName}",
                        path = assetPath
                    };
                    results.Add(entry);
                    currentEntry = entry;
                    continue;
                }

                if (currentEntry == null)
                {
                    continue;
                }

                TryAddMember(currentEntry, trimmed);
            }

            return results;
        }

        private static void TryAddMember(ScriptContractEntry entry, string line)
        {
            if (entry == null || string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            if (line.Contains(" class ", StringComparison.Ordinal) ||
                line.Contains(" struct ", StringComparison.Ordinal) ||
                line.Contains(" interface ", StringComparison.Ordinal) ||
                line.Contains(" enum ", StringComparison.Ordinal))
            {
                return;
            }

            var methodMatch = MethodRegex.Match(line);
            if (methodMatch.Success)
            {
                var returnType = methodMatch.Groups[1].Value.Trim();
                if (returnType.Contains("class", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                var name = methodMatch.Groups[2].Value.Trim();
                var parameters = methodMatch.Groups[3].Value.Trim();
                var signature = $"public {returnType} {name}({parameters})";
                AddMember(entry, signature);
                return;
            }

            var propertyMatch = PropertyRegex.Match(line);
            if (propertyMatch.Success)
            {
                var type = propertyMatch.Groups[1].Value.Trim();
                var name = propertyMatch.Groups[2].Value.Trim();
                var signature = $"public {type} {name}";
                AddMember(entry, signature);
                return;
            }

            var fieldMatch = FieldRegex.Match(line);
            if (fieldMatch.Success)
            {
                var type = fieldMatch.Groups[1].Value.Trim();
                var name = fieldMatch.Groups[2].Value.Trim();
                var signature = $"public {type} {name}";
                AddMember(entry, signature);
            }
        }

        private static void AddMember(ScriptContractEntry entry, string signature)
        {
            if (entry == null || string.IsNullOrWhiteSpace(signature))
            {
                return;
            }

            if (!entry.members.Contains(signature))
            {
                entry.members.Add(signature);
            }
        }

        private sealed class ScriptContractIndex
        {
            public readonly List<ScriptContractEntry> entries = new List<ScriptContractEntry>();
            public readonly Dictionary<string, ScriptContractEntry> byName = new Dictionary<string, ScriptContractEntry>(StringComparer.OrdinalIgnoreCase);
            public int totalCount;
        }

        private sealed class ScriptContractEntry
        {
            public string name;
            public string fullName;
            public string path;
            public readonly List<string> members = new List<string>();

            public string DisplayName => string.IsNullOrWhiteSpace(fullName) ? name : fullName;
        }

        private struct ScriptResolveResult
        {
            public bool success;
            public string assetPath;
            public string error;
        }

        private CancellationToken BeginOperation(string label)
        {
            _operationCts?.Cancel();
            _operationCts?.Dispose();
            _operationCts = new CancellationTokenSource();
            _cancelRequested = false;
            if (!string.IsNullOrWhiteSpace(label))
            {
                _toolStatus = label;
            }
            return _operationCts.Token;
        }

        private void EndOperation()
        {
            if (_operationCts != null)
            {
                _operationCts.Dispose();
                _operationCts = null;
            }
            _cancelRequested = false;
        }

        private void RequestCancel()
        {
            _cancelRequested = true;
            _toolStatus = "Canceling...";
            if (_operationCts != null && !_operationCts.IsCancellationRequested)
            {
                _operationCts.Cancel();
            }
            EndApiWait();
            Repaint();
        }

        private bool IsOperationActive()
        {
            return _isSending || _isGeneratingPlan || _isCreatingRequests || _isApplyingPlan || _isApplyingStage;
        }

        private void BeginAutoRefreshBlock()
        {
            if (_isAutoRefreshDeferred)
            {
                return;
            }

            _isAutoRefreshDeferred = true;
            AssetDatabase.DisallowAutoRefresh();
            EditorApplication.LockReloadAssemblies();
        }

        private void EndAutoRefreshBlock(bool refresh)
        {
            if (!_isAutoRefreshDeferred)
            {
                return;
            }

            _isAutoRefreshDeferred = false;
            AssetDatabase.AllowAutoRefresh();
            if (refresh)
            {
                AssetDatabase.Refresh();
            }
            EditorApplication.UnlockReloadAssemblies();
        }

        internal static void ApplySingleRequestFromTracker(ProtoFeatureRequest request)
        {
            if (request == null)
            {
                return;
            }

            var window = CreateInstance<ProtoChatWindow>();
            window.hideFlags = HideFlags.HideAndDontSave;
            _ = window.ApplySingleRequestInternalAsync(request);
        }

        private async Task ApplySingleRequestInternalAsync(ProtoFeatureRequest request)
        {
            _cancelRequested = false;
            var token = BeginOperation("re-do");
            try
            {
                var settings = await RunOnMainThread(GetSettingsSnapshot).ConfigureAwait(false);

                var list = new List<ProtoFeatureRequest> { request };
                list = await RunOnMainThread(() => PreflightRequestsForExecution(list)).ConfigureAwait(false);
                var lookup = BuildRequestLookup(list);

                await RunOnMainThread(BeginAutoRefreshBlock).ConfigureAwait(false);
                try
                {
                    await ExecuteFeatureRequestsAsync(list, lookup, token).ConfigureAwait(false);
                    if (!token.IsCancellationRequested)
                    {
                        await RetryBlockedRequestsAsync(list, lookup, token).ConfigureAwait(false);
                    }
                }
                finally
                {
                    await RunOnMainThread(() => EndAutoRefreshBlock(true)).ConfigureAwait(false);
                }

                if (string.Equals(request.type, "prefab", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(request.type, "asset", StringComparison.OrdinalIgnoreCase))
                {
                    var allRequests = await RunOnMainThread(LoadFeatureRequestsFromDisk).ConfigureAwait(false);
                    var allLookup = BuildRequestLookup(allRequests);
                    await RunOnMainThread(() => SchedulePrefabComponentAttachment(list, allLookup)).ConfigureAwait(false);
                }
            }
            finally
            {
                EndOperation();
                EditorApplication.delayCall += () => DestroyImmediate(this);
            }
        }

        private void BeginApiWait(string label)
        {
            _apiWaitLabel = string.IsNullOrWhiteSpace(label) ? "request" : label;
            _apiWaitStarted = EditorApplication.timeSinceStartup;
            _isApiWaiting = true;
            if (!_apiWaitHooked)
            {
                EditorApplication.update += OnApiWaitUpdate;
                _apiWaitHooked = true;
            }
        }

        private void EndApiWait()
        {
            _isApiWaiting = false;
            _apiWaitLabel = string.Empty;
            if (_apiWaitHooked)
            {
                EditorApplication.update -= OnApiWaitUpdate;
                _apiWaitHooked = false;
            }
        }

        private void OnApiWaitUpdate()
        {
            if (!_isApiWaiting)
            {
                if (_apiWaitHooked)
                {
                    EditorApplication.update -= OnApiWaitUpdate;
                    _apiWaitHooked = false;
                }
                return;
            }

            Repaint();
        }

        private static Task RunAssetEditingAsync(Action action, bool refresh)
        {
            return RunOnMainThread(() =>
            {
                try
                {
                    EditorApplication.LockReloadAssemblies();
                    AssetDatabase.StartAssetEditing();
                    action();
                }
                finally
                {
                    AssetDatabase.StopAssetEditing();
                    if (refresh)
                    {
                        AssetDatabase.Refresh();
                    }
                    EditorApplication.UnlockReloadAssemblies();
                }
            });
        }

        private static Task<T> RunOnMainThread<T>(Func<T> action)
        {
            var tcs = new TaskCompletionSource<T>();
            EditorApplication.delayCall += () =>
            {
                try
                {
                    var result = action();
                    tcs.SetResult(result);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            };
            return tcs.Task;
        }

        private static (string token, string model) GetSettingsSnapshot()
        {
            return (ProtoOpenAISettings.GetToken(), ProtoOpenAISettings.GetModel());
        }

        private static ProtoGenerationPlan ParsePlan(string json)
        {
            try
            {
                var normalized = NormalizePlanJson(json);
                return string.IsNullOrWhiteSpace(normalized)
                    ? null
                    : JsonUtility.FromJson<ProtoGenerationPlan>(normalized);
            }
            catch
            {
                return null;
            }
        }

        private static string ExtractCode(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            var trimmed = text.Trim();
            var fence = "```";
            var start = trimmed.IndexOf(fence, StringComparison.Ordinal);
            if (start < 0)
            {
                return trimmed;
            }

            var end = trimmed.IndexOf(fence, start + fence.Length, StringComparison.Ordinal);
            if (end < 0)
            {
                return trimmed;
            }

            var block = trimmed.Substring(start + fence.Length, end - (start + fence.Length));
            if (block.StartsWith("csharp", StringComparison.OrdinalIgnoreCase) ||
                block.StartsWith("cs", StringComparison.OrdinalIgnoreCase))
            {
                var newline = block.IndexOf('\n');
                if (newline >= 0)
                {
                    block = block.Substring(newline + 1);
                }
            }

            return block.Trim();
        }

        private static string ExtractJson(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            var trimmed = text.Trim();
            var fence = "```";
            var startFence = trimmed.IndexOf(fence, StringComparison.Ordinal);
            if (startFence >= 0)
            {
                var endFence = trimmed.IndexOf(fence, startFence + fence.Length, StringComparison.Ordinal);
                if (endFence > startFence)
                {
                    var fenced = trimmed.Substring(startFence + fence.Length, endFence - (startFence + fence.Length));
                    var newline = fenced.IndexOf('\n');
                    if (newline >= 0)
                    {
                        fenced = fenced.Substring(newline + 1);
                    }
                    trimmed = fenced.Trim();
                }
            }

            var start = trimmed.IndexOf('{');
            var end = trimmed.LastIndexOf('}');
            if (start >= 0 && end > start)
            {
                return trimmed.Substring(start, end - start + 1).Trim();
            }

            return string.Empty;
        }

        private static string NormalizePlanJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return string.Empty;
            }

            var trimmed = json.Trim();
            if (trimmed.StartsWith("[", StringComparison.Ordinal))
            {
                trimmed = "{\"featureRequests\":" + trimmed + "}";
            }

            trimmed = System.Text.RegularExpressions.Regex.Replace(trimmed, @",\s*([}\]])", "$1");
            return trimmed;
        }

        private async Task<ProtoPhasePlan> GeneratePhasedPlanAsync(string token, string model, ProtoChatMessage[] baseMessages, string prompt, CancellationToken cancellationToken, string scriptIndex, string prefabIndex, string sceneIndex, string assetIndex)
        {
            if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(model))
            {
                return null;
            }

            var currentScriptIndex = string.IsNullOrWhiteSpace(scriptIndex) ? ReadScriptIndexMarkdown() : scriptIndex;
            var currentPrefabIndex = string.IsNullOrWhiteSpace(prefabIndex) ? ReadPrefabIndexMarkdown() : prefabIndex;
            var currentSceneIndex = string.IsNullOrWhiteSpace(sceneIndex) ? ReadSceneIndexMarkdown() : sceneIndex;
            var currentAssetIndex = string.IsNullOrWhiteSpace(assetIndex) ? ReadAssetIndexMarkdown() : assetIndex;
            var plannedScripts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var plannedPrefabs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var plannedScenes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var plannedAssets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var phaseOutline = await RequestPhaseOutlineAsync(token, model, baseMessages, prompt, cancellationToken, currentScriptIndex, currentPrefabIndex, currentSceneIndex, currentAssetIndex).ConfigureAwait(false);
            if (phaseOutline == null || phaseOutline.phases == null || phaseOutline.phases.Length == 0)
            {
                return null;
            }

            var phasePlan = new ProtoPhasePlan
            {
                phases = new ProtoPlanPhase[phaseOutline.phases.Length]
            };

            for (var i = 0; i < phaseOutline.phases.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var outline = phaseOutline.phases[i];
                if (outline == null)
                {
                    continue;
                }

                var overview = await RequestPhaseOverviewAsync(token, model, baseMessages, prompt, outline, cancellationToken, currentScriptIndex, currentPrefabIndex, currentSceneIndex, currentAssetIndex).ConfigureAwait(false);
                var requests = await RequestPhaseFeatureRequestsAsync(token, model, baseMessages, prompt, outline, cancellationToken, currentScriptIndex, currentPrefabIndex, currentSceneIndex, currentAssetIndex).ConfigureAwait(false);
                NormalizePlanRequests(requests);

                phasePlan.phases[i] = new ProtoPlanPhase
                {
                    id = outline.id,
                    name = outline.name,
                    goal = outline.goal,
                    overview = overview,
                    featureRequests = requests
                };

                var phaseLabel = string.IsNullOrWhiteSpace(outline.name) ? $"Phase {i + 1}" : $"Phase {i + 1}: {outline.name}";
                var updatedIndex = AppendPlannedScriptsToIndex(currentScriptIndex, requests, phaseLabel, plannedScripts);
                if (!string.Equals(updatedIndex, currentScriptIndex, StringComparison.Ordinal))
                {
                    currentScriptIndex = updatedIndex;
                    await RunOnMainThread(() => WriteScriptIndexMarkdownContent(currentScriptIndex)).ConfigureAwait(false);
                }

                var updatedPrefabIndex = AppendPlannedPrefabsToIndex(currentPrefabIndex, requests, phaseLabel, plannedPrefabs);
                if (!string.Equals(updatedPrefabIndex, currentPrefabIndex, StringComparison.Ordinal))
                {
                    currentPrefabIndex = updatedPrefabIndex;
                    await RunOnMainThread(() => WritePrefabIndexMarkdownContent(currentPrefabIndex)).ConfigureAwait(false);
                }

                var updatedSceneIndex = AppendPlannedScenesToIndex(currentSceneIndex, requests, phaseLabel, plannedScenes);
                if (!string.Equals(updatedSceneIndex, currentSceneIndex, StringComparison.Ordinal))
                {
                    currentSceneIndex = updatedSceneIndex;
                    await RunOnMainThread(() => WriteSceneIndexMarkdownContent(currentSceneIndex)).ConfigureAwait(false);
                }

                var updatedAssetIndex = AppendPlannedAssetsToIndex(currentAssetIndex, requests, phaseLabel, plannedAssets);
                if (!string.Equals(updatedAssetIndex, currentAssetIndex, StringComparison.Ordinal))
                {
                    currentAssetIndex = updatedAssetIndex;
                    await RunOnMainThread(() => WriteAssetIndexMarkdownContent(currentAssetIndex)).ConfigureAwait(false);
                }
            }

            return phasePlan;
        }

        private async Task<ProtoPhaseOutlinePlan> RequestPhaseOutlineAsync(string token, string model, ProtoChatMessage[] baseMessages, string prompt, CancellationToken cancellationToken, string scriptIndex, string prefabIndex, string sceneIndex, string assetIndex)
        {
            var messages = new List<ProtoChatMessage>(baseMessages ?? Array.Empty<ProtoChatMessage>())
            {
                new ProtoChatMessage
                {
                    role = "user",
                    content = "Decide how many phases are needed to reach a 100% functional prototype. Return ONLY JSON: {\"phases\":[{\"id\":\"phase_1\",\"name\":\"Short Name\",\"goal\":\"One sentence goal\"}]}. Keep 2-5 phases."
                },
                new ProtoChatMessage
                {
                    role = "user",
                    content = $"Project intent: {prompt}"
                }
            };
            AppendPlanIndexMessages(messages, scriptIndex, prefabIndex, sceneIndex, assetIndex);

            await RunOnMainThread(() => BeginApiWait("phase count")).ConfigureAwait(false);
            var response = await ProtoOpenAIClient.SendChatAsync(token, model, messages.ToArray(), _apiWaitTimeoutSeconds, cancellationToken).ConfigureAwait(false);
            await RunOnMainThread(EndApiWait).ConfigureAwait(false);

            var json = ExtractJson(response);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            try
            {
                return JsonUtility.FromJson<ProtoPhaseOutlinePlan>(json);
            }
            catch
            {
                return null;
            }
        }

        private async Task<string> RequestPhaseOverviewAsync(string token, string model, ProtoChatMessage[] baseMessages, string prompt, ProtoPhaseOutline outline, CancellationToken cancellationToken, string scriptIndex, string prefabIndex, string sceneIndex, string assetIndex)
        {
            var messages = new List<ProtoChatMessage>(baseMessages ?? Array.Empty<ProtoChatMessage>())
            {
                new ProtoChatMessage
                {
                    role = "user",
                    content = "Return ONLY JSON: {\"overview\":\"2-4 sentences describing what will be delivered in this phase.\"}"
                },
                new ProtoChatMessage
                {
                    role = "user",
                    content = $"Project intent: {prompt}\nPhase: {outline.id} {outline.name}\nGoal: {outline.goal}"
                }
            };
            AppendPlanIndexMessages(messages, scriptIndex, prefabIndex, sceneIndex, assetIndex);

            await RunOnMainThread(() => BeginApiWait($"phase {outline.id} overview")).ConfigureAwait(false);
            var response = await ProtoOpenAIClient.SendChatAsync(token, model, messages.ToArray(), _apiWaitTimeoutSeconds, cancellationToken).ConfigureAwait(false);
            await RunOnMainThread(EndApiWait).ConfigureAwait(false);

            var json = ExtractJson(response);
            if (string.IsNullOrWhiteSpace(json))
            {
                return string.Empty;
            }

            try
            {
                var parsed = JsonUtility.FromJson<ProtoPhaseOverview>(json);
                return parsed?.overview ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private async Task<ProtoFeatureRequest[]> RequestPhaseFeatureRequestsAsync(string token, string model, ProtoChatMessage[] baseMessages, string prompt, ProtoPhaseOutline outline, CancellationToken cancellationToken, string scriptIndex, string prefabIndex, string sceneIndex, string assetIndex)
        {
            var messages = new List<ProtoChatMessage>(baseMessages ?? Array.Empty<ProtoChatMessage>())
            {
                new ProtoChatMessage
                {
                    role = "user",
                    content = "Return ONLY a JSON object with the schema: {\"featureRequests\":[{\"id\":\"optional\",\"type\":\"folder|script|scene|prefab|material|asset\",\"name\":\"DisplayName\",\"path\":\"Assets/...\",\"dependsOn\":[\"id1\",\"id2\"],\"notes\":\"optional\"}]}. All paths must live under Assets/Project. For scripts, path must be the folder (not the .cs file) and name must be a valid C# identifier (letters/numbers/underscore, start with letter or underscore, no spaces). For prefabs, path can be a folder or a .prefab path; notes should mention cube/box, sphere, capsule, cylinder, plane, quad, character controller, or empty. For scenes, path should be a .unity asset path or a folder; include notes like \"prefabs: A,B\" and \"managers: X,Y\" (and optionally \"ui\" or \"spawn\") and add dependsOn to those prefabs/scripts. For materials, path can be a folder or a .mat path; notes can mention URP/HDRP/Standard. Avoid names already present in the Script/Prefab/Scene/Asset indexes."
                },
                new ProtoChatMessage
                {
                    role = "user",
                    content = $"Project intent: {prompt}\nPhase: {outline.id} {outline.name}\nGoal: {outline.goal}\nOnly include feature requests needed for this phase."
                }
            };
            AppendPlanIndexMessages(messages, scriptIndex, prefabIndex, sceneIndex, assetIndex);

            await RunOnMainThread(() => BeginApiWait($"phase {outline.id} requests")).ConfigureAwait(false);
            var response = await ProtoOpenAIClient.SendChatAsync(token, model, messages.ToArray(), _apiWaitTimeoutSeconds, cancellationToken).ConfigureAwait(false);
            await RunOnMainThread(EndApiWait).ConfigureAwait(false);

            var json = ExtractJson(response);
            if (string.IsNullOrWhiteSpace(json))
            {
                return Array.Empty<ProtoFeatureRequest>();
            }

            var plan = ParsePlan(json);
            return plan?.featureRequests ?? Array.Empty<ProtoFeatureRequest>();
        }

        private static string[] BuildPhaseLabels(ProtoPhasePlan phasePlan)
        {
            if (phasePlan == null || phasePlan.phases == null || phasePlan.phases.Length == 0)
            {
                return Array.Empty<string>();
            }

            var labels = new string[phasePlan.phases.Length + 1];
            labels[0] = "All phases";
            for (var i = 0; i < phasePlan.phases.Length; i++)
            {
                var phase = phasePlan.phases[i];
                var name = string.IsNullOrWhiteSpace(phase?.name) ? $"Phase {i + 1}" : $"Phase {i + 1}: {phase.name}";
                labels[i + 1] = name;
            }

            return labels;
        }

        private static List<ProtoFeatureRequest> CollectPhaseRequests(ProtoPhasePlan phasePlan, int selectedPhaseIndex)
        {
            var results = new List<ProtoFeatureRequest>();
            if (phasePlan == null || phasePlan.phases == null || phasePlan.phases.Length == 0)
            {
                return results;
            }

            if (selectedPhaseIndex <= 0)
            {
                foreach (var phase in phasePlan.phases)
                {
                    if (phase?.featureRequests == null)
                    {
                        continue;
                    }
                    results.AddRange(phase.featureRequests);
                }
                return results;
            }

            var index = selectedPhaseIndex - 1;
            if (index < 0 || index >= phasePlan.phases.Length)
            {
                return results;
            }

            var selected = phasePlan.phases[index];
            if (selected?.featureRequests != null)
            {
                results.AddRange(selected.featureRequests);
            }

            return results;
        }

        private static List<ProtoFeatureRequest> BuildFeatureRequestList(ProtoFeatureRequest[] requests)
        {
            var results = new List<ProtoFeatureRequest>();
            if (requests == null)
            {
                return results;
            }

            var folderMap = new Dictionary<string, ProtoFeatureRequest>(StringComparer.OrdinalIgnoreCase);
            var pendingFolders = new List<ProtoFeatureRequest>();
            var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var request in requests)
            {
                if (request == null)
                {
                    continue;
                }

                request.type = (request.type ?? string.Empty).Trim();
                NormalizeScriptRequestName(request, true);
                request.id = EnsureRequestId(request, usedIds);

                request.path = NormalizePathForType(request.type, request.path);
                request.status = "todo";
                request.createdAt = DateTime.UtcNow.ToString("o");
                request.updatedAt = request.createdAt;

                results.Add(request);

                if (string.Equals(request.type, "folder", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(request.path))
                {
                    folderMap[request.path.Trim()] = request;
                }
            }

            foreach (var request in results)
            {
                if (!string.Equals(request.type, "script", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var folderPath = NormalizeFolderPath(request.path);
                if (string.IsNullOrWhiteSpace(folderPath))
                {
                    continue;
                }

                if (!folderMap.TryGetValue(folderPath, out var folderRequest))
                {
                    folderRequest = new ProtoFeatureRequest
                    {
                        id = EnsureRequestId(new ProtoFeatureRequest
                        {
                            type = "folder",
                            name = Path.GetFileName(folderPath),
                            path = folderPath
                        }, usedIds),
                        type = "folder",
                        name = Path.GetFileName(folderPath),
                        path = folderPath,
                        status = "todo",
                        createdAt = DateTime.UtcNow.ToString("o"),
                        updatedAt = DateTime.UtcNow.ToString("o")
                    };
                    folderMap[folderPath] = folderRequest;
                    pendingFolders.Add(folderRequest);
                }

                var deps = new List<string>(request.dependsOn ?? Array.Empty<string>());
                if (!deps.Contains(folderRequest.id))
                {
                    deps.Add(folderRequest.id);
                    request.dependsOn = deps.ToArray();
                }
            }

            if (pendingFolders.Count > 0)
            {
                results.AddRange(pendingFolders);
            }

            return results;
        }

        private static List<ProtoFeatureRequest> PreflightRequestsForWrite(List<ProtoFeatureRequest> requests)
        {
            if (requests == null || requests.Count == 0)
            {
                return requests ?? new List<ProtoFeatureRequest>();
            }

            var deduped = DeduplicateRequests(requests, out var idRemap);
            ApplyDependencyRemap(deduped, idRemap);
            AddDependenciesFromNotes(deduped);
            return deduped;
        }

        private static List<ProtoFeatureRequest> PreflightRequestsForExecution(List<ProtoFeatureRequest> requests)
        {
            if (requests == null || requests.Count == 0)
            {
                return requests ?? new List<ProtoFeatureRequest>();
            }

            NormalizeFeatureRequestsForExecution(requests);

            var deduped = DeduplicateRequests(requests, out var idRemap);
            var remapped = ApplyDependencyRemap(deduped, idRemap);
            AddDependenciesFromNotes(deduped);
            foreach (var request in remapped)
            {
                if (request == null)
                {
                    continue;
                }

                request.updatedAt = DateTime.UtcNow.ToString("o");
                ProtoFeatureRequestStore.SaveRequest(request);
            }

            MarkDuplicateRequests(requests, idRemap);
            return deduped;
        }

        private static List<ProtoFeatureRequest> DeduplicateRequests(List<ProtoFeatureRequest> requests, out Dictionary<string, string> idRemap)
        {
            idRemap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var results = new List<ProtoFeatureRequest>();
            if (requests == null)
            {
                return results;
            }

            var seen = new Dictionary<string, ProtoFeatureRequest>(StringComparer.OrdinalIgnoreCase);
            foreach (var request in requests)
            {
                if (request == null)
                {
                    continue;
                }

                var key = BuildRequestIdentity(request);
                if (string.IsNullOrWhiteSpace(key))
                {
                    results.Add(request);
                    continue;
                }

                if (seen.TryGetValue(key, out var existing))
                {
                    if (!string.IsNullOrWhiteSpace(request.id) && !string.IsNullOrWhiteSpace(existing.id))
                    {
                        idRemap[request.id] = existing.id;
                    }
                    continue;
                }

                seen[key] = request;
                results.Add(request);
            }

            return results;
        }

        private static List<ProtoFeatureRequest> ApplyDependencyRemap(List<ProtoFeatureRequest> requests, Dictionary<string, string> idRemap)
        {
            var changed = new List<ProtoFeatureRequest>();
            if (requests == null || idRemap == null || idRemap.Count == 0)
            {
                return changed;
            }

            foreach (var request in requests)
            {
                if (request == null || request.dependsOn == null || request.dependsOn.Length == 0)
                {
                    continue;
                }

                var updated = new List<string>();
                var touched = false;
                foreach (var dep in request.dependsOn)
                {
                    if (string.IsNullOrWhiteSpace(dep))
                    {
                        continue;
                    }

                    var finalId = dep;
                    if (idRemap.TryGetValue(dep, out var remapped))
                    {
                        finalId = remapped;
                        touched = true;
                    }

                    if (!updated.Contains(finalId))
                    {
                        updated.Add(finalId);
                    }
                    else
                    {
                        touched = true;
                    }
                }

                if (!touched)
                {
                    continue;
                }

                request.dependsOn = updated.ToArray();
                changed.Add(request);
            }

            return changed;
        }

        private static void MarkDuplicateRequests(List<ProtoFeatureRequest> requests, Dictionary<string, string> idRemap)
        {
            if (requests == null || idRemap == null || idRemap.Count == 0)
            {
                return;
            }

            foreach (var request in requests)
            {
                if (request == null || string.IsNullOrWhiteSpace(request.id))
                {
                    continue;
                }

                if (!idRemap.TryGetValue(request.id, out var replacement))
                {
                    continue;
                }

                request.status = "done";
                request.notes = AppendNote(request.notes, $"Skipped duplicate of {replacement}.");
                request.updatedAt = DateTime.UtcNow.ToString("o");
                ProtoFeatureRequestStore.SaveRequest(request);
            }
        }

        private static void AddDependenciesFromNotes(List<ProtoFeatureRequest> requests)
        {
            if (requests == null || requests.Count == 0)
            {
                return;
            }

            var prefabByName = new Dictionary<string, ProtoFeatureRequest>(StringComparer.OrdinalIgnoreCase);
            var scriptByName = new Dictionary<string, ProtoFeatureRequest>(StringComparer.OrdinalIgnoreCase);

            foreach (var request in requests)
            {
                if (request == null)
                {
                    continue;
                }

                if (string.Equals(request.type, "prefab", StringComparison.OrdinalIgnoreCase) ||
                    (string.Equals(request.type, "asset", StringComparison.OrdinalIgnoreCase) && LooksLikePrefabRequest(request)))
                {
                    var prefabName = GetPrefabName(request);
                    if (!string.IsNullOrWhiteSpace(prefabName))
                    {
                        prefabByName[prefabName.Trim()] = request;
                    }
                    continue;
                }

                if (string.Equals(request.type, "script", StringComparison.OrdinalIgnoreCase))
                {
                    var scriptName = request.name?.Trim();
                    if (!string.IsNullOrWhiteSpace(scriptName))
                    {
                        scriptByName[scriptName] = request;
                    }
                }
            }

            foreach (var request in requests)
            {
                if (request == null || string.IsNullOrWhiteSpace(request.notes))
                {
                    continue;
                }

                foreach (var prefabName in ParseScenePrefabNamesFromNotes(request.notes))
                {
                    if (prefabByName.TryGetValue(prefabName, out var prefabRequest))
                    {
                        AddDependency(request, prefabRequest.id);
                    }
                }

                foreach (var managerName in ParseSceneManagerNamesFromNotes(request.notes))
                {
                    if (scriptByName.TryGetValue(managerName, out var scriptRequest))
                    {
                        AddDependency(request, scriptRequest.id);
                    }
                }
            }
        }

        private static void AddDependency(ProtoFeatureRequest request, string dependencyId)
        {
            if (request == null || string.IsNullOrWhiteSpace(dependencyId))
            {
                return;
            }

            var deps = new List<string>(request.dependsOn ?? Array.Empty<string>());
            if (!deps.Contains(dependencyId))
            {
                deps.Add(dependencyId);
                request.dependsOn = deps.ToArray();
            }
        }

        private static string BuildRequestIdentity(ProtoFeatureRequest request)
        {
            if (request == null)
            {
                return string.Empty;
            }

            var type = (request.type ?? string.Empty).Trim().ToLowerInvariant();
            switch (type)
            {
                case "folder":
                    return $"folder:{EnsureProjectPath(request.path)}";
                case "script":
                {
                    var name = SanitizeScriptName(request.name ?? string.Empty);
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        name = ExtractNameFromPath(request.path);
                    }
                    return string.IsNullOrWhiteSpace(name) ? string.Empty : $"script:{name}";
                }
                case "prefab":
                {
                    var name = GetPrefabName(request);
                    var path = BuildPlannedPrefabPath(request.path, name);
                    return string.IsNullOrWhiteSpace(path) ? string.Empty : $"prefab:{path}";
                }
                case "scene":
                {
                    var name = GetSceneName(request);
                    var path = BuildPlannedScenePath(request.path, name);
                    return string.IsNullOrWhiteSpace(path) ? string.Empty : $"scene:{path}";
                }
                case "material":
                {
                    var name = GetMaterialName(request);
                    var path = BuildPlannedMaterialPath(request.path, name);
                    return string.IsNullOrWhiteSpace(path) ? string.Empty : $"material:{path}";
                }
                case "asset":
                {
                    if (LooksLikePrefabRequest(request))
                    {
                        var name = GetPrefabName(request);
                        var path = BuildPlannedPrefabPath(request.path, name);
                        return string.IsNullOrWhiteSpace(path) ? string.Empty : $"prefab:{path}";
                    }

                    if (LooksLikeSceneRequest(request))
                    {
                        var name = GetSceneName(request);
                        var path = BuildPlannedScenePath(request.path, name);
                        return string.IsNullOrWhiteSpace(path) ? string.Empty : $"scene:{path}";
                    }

                    if (LooksLikeMaterialRequest(request))
                    {
                        var name = GetMaterialName(request);
                        var path = BuildPlannedMaterialPath(request.path, name);
                        return string.IsNullOrWhiteSpace(path) ? string.Empty : $"material:{path}";
                    }

                    var assetName = string.IsNullOrWhiteSpace(request.name) ? ExtractNameFromPath(request.path) : request.name.Trim();
                    var assetPath = BuildPlannedAssetPath(request.path, assetName);
                    return string.IsNullOrWhiteSpace(assetPath) ? string.Empty : $"asset:{assetPath}";
                }
                default:
                    break;
            }

            return string.Empty;
        }

        private static void NormalizePlanRequests(ProtoFeatureRequest[] requests)
        {
            if (requests == null || requests.Length == 0)
            {
                return;
            }

            foreach (var request in requests)
            {
                if (request == null)
                {
                    continue;
                }

                request.type = (request.type ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(request.path))
                {
                    request.path = EnsureProjectPath(request.path);
                }

                NormalizeScriptRequestName(request, true);
            }
        }

        private static void NormalizeFeatureRequestsForExecution(List<ProtoFeatureRequest> requests)
        {
            if (requests == null || requests.Count == 0)
            {
                return;
            }

            foreach (var request in requests)
            {
                if (request == null)
                {
                    continue;
                }

                if (!NormalizeRequestForExecution(request))
                {
                    continue;
                }

                request.updatedAt = DateTime.UtcNow.ToString("o");
                ProtoFeatureRequestStore.SaveRequest(request);
            }
        }

        private static bool NormalizeRequestForExecution(ProtoFeatureRequest request)
        {
            if (request == null)
            {
                return false;
            }

            var changed = false;
            var trimmedType = (request.type ?? string.Empty).Trim();
            if (!string.Equals(request.type, trimmedType, StringComparison.Ordinal))
            {
                request.type = trimmedType;
                changed = true;
            }

            var normalizedPath = NormalizePathForType(request.type, request.path);
            if (!string.Equals(request.path, normalizedPath, StringComparison.Ordinal))
            {
                request.path = normalizedPath;
                changed = true;
            }

            if (NormalizeScriptRequestName(request, true))
            {
                changed = true;
            }

            return changed;
        }

        private static bool NormalizeScriptRequestName(ProtoFeatureRequest request, bool addNotes)
        {
            if (request == null || !string.Equals(request.type, "script", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var original = request.name ?? string.Empty;
            var trimmed = original.Trim();
            var sanitized = SanitizeScriptName(trimmed);
            if (string.IsNullOrWhiteSpace(sanitized))
            {
                sanitized = "Script";
            }

            if (string.Equals(trimmed, sanitized, StringComparison.Ordinal))
            {
                if (!string.Equals(original, trimmed, StringComparison.Ordinal))
                {
                    request.name = sanitized;
                    return true;
                }
                return false;
            }

            request.name = sanitized;
            if (addNotes)
            {
                request.notes = AppendNote(request.notes, BuildScriptRenameNote(original, sanitized));
            }
            return true;
        }

        private static string BuildScriptRenameNote(string original, string sanitized)
        {
            if (string.IsNullOrWhiteSpace(original))
            {
                return $"Assigned script name '{sanitized}' because it was missing or invalid.";
            }

            var trimmed = original.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                return $"Assigned script name '{sanitized}' because it was missing or invalid.";
            }

            return $"Renamed script '{trimmed}' -> '{sanitized}' to be a valid C# identifier.";
        }

        private static string SanitizeScriptName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return string.Empty;
            }

            var trimmed = name.Trim();
            if (IsValidScriptIdentifier(trimmed))
            {
                return trimmed;
            }

            var builder = new StringBuilder(trimmed.Length);
            var makeUpper = false;
            foreach (var ch in trimmed)
            {
                if (char.IsLetterOrDigit(ch) || ch == '_')
                {
                    builder.Append(makeUpper ? char.ToUpperInvariant(ch) : ch);
                    makeUpper = false;
                }
                else
                {
                    makeUpper = true;
                }
            }

            var sanitized = builder.ToString();
            if (string.IsNullOrWhiteSpace(sanitized))
            {
                return string.Empty;
            }

            if (!IsValidScriptIdentifier(sanitized))
            {
                var first = sanitized[0];
                if (!char.IsLetter(first) && first != '_')
                {
                    sanitized = $"Script{sanitized}";
                }
            }

            return sanitized;
        }

        private static bool IsValidScriptIdentifier(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var first = value[0];
            if (!char.IsLetter(first) && first != '_')
            {
                return false;
            }

            for (var i = 1; i < value.Length; i++)
            {
                var ch = value[i];
                if (!char.IsLetterOrDigit(ch) && ch != '_')
                {
                    return false;
                }
            }

            return true;
        }

        private static ScriptableObjectAttemptResult TryCreateScriptableObjectAsset(ProtoFeatureRequest request, Dictionary<string, ProtoFeatureRequest> requestLookup, out string error)
        {
            error = string.Empty;
            if (request == null)
            {
                return ScriptableObjectAttemptResult.NotApplicable;
            }

            if (LooksLikePrefabRequest(request) || LooksLikeSceneRequest(request) || LooksLikeMaterialRequest(request))
            {
                return ScriptableObjectAttemptResult.NotApplicable;
            }

            var shouldTry = false;
            if (!string.IsNullOrWhiteSpace(request.path) &&
                request.path.Trim().EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
            {
                shouldTry = true;
            }

            if (!shouldTry && request.notes != null &&
                request.notes.IndexOf("scriptableobject", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                shouldTry = true;
            }

            if (!shouldTry && request.dependsOn != null && requestLookup != null)
            {
                foreach (var depId in request.dependsOn)
                {
                    if (string.IsNullOrWhiteSpace(depId))
                    {
                        continue;
                    }

                    if (requestLookup.TryGetValue(depId, out var dep) &&
                        dep != null &&
                        string.Equals(dep.type, "script", StringComparison.OrdinalIgnoreCase))
                    {
                        shouldTry = true;
                        break;
                    }
                }
            }

            if (!shouldTry)
            {
                return ScriptableObjectAttemptResult.NotApplicable;
            }

            var scriptType = ResolveScriptableObjectType(request, requestLookup);
            if (scriptType == null)
            {
                error = "ScriptableObject type not found. Ensure the referenced script compiles and inherits ScriptableObject.";
                return ScriptableObjectAttemptResult.Failed;
            }

            var assetName = string.IsNullOrWhiteSpace(request.name) ? scriptType.Name : request.name.Trim();
            var assetPath = BuildScriptableObjectPath(request.path, assetName, scriptType.Name, out var folderPath);
            if (!ProtoAssetUtility.TryEnsureFolder(folderPath, out _, out var folderError))
            {
                error = folderError;
                return ScriptableObjectAttemptResult.Failed;
            }

            if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath) != null)
            {
                error = "Asset already exists. Delete it or choose a new name.";
                return ScriptableObjectAttemptResult.Failed;
            }

            var instance = ScriptableObject.CreateInstance(scriptType);
            if (instance == null)
            {
                error = $"Failed to create ScriptableObject instance for {scriptType.Name}.";
                return ScriptableObjectAttemptResult.Failed;
            }

            AssetDatabase.CreateAsset(instance, assetPath);
            AssetDatabase.SaveAssets();
            return ScriptableObjectAttemptResult.Success;
        }

        private static Type ResolveScriptableObjectType(ProtoFeatureRequest request, Dictionary<string, ProtoFeatureRequest> requestLookup)
        {
            if (request == null || requestLookup == null || request.dependsOn == null)
            {
                return null;
            }

            foreach (var depId in request.dependsOn)
            {
                if (string.IsNullOrWhiteSpace(depId))
                {
                    continue;
                }

                if (!requestLookup.TryGetValue(depId, out var dep) || dep == null)
                {
                    continue;
                }

                if (!string.Equals(dep.type, "script", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(dep.name))
                {
                    continue;
                }

                var type = FindTypeByName(dep.name.Trim());
                if (type != null && typeof(ScriptableObject).IsAssignableFrom(type))
                {
                    return type;
                }
            }

            return null;
        }

        private static string BuildScriptableObjectPath(string path, string name, string typeName, out string folderPath)
        {
            var assetName = string.IsNullOrWhiteSpace(name) ? (typeName ?? "NewAsset") : name.Trim();
            var normalized = EnsureProjectPath(string.IsNullOrWhiteSpace(path) ? $"{ProjectRoot}/Assets" : path);

            if (normalized.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
            {
                folderPath = Path.GetDirectoryName(normalized)?.Replace('\\', '/') ?? ProjectRoot;
                return normalized;
            }

            if (normalized.EndsWith("/", StringComparison.Ordinal))
            {
                normalized = normalized.TrimEnd('/');
            }

            if (Path.HasExtension(normalized))
            {
                folderPath = Path.GetDirectoryName(normalized)?.Replace('\\', '/') ?? ProjectRoot;
                return $"{folderPath}/{assetName}.asset";
            }

            folderPath = normalized;
            return $"{normalized}/{assetName}.asset";
        }

        private static List<ProtoFeatureRequest> FilterExistingScriptRequests(List<ProtoFeatureRequest> requests)
        {
            if (requests == null || requests.Count == 0)
            {
                return requests ?? new List<ProtoFeatureRequest>();
            }

            var filtered = new List<ProtoFeatureRequest>(requests.Count);
            foreach (var request in requests)
            {
                if (request == null)
                {
                    continue;
                }

                if (!string.Equals(request.type, "script", StringComparison.OrdinalIgnoreCase))
                {
                    filtered.Add(request);
                    continue;
                }

                var folder = NormalizeFolderPath(request.path);
                var fileName = string.IsNullOrWhiteSpace(request.name) ? string.Empty : request.name.Trim();
                if (string.IsNullOrWhiteSpace(fileName))
                {
                    filtered.Add(request);
                    continue;
                }

                var assetPath = $"{folder}/{fileName}.cs";
                var fullPath = Path.GetFullPath(assetPath);
                if (File.Exists(fullPath))
                {
                    request.notes = AppendNote(request.notes, "Skipped: script already exists.");
                    continue;
                }

                filtered.Add(request);
            }

            return filtered;
        }

        private static Dictionary<string, ProtoFeatureRequest> BuildRequestLookup(List<ProtoFeatureRequest> requests)
        {
            var lookup = new Dictionary<string, ProtoFeatureRequest>(StringComparer.OrdinalIgnoreCase);
            if (requests == null)
            {
                return lookup;
            }

            foreach (var request in requests)
            {
                if (request == null || string.IsNullOrWhiteSpace(request.id))
                {
                    continue;
                }

                lookup[request.id] = request;
            }

            return lookup;
        }

        private static List<ProtoFeatureRequest> FilterRequestsByTypes(List<ProtoFeatureRequest> requests, string[] types)
        {
            var results = new List<ProtoFeatureRequest>();
            if (requests == null || types == null || types.Length == 0)
            {
                return results;
            }

            var typeSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var type in types)
            {
                if (!string.IsNullOrWhiteSpace(type))
                {
                    typeSet.Add(type.Trim());
                }
            }

            foreach (var request in requests)
            {
                if (request == null || string.IsNullOrWhiteSpace(request.type))
                {
                    continue;
                }

                if (typeSet.Contains(request.type.Trim()))
                {
                    results.Add(request);
                }
            }

            return results;
        }

        private static bool TypesContain(string[] types, string value)
        {
            if (types == null || types.Length == 0 || string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            foreach (var type in types)
            {
                if (string.Equals(type, value, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static string EnsureRequestId(ProtoFeatureRequest request, HashSet<string> usedIds)
        {
            var baseId = string.IsNullOrWhiteSpace(request?.id)
                ? BuildReadableId(request)
                : request.id.Trim();

            if (string.IsNullOrWhiteSpace(baseId))
            {
                baseId = "request";
            }

            var candidate = baseId;
            var suffix = 1;
            while (usedIds.Contains(candidate))
            {
                candidate = $"{baseId}_{suffix}";
                suffix++;
            }

            usedIds.Add(candidate);
            return candidate;
        }

        private static string BuildReadableId(ProtoFeatureRequest request)
        {
            if (request == null)
            {
                return string.Empty;
            }

            var type = string.IsNullOrWhiteSpace(request.type) ? "request" : request.type.Trim().ToLowerInvariant();
            var source = !string.IsNullOrWhiteSpace(request.name)
                ? request.name.Trim()
                : request.path?.Trim() ?? string.Empty;

            var slug = Slugify(source);
            if (string.IsNullOrWhiteSpace(slug))
            {
                return type;
            }

            return $"{type}_{slug}";
        }

        private static string Slugify(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var builder = new System.Text.StringBuilder(value.Length);
            foreach (var ch in value)
            {
                if (ch >= 'A' && ch <= 'Z')
                {
                    builder.Append((char)(ch + 32));
                }
                else if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9'))
                {
                    builder.Append(ch);
                }
                else
                {
                    builder.Append('_');
                }
            }

            var slug = builder.ToString();
            while (slug.Contains("__", StringComparison.Ordinal))
            {
                slug = slug.Replace("__", "_");
            }

            return slug.Trim('_');
        }

        private static void WriteFeatureRequests(List<ProtoFeatureRequest> requests)
        {
            if (requests == null)
            {
                return;
            }

            ProtoFeatureRequestStore.EnsureFeatureRequestFolder();
            foreach (var request in requests)
            {
                if (request == null)
                {
                    continue;
                }
                ProtoFeatureRequestStore.SaveRequest(request);
            }
        }

        private static List<ProtoFeatureRequest> OrderRequests(List<ProtoFeatureRequest> requests)
        {
            var ordered = new List<ProtoFeatureRequest>();
            if (requests == null || requests.Count == 0)
            {
                return ordered;
            }

            var requestById = new Dictionary<string, ProtoFeatureRequest>();
            foreach (var request in requests)
            {
                if (request == null || string.IsNullOrWhiteSpace(request.id))
                {
                    continue;
                }
                requestById[request.id] = request;
            }

            var inDegree = new Dictionary<string, int>();
            var edges = new Dictionary<string, List<string>>();

            foreach (var request in requests)
            {
                if (request == null || string.IsNullOrWhiteSpace(request.id))
                {
                    continue;
                }

                if (!inDegree.ContainsKey(request.id))
                {
                    inDegree[request.id] = 0;
                }

                if (request.dependsOn == null)
                {
                    continue;
                }

                foreach (var dep in request.dependsOn)
                {
                    if (string.IsNullOrWhiteSpace(dep))
                    {
                        continue;
                    }

                    if (!requestById.ContainsKey(dep))
                    {
                        continue;
                    }

                    if (!edges.TryGetValue(dep, out var list))
                    {
                        list = new List<string>();
                        edges[dep] = list;
                    }
                    list.Add(request.id);
                    inDegree[request.id] = inDegree.TryGetValue(request.id, out var count) ? count + 1 : 1;
                }
            }

            var queue = new Queue<string>();
            foreach (var pair in inDegree)
            {
                if (pair.Value == 0)
                {
                    queue.Enqueue(pair.Key);
                }
            }

            while (queue.Count > 0)
            {
                var id = queue.Dequeue();
                if (requestById.TryGetValue(id, out var request))
                {
                    ordered.Add(request);
                }

                if (!edges.TryGetValue(id, out var next))
                {
                    continue;
                }

                foreach (var child in next)
                {
                    inDegree[child] = inDegree[child] - 1;
                    if (inDegree[child] == 0)
                    {
                        queue.Enqueue(child);
                    }
                }
            }

            if (ordered.Count < requestById.Count)
            {
                foreach (var request in requests)
                {
                    if (request != null && !ordered.Contains(request))
                    {
                        ordered.Add(request);
                    }
                }
            }

            return ordered;
        }

        private static string NormalizePathForType(string type, string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return ProjectRoot;
            }

            var normalized = EnsureProjectPath(path);
            if (string.Equals(type, "script", StringComparison.OrdinalIgnoreCase))
            {
                if (normalized.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    var folder = Path.GetDirectoryName(normalized)?.Replace('\\', '/');
                    return string.IsNullOrWhiteSpace(folder) ? $"{ProjectRoot}/Scripts" : folder;
                }
            }
            else if (string.Equals(type, "scene", StringComparison.OrdinalIgnoreCase))
            {
                if (!normalized.EndsWith(".unity", StringComparison.OrdinalIgnoreCase) &&
                    !AssetDatabase.IsValidFolder(normalized))
                {
                    normalized += ".unity";
                }
            }
            else if (string.Equals(type, "material", StringComparison.OrdinalIgnoreCase))
            {
                if (!normalized.EndsWith(".mat", StringComparison.OrdinalIgnoreCase) &&
                    !AssetDatabase.IsValidFolder(normalized))
                {
                    normalized += ".mat";
                }
            }

            return normalized;
        }

        private static string NormalizeFolderPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return $"{ProjectRoot}/Scripts";
            }

            var normalized = EnsureProjectPath(path);
            if (normalized.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                var folder = Path.GetDirectoryName(normalized)?.Replace('\\', '/');
                return string.IsNullOrWhiteSpace(folder) ? $"{ProjectRoot}/Scripts" : folder;
            }

            return normalized;
        }

        private static string EnsureProjectPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return ProjectRoot;
            }

            var normalized = path.Trim().Replace('\\', '/');
            if (string.Equals(normalized, "Assets", StringComparison.OrdinalIgnoreCase))
            {
                return ProjectRoot;
            }

            if (normalized.StartsWith(ProjectRoot, StringComparison.OrdinalIgnoreCase))
            {
                return normalized;
            }

            if (normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                var relative = normalized.Substring("Assets/".Length).TrimStart('/');
                return string.IsNullOrWhiteSpace(relative) ? ProjectRoot : $"{ProjectRoot}/{relative}";
            }

            if (normalized.StartsWith("Assets", StringComparison.OrdinalIgnoreCase))
            {
                var relative = normalized.Substring("Assets".Length).TrimStart('/');
                return string.IsNullOrWhiteSpace(relative) ? ProjectRoot : $"{ProjectRoot}/{relative}";
            }

            return $"{ProjectRoot}/{normalized.TrimStart('/')}";
        }

        [Serializable]
        private sealed class ProtoPhaseOutlinePlan
        {
            public ProtoPhaseOutline[] phases;
        }

        [Serializable]
        private sealed class ProtoPhaseOutline
        {
            public string id;
            public string name;
            public string goal;
        }

        [Serializable]
        private sealed class ProtoPhaseOverview
        {
            public string overview;
        }

        [Serializable]
        private sealed class ProtoPhasePlan
        {
            public ProtoPlanPhase[] phases;
        }

        [Serializable]
        private sealed class ProtoPlanPhase
        {
            public string id;
            public string name;
            public string goal;
            public string overview;
            public ProtoFeatureRequest[] featureRequests;
        }

        [Serializable]
        private sealed class ProtoGenerationPlan
        {
            public ProtoFeatureRequest[] featureRequests;
        }
    }
}
