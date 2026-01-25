using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
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
        private static readonly Regex NamespaceRegex = new Regex(@"^\s*namespace\s+([A-Za-z0-9_.]+)", RegexOptions.Compiled);
        private static readonly Regex TypeRegex = new Regex(@"\b(?:(public|internal)\s+)?(?:static\s+|abstract\s+|sealed\s+|partial\s+)*\b(class|struct|interface|enum)\s+([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);
        private static readonly Regex MethodRegex = new Regex(@"\bpublic\s+(?:static\s+|virtual\s+|override\s+|abstract\s+|sealed\s+|async\s+)*([A-Za-z0-9_<>,\[\]\s]+)\s+([A-Za-z_][A-Za-z0-9_]*)\s*\(([^)]*)\)", RegexOptions.Compiled);
        private static readonly Regex PropertyRegex = new Regex(@"\bpublic\s+([A-Za-z0-9_<>,\[\]\s]+)\s+([A-Za-z_][A-Za-z0-9_]*)\s*\{\s*(get|set)", RegexOptions.Compiled);
        private static readonly Regex FieldRegex = new Regex(@"\bpublic\s+([A-Za-z0-9_<>,\[\]\s]+)\s+([A-Za-z_][A-Za-z0-9_]*)\s*(=|;)", RegexOptions.Compiled);
        private static readonly Regex IdentifierRegex = new Regex(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

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
        private string _operationProgressLabel = string.Empty;
        private int _operationProgressCurrent;
        private int _operationProgressTotal;
        private enum ProtoToolType
        {
            ReadFile,
            WriteFile,
            ListDirectory,
            SearchText
        }

        private static readonly string[] ToolLabels =
        {
            "Read File",
            "Write File",
            "List Folder",
            "Search Text"
        };

        private ProtoToolType _selectedToolType;
        private string _toolFilePath = string.Empty;
        private string _toolContent = string.Empty;
        private string _toolSearchTerm = string.Empty;
        private bool _toolIsRunning;
        private string _toolExecutionStatus = string.Empty;
        private string[] _diagnostics = Array.Empty<string>();
        private double _lastDiagnosticsRefresh;
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
        private bool _isCanceling;
        private bool _cancelRequested;
        private bool _isAutoRefreshDeferred;
        private ScriptContractIndex _scriptIndexCache;
        private bool _scriptIndexDirty = true;
        private static readonly Queue<SceneLayoutJob> SceneLayoutQueue = new Queue<SceneLayoutJob>();
        private static bool _isSceneLayoutRunning;
        private bool _isFixingErrors;
        private int _fixPassIterations = 2;
        private HashSet<string> _lastFixPassTargets;
        private string _lastFixPassLabel = string.Empty;
        private bool _isAgentLoopActive;
        private Task _agentLoopTask;
        private int _agentMaxIterations = 6;
        private int _agentIteration;
        private int _agentLastIterations;
        private string _agentLastOutcome = string.Empty;
        private string _agentLastOutcomeTime = string.Empty;
        private const int AgentStatusMaxChars = 140;
        private static readonly char[] AgentSpinnerFrames = { '|', '/', '-', '\\' };
        private const int AgentMaxToolOutputChars = 2400;
        private const int AgentMaxHistoryEntries = 6;
        private readonly List<string> _agentHistory = new List<string>();
        private string _lastAgentGoal;
        private readonly Dictionary<string, AgentReadSnapshot> _agentReadMemory = new Dictionary<string, AgentReadSnapshot>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _agentToolCallHistory = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool _chatHistoryDirty;
        private bool _chatHistorySaveQueued;
        private const int MaxChatHistoryMessages = 160;
        private const string SessionPrefsKey = "ProtoTipAI.LastChatSessionId";
        private const int SessionCompactionThreshold = 60;
        private const int SessionCompactionKeepMessages = 20;
        private const int SessionCompactionMaxChars = 12000;
        private readonly List<ProtoChatSessionInfo> _sessionIndex = new List<ProtoChatSessionInfo>();
        private string _sessionId;
        private string _sessionTitle;
        private long _sessionCreatedAt;
        private long _sessionUpdatedAt;
        private string _sessionSummary;
        private int _sessionSelectionIndex = -1;
        private bool _showSessionSummary;
        private bool _isCompactingSession;
        private Vector2 _planPromptScroll;
        private Vector2 _inputScroll;
        private static GUIStyle _multiLineTextAreaStyle;

        private static GUIStyle MultiLineTextAreaStyle
        {
            get
            {
                if (_multiLineTextAreaStyle == null)
                {
                    _multiLineTextAreaStyle = new GUIStyle(EditorStyles.textArea)
                    {
                        wordWrap = true
                    };
                }

                return _multiLineTextAreaStyle;
            }
        }

        [MenuItem("Proto/Chat")]
        public static void ShowWindow()
        {
            var inspectorType = typeof(EditorWindow).Assembly.GetType("UnityEditor.InspectorWindow");
            if (inspectorType != null)
            {
                var window = GetWindow<ProtoChatWindow>("Proto Chat", false, new[] { inspectorType });
                window.minSize = new Vector2(520f, 360f);
            }
            else
            {
                var window = GetWindow<ProtoChatWindow>("Proto Chat");
                window.minSize = new Vector2(520f, 360f);
            }
        }

        internal static ProtoChatWindow FindWindow()
        {
            var windows = Resources.FindObjectsOfTypeAll<ProtoChatWindow>();
            if (windows == null || windows.Length == 0)
            {
                return null;
            }

            return windows[0];
        }

        private void OnEnable()
        {
            RestorePlanFromDisk();
            RestoreChatHistoryFromDisk();
            _diagnostics = CollectDiagnostics();
            _lastDiagnosticsRefresh = EditorApplication.timeSinceStartup;
            CheckApiWaitTimeout(true);
        }

        private void OnDisable()
        {
            SaveChatHistoryIfDirty();
        }

        private void OnGUI()
        {
            DrawChatPanel();
        }

        internal void DrawChatPanel()
        {
            CheckAgentLoopCompletion();
            CheckApiWaitTimeout(false);
            DrawHeader(true);
            DrawChatStatus();
            DrawChatHistory();
            DrawInputArea();
        }

        internal void DrawControlPanel()
        {
            CheckAgentLoopCompletion();
            CheckApiWaitTimeout(false);
            DrawHeader(false);
            DrawSessionPanel();
            DrawContextOptions();
            DrawDiagnosticsPanel();
            DrawAgentTools();
            DrawToolPanel();
        }

        private void DrawHeader(bool includeClear)
        {
            var settings = ProtoProviderSettings.GetSnapshot();
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label(settings.Provider.DisplayName, EditorStyles.toolbarButton);
                GUILayout.FlexibleSpace();
                GUILayout.Label($"Model: {settings.Model}", EditorStyles.miniLabel);
                if (includeClear && GUILayout.Button("New Session", EditorStyles.toolbarButton))
                {
                    ClearChatHistory();
                }
            }

            if (string.IsNullOrWhiteSpace(settings.ApiKey))
            {
                EditorGUILayout.HelpBox(
                    $"Missing API key for {settings.Provider.DisplayName}. Open Setup to configure.",
                    MessageType.Warning);
                if (GUILayout.Button("Open Setup"))
                {
                    ProtoSetupWindow.ShowWindow();
                }
            }
        }

        private void DrawChatStatus()
        {
            if (!ShouldShowChatStatus())
            {
                return;
            }

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Status", EditorStyles.boldLabel);

                if (_isAgentLoopActive)
                {
                    EditorGUILayout.LabelField(BuildAgentRunningLabel(), EditorStyles.wordWrappedLabel);
                    if (_agentMaxIterations > 0)
                    {
                        var progress = Mathf.Clamp01(_agentMaxIterations == 0
                            ? 0f
                            : (float)Mathf.Max(1, _agentIteration) / _agentMaxIterations);
                        var label = $"Agent loop ({Mathf.Max(1, _agentIteration)}/{_agentMaxIterations})";
                        var rect = GUILayoutUtility.GetRect(18f, 18f);
                        EditorGUI.ProgressBar(rect, progress, label);
                    }

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.FlexibleSpace();
                        if (GUILayout.Button("Stop Agent", GUILayout.Width(120f)))
                        {
                            ForceStopAgentLoop("Stopped by user");
                        }
                    }
                }
                else if (!string.IsNullOrWhiteSpace(_agentLastOutcome))
                {
                    EditorGUILayout.LabelField(BuildAgentOutcomeLabel(), EditorStyles.wordWrappedLabel);
                }

                if (_isApiWaiting)
                {
                    var elapsed = EditorApplication.timeSinceStartup - _apiWaitStarted;
                    var timeoutLabel = _apiWaitTimeoutSeconds > 0 ? $" / {_apiWaitTimeoutSeconds}s" : string.Empty;
                    EditorGUILayout.LabelField(
                        $"Waiting for OpenAI: {_apiWaitLabel} ({elapsed:0}s{timeoutLabel})",
                        EditorStyles.wordWrappedLabel);
                }

                if (_operationProgressTotal > 0)
                {
                    var progress = Mathf.Clamp01(_operationProgressTotal == 0
                        ? 0f
                        : (float)_operationProgressCurrent / _operationProgressTotal);
                    var label = string.IsNullOrWhiteSpace(_operationProgressLabel)
                        ? $"{_operationProgressCurrent}/{_operationProgressTotal}"
                        : $"{_operationProgressLabel} ({_operationProgressCurrent}/{_operationProgressTotal})";
                    var rect = GUILayoutUtility.GetRect(18f, 18f);
                    EditorGUI.ProgressBar(rect, progress, label);
                }

                if (!string.IsNullOrWhiteSpace(_toolStatus))
                {
                    EditorGUILayout.LabelField(_toolStatus, EditorStyles.wordWrappedLabel);
                }

                if (!string.IsNullOrWhiteSpace(_status))
                {
                    EditorGUILayout.HelpBox(_status, MessageType.Info);
                }
            }
        }

        private bool ShouldShowChatStatus()
        {
            return _isAgentLoopActive ||
                   _isApiWaiting ||
                   _operationProgressTotal > 0 ||
                   !string.IsNullOrWhiteSpace(_toolStatus) ||
                   !string.IsNullOrWhiteSpace(_status) ||
                   !string.IsNullOrWhiteSpace(_agentLastOutcome);
        }

        private string BuildAgentRunningLabel()
        {
            var spinner = GetSpinnerFrame();
            var currentIteration = Mathf.Max(1, _agentIteration);
            var totalLabel = _agentMaxIterations > 0 ? $"/{_agentMaxIterations}" : string.Empty;
            return $"Agent running {spinner} ({currentIteration}{totalLabel})";
        }

        private string BuildAgentOutcomeLabel()
        {
            if (string.IsNullOrWhiteSpace(_agentLastOutcome))
            {
                return string.Empty;
            }

            var iterationLabel = _agentLastIterations > 0 && _agentMaxIterations > 0
                ? $" ({_agentLastIterations}/{_agentMaxIterations})"
                : (_agentLastIterations > 0 ? $" ({_agentLastIterations})" : string.Empty);
            var timeLabel = string.IsNullOrWhiteSpace(_agentLastOutcomeTime) ? string.Empty : $" at {_agentLastOutcomeTime}";
            return $"Last agent: {_agentLastOutcome}{iterationLabel}{timeLabel}";
        }

        private static string GetSpinnerFrame()
        {
            if (AgentSpinnerFrames.Length == 0)
            {
                return string.Empty;
            }

            var index = (int)(EditorApplication.timeSinceStartup * 4f) % AgentSpinnerFrames.Length;
            return AgentSpinnerFrames[index].ToString();
        }

        private static string ClampStatusLine(string text, int maxChars)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            var sanitized = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (maxChars <= 0 || sanitized.Length <= maxChars)
            {
                return sanitized;
            }

            return $"{sanitized.Substring(0, maxChars)}...";
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
            }
        }

        private void DrawSessionPanel()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Session", EditorStyles.boldLabel);
                var title = string.IsNullOrWhiteSpace(_sessionTitle) ? "Untitled" : _sessionTitle;
                EditorGUILayout.LabelField($"Current: {title}", EditorStyles.wordWrappedLabel);

                if (_sessionIndex.Count > 0)
                {
                    var labels = BuildSessionLabels(_sessionIndex);
                    var selection = _sessionSelectionIndex < 0 ? 0 : _sessionSelectionIndex;
                    var nextSelection = EditorGUILayout.Popup("Switch", selection, labels);
                    if (nextSelection != _sessionSelectionIndex && nextSelection >= 0 && nextSelection < _sessionIndex.Count)
                    {
                        SwitchSession(_sessionIndex[nextSelection].id);
                    }
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(!CanSwitchSessions()))
                    {
                        if (GUILayout.Button("New Session", GUILayout.Width(140f)))
                        {
                            StartNewSession();
                        }
                    }

                    using (new EditorGUI.DisabledScope(!CanSwitchSessions() || !HasPreviousSession()))
                    {
                        if (GUILayout.Button("Continue Last", GUILayout.Width(140f)))
                        {
                            ContinueLastSession();
                        }
                    }

                    GUILayout.FlexibleSpace();
                }

                if (!string.IsNullOrWhiteSpace(_sessionSummary))
                {
                    _showSessionSummary = EditorGUILayout.Foldout(_showSessionSummary, "Summary");
                    if (_showSessionSummary)
                    {
                        using (new EditorGUI.DisabledScope(true))
                        {
                            EditorGUILayout.TextArea(_sessionSummary, GUILayout.MinHeight(60f));
                        }
                    }
                }
            }
        }

        private bool CanSwitchSessions()
        {
            return !_isSending && !_isAgentLoopActive && !_isCompactingSession && !IsOperationActive();
        }

        private bool HasPreviousSession()
        {
            if (_sessionIndex.Count == 0)
            {
                return false;
            }

            if (_sessionIndex.Count == 1)
            {
                return !string.Equals(_sessionIndex[0].id, _sessionId, StringComparison.OrdinalIgnoreCase);
            }

            return true;
        }

        private static string[] BuildSessionLabels(List<ProtoChatSessionInfo> sessions)
        {
            if (sessions == null || sessions.Count == 0)
            {
                return Array.Empty<string>();
            }

            var labels = new string[sessions.Count];
            for (var i = 0; i < sessions.Count; i++)
            {
                labels[i] = BuildSessionLabel(sessions[i]);
            }

            return labels;
        }

        private static string BuildSessionLabel(ProtoChatSessionInfo session)
        {
            if (session == null)
            {
                return "Unknown";
            }

            var title = string.IsNullOrWhiteSpace(session.title) ? "Untitled" : session.title.Trim();
            var timeLabel = session.updatedAt > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(session.updatedAt).ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                : "unknown";
            return $"{title} ({timeLabel})";
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
                        ProtoProjectSettings.SetProjectSummary(summary);
                        _status = "Project summary refreshed.";
                    }

                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("Open Setup", GUILayout.Width(120f)))
                    {
                        ProtoSetupWindow.ShowWindow();
                    }
                }

                if (string.IsNullOrWhiteSpace(ProtoProjectSettings.GetProjectGoal()))
                {
                    EditorGUILayout.HelpBox("Project Goal is empty. Set it in Setup for better context.", MessageType.Info);
                }
            }
        }

        private void DrawDiagnosticsPanel()
        {
            EnsureDiagnosticsRefreshed();
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Diagnostics", EditorStyles.boldLabel);
                if (_diagnostics.Length == 0)
                {
                    EditorGUILayout.LabelField("No compilation errors or warnings detected recently.", EditorStyles.wordWrappedLabel);
                }
                else
                {
                    var count = Math.Min(_diagnostics.Length, 10);
                    for (var i = 0; i < count; i++)
                    {
                        EditorGUILayout.LabelField(_diagnostics[i], EditorStyles.miniLabel);
                    }
                }
            }
        }

        private void DrawAgentTools()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Agent Tools", EditorStyles.boldLabel);
                EditorGUILayout.LabelField("Plan Prompt");
                var planPromptHeight = Mathf.Max(72f, EditorGUIUtility.singleLineHeight * 4f);
                using (var scroll = new EditorGUILayout.ScrollViewScope(_planPromptScroll, GUILayout.Height(planPromptHeight)))
                {
                    _planPromptScroll = scroll.scrollPosition;
                    _planPrompt = EditorGUILayout.TextArea(_planPrompt, MultiLineTextAreaStyle, GUILayout.ExpandHeight(true));
                }
                _overwriteScript = EditorGUILayout.ToggleLeft("Overwrite scripts if they exist", _overwriteScript);
                _fixPassIterations = EditorGUILayout.IntSlider("Fix Step Iterations", _fixPassIterations, 1, 5);
                _agentMaxIterations = EditorGUILayout.IntSlider("Agent Loop Iterations", _agentMaxIterations, 1, 50);

                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(_isGeneratingPlan || !ProtoProviderSettings.HasApiKey()))
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

                using (new EditorGUI.DisabledScope(_isFixingErrors || !ProtoProviderSettings.HasApiKey()))
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

                if (_operationProgressTotal > 0)
                {
                    var progress = Mathf.Clamp01(_operationProgressTotal == 0
                        ? 0f
                        : (float)_operationProgressCurrent / _operationProgressTotal);
                    var label = string.IsNullOrWhiteSpace(_operationProgressLabel)
                        ? $"{_operationProgressCurrent}/{_operationProgressTotal}"
                        : $"{_operationProgressLabel} ({_operationProgressCurrent}/{_operationProgressTotal})";
                    var rect = GUILayoutUtility.GetRect(18f, 18f);
                    EditorGUI.ProgressBar(rect, progress, label);
                }

                if (!string.IsNullOrWhiteSpace(_toolStatus))
                {
                    EditorGUILayout.HelpBox(_toolStatus, MessageType.Info);
                }
            }
        }

        private void DrawToolPanel()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Tool Bench", EditorStyles.boldLabel);
                _selectedToolType = (ProtoToolType)EditorGUILayout.Popup("Tool", (int)_selectedToolType, ToolLabels);
                _toolFilePath = EditorGUILayout.TextField("Path", _toolFilePath);

                switch (_selectedToolType)
                {
                    case ProtoToolType.ReadFile:
                        EditorGUILayout.LabelField("Reads a file and shows a preview.", EditorStyles.wordWrappedLabel);
                        break;
                    case ProtoToolType.WriteFile:
                        EditorGUILayout.LabelField("Replaces the file content with your text.", EditorStyles.wordWrappedLabel);
                        _toolContent = EditorGUILayout.TextArea(_toolContent, GUILayout.MinHeight(80f));
                        break;
                    case ProtoToolType.ListDirectory:
                        EditorGUILayout.LabelField("Lists the first entries inside a folder.", EditorStyles.wordWrappedLabel);
                        break;
                    case ProtoToolType.SearchText:
                        EditorGUILayout.LabelField("Search for text across the project.", EditorStyles.wordWrappedLabel);
                        _toolSearchTerm = EditorGUILayout.TextField("Search Text", _toolSearchTerm);
                        break;
                }

                if (!string.IsNullOrWhiteSpace(_toolExecutionStatus))
                {
                    EditorGUILayout.HelpBox(_toolExecutionStatus, MessageType.Info);
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.FlexibleSpace();
                    using (new EditorGUI.DisabledScope(_toolIsRunning))
                    {
                        if (GUILayout.Button("Run Tool", GUILayout.Width(120f)))
                        {
                            _ = RunToolAsync();
                        }
                    }
                }
            }
        }

        private async Task RunToolAsync()
        {
            if (_toolIsRunning)
            {
                return;
            }

            var trimmedPath = _toolFilePath.Trim();
            if (string.IsNullOrWhiteSpace(trimmedPath))
            {
                _toolExecutionStatus = "Provide a path first.";
                return;
            }

            _toolIsRunning = true;
            _toolExecutionStatus = "Running tool...";
            var absolutePath = ResolveAbsolutePath(trimmedPath);
            var relativePath = BuildRelativePath(absolutePath);
            try
            {
                switch (_selectedToolType)
                {
                    case ProtoToolType.ReadFile:
                        await RunReadFileAsync(absolutePath, relativePath).ConfigureAwait(false);
                        break;
                    case ProtoToolType.WriteFile:
                        await RunWriteFileAsync(absolutePath, relativePath).ConfigureAwait(false);
                        break;
                    case ProtoToolType.ListDirectory:
                        await RunListDirectoryAsync(absolutePath, relativePath).ConfigureAwait(false);
                        break;
                    case ProtoToolType.SearchText:
                        await RunSearchTextAsync(absolutePath, relativePath).ConfigureAwait(false);
                        break;
                }
            }
            catch (Exception ex)
            {
                _toolExecutionStatus = $"Tool failed: {ex.Message}";
            }
            finally
            {
                _toolIsRunning = false;
                Repaint();
            }
        }

        private async Task RunReadFileAsync(string absolutePath, string relativePath)
        {
            if (!File.Exists(absolutePath))
            {
                _toolExecutionStatus = $"File not found: {relativePath}.";
                return;
            }

            string content;
            try
            {
                content = File.ReadAllText(absolutePath);
            }
            catch (Exception ex)
            {
                _toolExecutionStatus = $"Unable to read file: {ex.Message}";
                return;
            }

            var preview = content.Length > 4000 ? $"{content.Substring(0, 4000)}\n... (truncated)" : content;
            AppendToolMessage("Read File", relativePath, preview);
            _toolExecutionStatus = $"Read {relativePath} ({content.Length} chars).";
        }

        private async Task RunWriteFileAsync(string absolutePath, string relativePath)
        {
            if (!ProtoToolSettings.GetAutoConfirm())
            {
                if (!EditorUtility.DisplayDialog(
                    "Confirm Write",
                    $"Overwrite {relativePath}?",
                    "Overwrite",
                    "Cancel"))
                {
                    _toolExecutionStatus = "Write canceled.";
                    return;
                }
            }

            await RunOnMainThread(() =>
            {
                var directory = Path.GetDirectoryName(absolutePath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(absolutePath, _toolContent ?? string.Empty);
                AssetDatabase.Refresh();
            }).ConfigureAwait(false);

            AppendToolMessage("Write File", relativePath, _toolContent ?? string.Empty);
            _toolExecutionStatus = $"Wrote {relativePath}.";
        }

        private Task RunListDirectoryAsync(string absolutePath, string relativePath)
        {
            if (!Directory.Exists(absolutePath))
            {
                _toolExecutionStatus = $"Folder not found: {relativePath}.";
                return Task.CompletedTask;
            }

            var entries = Directory.GetFileSystemEntries(absolutePath);
            var previewList = new List<string>(Math.Min(entries.Length, 20));
            for (var i = 0; i < entries.Length && previewList.Count < 20; i++)
            {
                previewList.Add(BuildRelativePath(entries[i]));
            }

            var body = previewList.Count == 0 ? "(empty)" : string.Join("\n", previewList);
            AppendToolMessage("List Folder", relativePath, body);
            _toolExecutionStatus = $"Found {entries.Length} entries.";
            return Task.CompletedTask;
        }

        private async Task RunSearchTextAsync(string absolutePath, string relativePath)
        {
            var term = _toolSearchTerm.Trim();
            if (string.IsNullOrWhiteSpace(term))
            {
                _toolExecutionStatus = "Enter text to search for.";
                return;
            }

            var searchRoot = Directory.Exists(absolutePath) ? absolutePath : GetProjectRoot();
            var matches = new List<string>();
            await Task.Run(() =>
            {
                foreach (var file in Directory.EnumerateFiles(searchRoot, "*.*", SearchOption.AllDirectories))
                {
                    if (file.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string content;
                    try
                    {
                        content = File.ReadAllText(file);
                    }
                    catch
                    {
                        continue;
                    }

                    if (content.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        matches.Add(BuildRelativePath(file));
                        if (matches.Count >= 12)
                        {
                            break;
                        }
                    }
                }
            }).ConfigureAwait(false);

            if (matches.Count == 0)
            {
                _toolExecutionStatus = $"No matches for \"{term}\".";
                return;
            }

            AppendToolMessage("Search Text", relativePath, string.Join("\n", matches));
            _toolExecutionStatus = $"Found {matches.Count} matches for \"{term}\".";
        }

        private void AppendToolMessage(string title, string relativePath, string content)
        {
            var builder = new StringBuilder();
            builder.AppendLine($"[{title}] {relativePath}");
            builder.AppendLine(content);
            AddChatMessage(new ProtoChatMessage
            {
                role = RoleAssistant,
                content = builder.ToString().Trim()
            });
        }

        private void AddChatMessage(ProtoChatMessage message)
        {
            if (message == null)
            {
                return;
            }

            _messages.Add(message);
            MarkChatHistoryDirty();
        }

        private void UpdateChatMessageContent(int index, string content)
        {
            if (index < 0 || index >= _messages.Count)
            {
                return;
            }

            if (_messages[index] == null)
            {
                return;
            }

            _messages[index].content = content;
            MarkChatHistoryDirty();
        }

        private void ClearChatHistory()
        {
            StartNewSession();
        }

        private void MarkChatHistoryDirty()
        {
            _chatHistoryDirty = true;
            if (_chatHistorySaveQueued)
            {
                return;
            }

            _chatHistorySaveQueued = true;
            EditorApplication.delayCall += SaveChatHistoryIfDirty;
        }

        private void SaveChatHistoryIfDirty()
        {
            if (!_chatHistoryDirty)
            {
                _chatHistorySaveQueued = false;
                return;
            }

            _chatHistorySaveQueued = false;
            _chatHistoryDirty = false;
            SaveChatHistoryToDisk();
        }

        private void SaveChatHistoryToDisk()
        {
            EnsureSessionId();
            var now = ProtoChatSessionStore.NowMillis();
            if (_sessionCreatedAt <= 0)
            {
                _sessionCreatedAt = now;
            }

            _sessionUpdatedAt = now;
            if (IsDefaultSessionTitle(_sessionTitle))
            {
                var derived = DeriveSessionTitle(_messages);
                if (!string.IsNullOrWhiteSpace(derived))
                {
                    _sessionTitle = derived;
                }
            }

            if (string.IsNullOrWhiteSpace(_sessionTitle))
            {
                _sessionTitle = BuildDefaultSessionTitle(now);
            }

            var messages = TrimMessagesForStorage(_messages);
            var session = new ProtoChatSession
            {
                id = _sessionId,
                title = _sessionTitle,
                createdAt = _sessionCreatedAt,
                updatedAt = _sessionUpdatedAt,
                summary = _sessionSummary ?? string.Empty,
                messages = messages,
                lastAgentGoal = _lastAgentGoal ?? string.Empty,
                agentHistory = _agentHistory.Count == 0 ? Array.Empty<string>() : _agentHistory.ToArray()
            };

            ProtoChatSessionStore.SaveSession(session);
            UpsertSessionIndex(session);
            ProtoChatSessionStore.SaveSessionIndex(_sessionIndex);
            _sessionSelectionIndex = FindSessionIndex(_sessionId);
            EditorPrefs.SetString(SessionPrefsKey, _sessionId);
        }

        private void RestoreChatHistoryFromDisk()
        {
            _sessionIndex.Clear();
            _sessionIndex.AddRange(ProtoChatSessionStore.LoadSessionIndex());

            if (ProtoChatSessionStore.TryMigrateLegacyHistory(out var migrated))
            {
                UpsertSessionIndex(migrated);
                ProtoChatSessionStore.SaveSessionIndex(_sessionIndex);
                SetActiveSession(migrated);
                return;
            }

            var lastSessionId = EditorPrefs.GetString(SessionPrefsKey, string.Empty);
            ProtoChatSession session = null;
            if (!string.IsNullOrWhiteSpace(lastSessionId))
            {
                session = ProtoChatSessionStore.LoadSession(lastSessionId);
            }

            if (session == null && _sessionIndex.Count > 0)
            {
                session = ProtoChatSessionStore.LoadSession(_sessionIndex[0].id);
            }

            if (session == null)
            {
                session = CreateNewSessionSnapshot();
                ProtoChatSessionStore.SaveSession(session);
                UpsertSessionIndex(session);
                ProtoChatSessionStore.SaveSessionIndex(_sessionIndex);
            }

            SetActiveSession(session);
        }

        private void StartNewSession()
        {
            if (!CanSwitchSessions())
            {
                return;
            }

            SaveChatHistoryIfDirty();
            var session = CreateNewSessionSnapshot();
            ProtoChatSessionStore.SaveSession(session);
            UpsertSessionIndex(session);
            ProtoChatSessionStore.SaveSessionIndex(_sessionIndex);
            SetActiveSession(session);
        }

        private void ContinueLastSession()
        {
            if (_sessionIndex.Count == 0)
            {
                return;
            }

            var target = _sessionIndex[0];
            if (string.Equals(target.id, _sessionId, StringComparison.OrdinalIgnoreCase) && _sessionIndex.Count > 1)
            {
                target = _sessionIndex[1];
            }

            if (string.Equals(target.id, _sessionId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            SwitchSession(target.id);
        }

        private void SwitchSession(string sessionId)
        {
            if (!CanSwitchSessions() || string.IsNullOrWhiteSpace(sessionId))
            {
                return;
            }

            SaveChatHistoryIfDirty();
            var session = ProtoChatSessionStore.LoadSession(sessionId);
            if (session == null)
            {
                return;
            }

            SetActiveSession(session);
        }

        private void SetActiveSession(ProtoChatSession session)
        {
            if (session == null)
            {
                return;
            }

            if (FindSessionIndex(session.id) < 0)
            {
                UpsertSessionIndex(session);
                ProtoChatSessionStore.SaveSessionIndex(_sessionIndex);
            }

            _messages.Clear();
            if (session.messages != null)
            {
                _messages.AddRange(session.messages);
            }

            _agentHistory.Clear();
            if (session.agentHistory != null)
            {
                _agentHistory.AddRange(session.agentHistory);
            }

            _lastAgentGoal = session.lastAgentGoal ?? string.Empty;
            _sessionSummary = session.summary ?? string.Empty;
            _sessionId = session.id ?? string.Empty;
            _sessionTitle = session.title ?? string.Empty;
            _sessionCreatedAt = session.createdAt;
            _sessionUpdatedAt = session.updatedAt;
            _sessionSelectionIndex = FindSessionIndex(_sessionId);
            _agentReadMemory.Clear();
            _agentToolCallHistory.Clear();
            _status = string.Empty;
            _toolStatus = string.Empty;
            ResetAgentStatus();
            EditorPrefs.SetString(SessionPrefsKey, _sessionId);
            Repaint();
        }

        private void EnsureSessionId()
        {
            if (!string.IsNullOrWhiteSpace(_sessionId))
            {
                return;
            }

            var session = CreateNewSessionSnapshot();
            _sessionId = session.id;
            _sessionTitle = session.title;
            _sessionCreatedAt = session.createdAt;
            _sessionUpdatedAt = session.updatedAt;
            _sessionSummary = session.summary ?? string.Empty;
            UpsertSessionIndex(session);
            ProtoChatSessionStore.SaveSessionIndex(_sessionIndex);
        }

        private ProtoChatSession CreateNewSessionSnapshot()
        {
            var now = ProtoChatSessionStore.NowMillis();
            return new ProtoChatSession
            {
                id = Guid.NewGuid().ToString("N"),
                title = BuildDefaultSessionTitle(now),
                createdAt = now,
                updatedAt = now,
                summary = string.Empty,
                messages = Array.Empty<ProtoChatMessage>(),
                lastAgentGoal = string.Empty,
                agentHistory = Array.Empty<string>()
            };
        }

        private void UpsertSessionIndex(ProtoChatSession session)
        {
            if (session == null || string.IsNullOrWhiteSpace(session.id))
            {
                return;
            }

            var info = new ProtoChatSessionInfo
            {
                id = session.id,
                title = session.title,
                createdAt = session.createdAt,
                updatedAt = session.updatedAt
            };

            var index = FindSessionIndex(session.id);
            if (index >= 0)
            {
                _sessionIndex[index] = info;
            }
            else
            {
                _sessionIndex.Add(info);
            }

            _sessionIndex.Sort((a, b) => b.updatedAt.CompareTo(a.updatedAt));
        }

        private int FindSessionIndex(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return -1;
            }

            for (var i = 0; i < _sessionIndex.Count; i++)
            {
                var entry = _sessionIndex[i];
                if (entry != null && string.Equals(entry.id, sessionId, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return -1;
        }

        private static ProtoChatMessage[] TrimMessagesForStorage(List<ProtoChatMessage> messages)
        {
            if (messages == null || messages.Count == 0)
            {
                return Array.Empty<ProtoChatMessage>();
            }

            var count = messages.Count;
            var startIndex = count > MaxChatHistoryMessages ? count - MaxChatHistoryMessages : 0;
            var messageCount = count - startIndex;
            var trimmed = new ProtoChatMessage[messageCount];
            for (var i = 0; i < messageCount; i++)
            {
                trimmed[i] = messages[startIndex + i];
            }

            return trimmed;
        }

        private static string DeriveSessionTitle(List<ProtoChatMessage> messages)
        {
            if (messages == null)
            {
                return string.Empty;
            }

            foreach (var message in messages)
            {
                if (message == null || string.IsNullOrWhiteSpace(message.content))
                {
                    continue;
                }

                if (!string.Equals(message.role, RoleUser, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var title = message.content.Trim();
                if (title.Length > 60)
                {
                    title = title.Substring(0, 57) + "...";
                }

                return title;
            }

            return string.Empty;
        }

        private static string BuildDefaultSessionTitle(long timestampMillis)
        {
            if (timestampMillis <= 0)
            {
                timestampMillis = ProtoChatSessionStore.NowMillis();
            }

            var date = DateTimeOffset.FromUnixTimeMilliseconds(timestampMillis).ToLocalTime();
            return $"New session - {date:yyyy-MM-dd HH:mm}";
        }

        private static bool IsDefaultSessionTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return true;
            }

            return title.StartsWith("New session - ", StringComparison.OrdinalIgnoreCase);
        }

        private static string ResolveAbsolutePath(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                return string.Empty;
            }

            var normalized = relativePath.Replace('\\', '/');
            if (Path.IsPathRooted(normalized))
            {
                return Path.GetFullPath(normalized);
            }

            var root = GetProjectRoot();
            return Path.GetFullPath(Path.Combine(root, normalized));
        }

        private static string BuildRelativePath(string absolutePath)
        {
            if (string.IsNullOrWhiteSpace(absolutePath))
            {
                return string.Empty;
            }

            var normalized = absolutePath.Replace('\\', '/');
            var projectRoot = GetProjectRoot().Replace('\\', '/');
            if (!string.IsNullOrWhiteSpace(projectRoot) && normalized.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
            {
                var relative = normalized.Substring(projectRoot.Length).TrimStart('/');
                if (string.IsNullOrWhiteSpace(relative))
                {
                    return "Assets";
                }

                if (relative.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(relative, "Assets", StringComparison.OrdinalIgnoreCase))
                {
                    return relative;
                }

                return relative;
            }

            return normalized;
        }

        private static string GetProjectRoot()
        {
            return Directory.GetParent(Application.dataPath)?.FullName ?? Directory.GetCurrentDirectory();
        }

        private void EnsureDiagnosticsRefreshed()
        {
            var now = EditorApplication.timeSinceStartup;
            if (now - _lastDiagnosticsRefresh < 3.0)
            {
                return;
            }

            _diagnostics = CollectDiagnostics();
            _lastDiagnosticsRefresh = now;
        }

        private static string[] CollectDiagnostics()
        {
            var errors = GetConsoleErrorItems();
            if (errors.Count == 0)
            {
                return Array.Empty<string>();
            }

            var results = new List<string>(Math.Min(errors.Count, 12));
            for (var i = 0; i < errors.Count && results.Count < 12; i++)
            {
                var error = errors[i];
                var path = string.IsNullOrWhiteSpace(error.filePath) ? "(console)" : error.filePath;
                var lineInfo = error.line >= 0 ? $":{error.line}" : string.Empty;
                results.Add($"{error.type}: {path}{lineInfo} {error.message}");
            }

            return results.ToArray();
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
                            _ = ApplyPlanStageAsync("prefabs", new[] { "prefab", "prefab_component" });
                        }
                        if (GUILayout.Button("Create Scenes", GUILayout.Width(140f)))
                        {
                            _ = ApplyPlanStageAsync("scenes", new[] { "scene", "scene_prefab", "scene_manager" });
                        }
                        if (GUILayout.Button("Create Assets", GUILayout.Width(140f)))
                        {
                            _ = ApplyPlanStageAsync("assets", new[] { "asset" });
                        }
                    }
                }
            }
        }

        private string GetSelectedPhaseId()
        {
            if (_cachedPhasePlan?.phases == null || _cachedPhasePlan.phases.Length == 0)
            {
                return null;
            }

            if (_selectedPhaseIndex <= 0)
            {
                return null;
            }

            var index = _selectedPhaseIndex - 1;
            if (index < 0 || index >= _cachedPhasePlan.phases.Length)
            {
                return null;
            }

            return _cachedPhasePlan.phases[index]?.id;
        }

        private string GetSelectedPhaseLabel()
        {
            if (_phaseLabels == null || _phaseLabels.Length <= 1)
            {
                return null;
            }

            if (_selectedPhaseIndex <= 0 || _selectedPhaseIndex >= _phaseLabels.Length)
            {
                return null;
            }

            return _phaseLabels[_selectedPhaseIndex];
        }

        private static List<ProtoFeatureRequest> FilterRequestsByPhase(List<ProtoFeatureRequest> requests, string phaseId)
        {
            if (requests == null)
            {
                return new List<ProtoFeatureRequest>();
            }

            if (string.IsNullOrWhiteSpace(phaseId))
            {
                return new List<ProtoFeatureRequest>(requests);
            }

            var filtered = new List<ProtoFeatureRequest>();
            foreach (var request in requests)
            {
                if (request == null)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(request.phaseId) || string.Equals(request.phaseId, phaseId, StringComparison.OrdinalIgnoreCase))
                {
                    filtered.Add(request);
                }
            }

            return filtered;
        }

        private void DrawInputArea()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Message", EditorStyles.boldLabel);
                var inputHeight = Mathf.Max(92f, EditorGUIUtility.singleLineHeight * 5f);
                using (var scroll = new EditorGUILayout.ScrollViewScope(_inputScroll, GUILayout.Height(inputHeight)))
                {
                    _inputScroll = scroll.scrollPosition;
                    _input = EditorGUILayout.TextArea(_input, MultiLineTextAreaStyle, GUILayout.ExpandHeight(true));
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.FlexibleSpace();
                    using (new EditorGUI.DisabledScope(_isSending || _isAgentLoopActive))
                    {
                        if (GUILayout.Button("Command", GUILayout.Width(120f)))
                        {
                            OpenCommandDialog();
                        }
                    }

                    using (new EditorGUI.DisabledScope(_isAgentLoopActive || _isSending || !ProtoProviderSettings.HasApiKey() || string.IsNullOrWhiteSpace(_input)))
                    {
                        if (GUILayout.Button(_isAgentLoopActive ? "Agent..." : "Agent", GUILayout.Width(120f)))
                        {
                            var text = _input.Trim();
                            _input = string.Empty;
                            StartAgentLoop(text);
                        }
                    }
                }
            }
        }

        private void OpenCommandDialog()
        {
            ProtoAgentCommandWindow.Show(this);
        }

        private void StartAgentLoop(string userGoal)
        {
            if (string.IsNullOrWhiteSpace(userGoal) || _isAgentLoopActive)
            {
                return;
            }

            _agentLoopTask = RunAgentLoopAsync(userGoal);
        }

        private void CheckAgentLoopCompletion()
        {
            if (!_isAgentLoopActive || _agentLoopTask == null || !_agentLoopTask.IsCompleted)
            {
                return;
            }

            FinalizeAgentLoopState("Completed");
        }

        private void ForceStopAgentLoop(string reason)
        {
            if (!_isAgentLoopActive)
            {
                return;
            }

            _cancelRequested = true;
            if (_operationCts != null && !_operationCts.IsCancellationRequested)
            {
                _operationCts.Cancel();
            }
            FinalizeAgentLoopState(reason);
        }

        private void FinalizeAgentLoopState(string reason)
        {
            _isAgentLoopActive = false;
            _agentIteration = 0;
            if (string.IsNullOrWhiteSpace(_agentLastOutcome) && !string.IsNullOrWhiteSpace(reason))
            {
                SetAgentOutcome(reason, _agentLastIterations);
            }

            _status = string.Empty;
            _toolStatus = string.Empty;
            EndApiWait();
            EndOperation();
            _agentLoopTask = null;
            Repaint();
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
            AddChatMessage(new ProtoChatMessage { role = RoleUser, content = userText });
            await MaybeCompactSessionAsync(token).ConfigureAwait(false);
            var requestMessages = _messages.ToArray();
            var assistantIndex = _messages.Count;
            AddChatMessage(new ProtoChatMessage { role = RoleAssistant, content = "..." });
            Repaint();

            try
            {
                var settings = await RunOnMainThread(GetSettingsSnapshot).ConfigureAwait(false);
                await RunOnMainThread(() => BeginApiWait("chat response")).ConfigureAwait(false);
                var response = await ProtoProviderClient.SendChatAsync(settings, BuildRequestMessages(requestMessages), _apiWaitTimeoutSeconds, token).ConfigureAwait(false);

                UpdateChatMessageContent(assistantIndex, response);
                _status = string.Empty;
            }
            catch (OperationCanceledException)
            {
                UpdateChatMessageContent(assistantIndex, "Canceled.");
                _status = "Canceled.";
            }
            catch (Exception ex)
            {
                UpdateChatMessageContent(assistantIndex, "Error getting response.");
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

        private async Task RunAgentLoopAsync(string userGoal)
        {
            if (string.IsNullOrWhiteSpace(userGoal) || _isAgentLoopActive)
            {
                return;
            }

            var trimmedGoal = userGoal.Trim();
            var isContinuation = IsContinuationPrompt(trimmedGoal);
            var effectiveGoal = trimmedGoal;
            if (isContinuation && !string.IsNullOrWhiteSpace(_lastAgentGoal))
            {
                effectiveGoal = _lastAgentGoal;
            }
            else
            {
                _lastAgentGoal = trimmedGoal;
                _agentHistory.Clear();
                _agentReadMemory.Clear();
                _agentToolCallHistory.Clear();
            }

            ResetAgentStatus();
            _isAgentLoopActive = true;
            _status = "Agent loop running...";
            _cancelRequested = false;
            var token = BeginOperation("agent loop");
            _apiWaitTimeoutSeconds = 320;
            var displayGoal = isContinuation && !string.Equals(trimmedGoal, effectiveGoal, StringComparison.OrdinalIgnoreCase)
                ? $"Continue: {effectiveGoal}"
                : trimmedGoal;
            AddChatMessage(new ProtoChatMessage { role = RoleUser, content = displayGoal });
            await MaybeCompactSessionAsync(token).ConfigureAwait(false);
            Repaint();

            var toolHistory = _agentHistory;
            var completed = false;

            try
            {
                for (var iteration = 0; iteration < _agentMaxIterations; iteration++)
                {
                    token.ThrowIfCancellationRequested();
                    await RunOnMainThread(() =>
                    {
                        _agentIteration = iteration + 1;
                        _agentLastIterations = _agentIteration;
                        _toolStatus = $"Agent iteration {iteration + 1}/{_agentMaxIterations}...";
                        Repaint();
                    }).ConfigureAwait(false);

                    var action = await RequestAgentActionAsync(effectiveGoal, iteration + 1, _agentMaxIterations, toolHistory, token).ConfigureAwait(false);
                    if (action == null || string.IsNullOrWhiteSpace(action.action))
                    {
                        await AppendAgentMessageAsync("Agent response was invalid. Stopping.").ConfigureAwait(false);
                        await RunOnMainThread(() => SetAgentOutcome("Invalid response", _agentLastIterations))
                            .ConfigureAwait(false);
                        completed = true;
                        break;
                    }

                    var result = await ExecuteAgentActionAsync(action, token).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(result.historyEntry))
                    {
                        AppendAgentHistory(toolHistory, result.historyEntry);
                        MarkChatHistoryDirty();
                    }

                    if (result.stop)
                    {
                        var outcome = string.IsNullOrWhiteSpace(result.outcomeLabel)
                            ? "Completed"
                            : result.outcomeLabel;
                        await RunOnMainThread(() => SetAgentOutcome(outcome, _agentLastIterations))
                            .ConfigureAwait(false);
                        completed = true;
                        break;
                    }
                }

                if (!completed)
                {
                    await AppendAgentMessageAsync("Agent loop reached the max iteration limit. Type 'continue' to resume.").ConfigureAwait(false);
                    await RunOnMainThread(() => SetAgentOutcome("Reached iteration limit", _agentLastIterations))
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                await AppendAgentMessageAsync("Agent loop canceled.").ConfigureAwait(false);
                await RunOnMainThread(() => SetAgentOutcome("Canceled", _agentLastIterations)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await AppendAgentMessageAsync($"Agent loop error: {ex.Message}").ConfigureAwait(false);
                await RunOnMainThread(() => SetAgentOutcome($"Error: {ex.Message}", _agentLastIterations))
                    .ConfigureAwait(false);
            }
            finally
            {
                await RunOnMainThread(() =>
                {
                    _isAgentLoopActive = false;
                    _agentIteration = 0;
                    _status = string.Empty;
                    EndApiWait();
                    EndOperation();
                    _agentLoopTask = null;
                    Repaint();
                }).ConfigureAwait(false);
            }
        }

        private void ResetAgentStatus()
        {
            _agentIteration = 0;
            _agentLastIterations = 0;
            _agentLastOutcome = string.Empty;
            _agentLastOutcomeTime = string.Empty;
        }

        private void SetAgentOutcome(string outcome, int iterations)
        {
            var clamped = ClampStatusLine(outcome, AgentStatusMaxChars);
            _agentLastOutcome = clamped;
            _agentLastOutcomeTime = string.IsNullOrWhiteSpace(clamped) ? string.Empty : DateTime.Now.ToString("HH:mm:ss");
            _agentLastIterations = Mathf.Max(_agentLastIterations, iterations);
        }

        private async Task<ProtoAgentAction> RequestAgentActionAsync(
            string userGoal,
            int iteration,
            int maxIterations,
            List<string> toolHistory,
            CancellationToken token)
        {
            var baseMessages = await RunOnMainThread(() => BuildBaseContextMessages(true).ToArray()).ConfigureAwait(false);
            var messages = new List<ProtoChatMessage>(baseMessages ?? Array.Empty<ProtoChatMessage>());
            messages.Add(new ProtoChatMessage
            {
                role = "system",
                content = ProtoPrompts.AgentLoopSystemInstruction
            });
            messages.Add(new ProtoChatMessage
            {
                role = "system",
                content = ProtoPrompts.AgentLoopToolSchema
            });
            messages.Add(new ProtoChatMessage
            {
                role = "user",
                content = string.Format(ProtoPrompts.AgentLoopGoalFormat, userGoal)
            });
            messages.Add(new ProtoChatMessage
            {
                role = "user",
                content = string.Format(ProtoPrompts.AgentLoopIterationFormat, iteration, maxIterations)
            });

            var recentChat = BuildRecentChatContext(_messages, 6);
            if (!string.IsNullOrWhiteSpace(recentChat))
            {
                messages.Add(new ProtoChatMessage
                {
                    role = "user",
                    content = string.Format(ProtoPrompts.AgentLoopChatHistoryFormat, recentChat)
                });
            }

            if (toolHistory != null && toolHistory.Count > 0)
            {
                var historyText = string.Join("\n\n", toolHistory);
                messages.Add(new ProtoChatMessage
                {
                    role = "user",
                    content = string.Format(ProtoPrompts.AgentLoopHistoryFormat, historyText)
                });
            }

            var diagnostics = await RunOnMainThread(CollectDiagnostics).ConfigureAwait(false);
            if (diagnostics != null && diagnostics.Length > 0)
            {
                messages.Add(new ProtoChatMessage
                {
                    role = "user",
                    content = string.Format(ProtoPrompts.AgentLoopDiagnosticsFormat, string.Join("\n", diagnostics))
                });
            }

            var settings = await RunOnMainThread(GetSettingsSnapshot).ConfigureAwait(false);
            if (settings == null || string.IsNullOrWhiteSpace(settings.ApiKey))
            {
                return new ProtoAgentAction
                {
                    action = "respond",
                    message = "Missing API key."
                };
            }

            await RunOnMainThread(() => BeginApiWait($"agent {iteration}/{maxIterations}")).ConfigureAwait(false);
            var response = await ProtoProviderClient.SendChatAsync(
                settings,
                messages.ToArray(),
                _apiWaitTimeoutSeconds,
                token).ConfigureAwait(false);
            await RunOnMainThread(EndApiWait).ConfigureAwait(false);

            if (TryParseAgentAction(response, out var action))
            {
                return action;
            }

            var retryMessages = new List<ProtoChatMessage>(messages)
            {
                new ProtoChatMessage
                {
                    role = "system",
                    content = ProtoPrompts.AgentLoopRetryInstruction
                },
                new ProtoChatMessage
                {
                    role = "user",
                    content = string.Format(ProtoPrompts.AgentLoopRetryResponseFormat, ClampToolOutput(response, AgentMaxToolOutputChars))
                }
            };

            await RunOnMainThread(() => BeginApiWait($"agent retry {iteration}/{maxIterations}")).ConfigureAwait(false);
            var retryResponse = await ProtoProviderClient.SendChatAsync(
                settings,
                retryMessages.ToArray(),
                _apiWaitTimeoutSeconds,
                token).ConfigureAwait(false);
            await RunOnMainThread(EndApiWait).ConfigureAwait(false);

            if (TryParseAgentAction(retryResponse, out var retryAction))
            {
                return retryAction;
            }

            await AppendAgentMessageAsync($"Agent response invalid. Raw response:\n{ClampToolOutput(retryResponse, AgentMaxToolOutputChars)}")
                .ConfigureAwait(false);
            return null;
        }

        private async Task<AgentActionResult> ExecuteAgentActionAsync(ProtoAgentAction action, CancellationToken token)
        {
            var actionName = NormalizeAgentActionName(action.action);
            switch (actionName)
            {
                case "read_file":
                    return new AgentActionResult
                    {
                        historyEntry = await AgentReadFileAsync(action, token).ConfigureAwait(false)
                    };
                case "write_file":
                    return new AgentActionResult
                    {
                        historyEntry = await AgentWriteFileAsync(action.path, action.content, token).ConfigureAwait(false)
                    };
                case "list_folder":
                    return new AgentActionResult
                    {
                        historyEntry = await AgentListFolderAsync(action.path, token).ConfigureAwait(false)
                    };
                case "search_text":
                    return new AgentActionResult
                    {
                        historyEntry = await AgentSearchTextAsync(action.path, action.query, token).ConfigureAwait(false)
                    };
                case "apply_plan":
                    await ApplyPlanInternalAsync(token, false).ConfigureAwait(false);
                    return new AgentActionResult
                    {
                        historyEntry = BuildAgentStatusHistory("Apply Plan", _toolStatus)
                    };
                case "apply_stage":
                    if (!TryResolveAgentStage(action.stage, out var label, out var types))
                    {
                        return new AgentActionResult
                        {
                            historyEntry = BuildAgentErrorHistory("Apply Stage", $"Unknown stage \"{action.stage}\".")
                        };
                    }
                    await ApplyPlanStageInternalAsync(label, types, token, false).ConfigureAwait(false);
                    return new AgentActionResult
                    {
                        historyEntry = BuildAgentStatusHistory($"Apply Stage ({label})", _toolStatus)
                    };
                case "fix_pass":
                    var scope = NormalizeAgentToken(action.scope);
                    HashSet<string> targets = null;
                    var passLabel = string.Empty;
                    if (string.Equals(scope, "last_stage", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(scope, "last", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(scope, "previous_stage", StringComparison.OrdinalIgnoreCase))
                    {
                        targets = _lastFixPassTargets;
                        passLabel = "last stage";
                    }
                    else if (string.Equals(scope, "all", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(scope, "all_scripts", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(scope, "all_errors", StringComparison.OrdinalIgnoreCase))
                    {
                        targets = new HashSet<string>();
                        passLabel = "all scripts";
                    }
                    else if (!string.IsNullOrWhiteSpace(scope))
                    {
                        return new AgentActionResult
                        {
                            historyEntry = BuildAgentErrorHistory("Fix Pass", $"Unknown scope \"{action.scope}\".")
                        };
                    }
                    await RunFixPassInternalAsync(targets, passLabel, token, false).ConfigureAwait(false);
                    return new AgentActionResult
                    {
                        historyEntry = BuildAgentStatusHistory("Fix Pass", _toolStatus)
                    };
                case "scene_edit":
                    return new AgentActionResult
                    {
                        historyEntry = await AgentEditSceneAsync(action, token).ConfigureAwait(false)
                    };
                case "respond":
                    if (AgentResponseRequestsUserInput(action.message))
                    {
                        var needsContext = string.IsNullOrWhiteSpace(action.message)
                            ? "Agent needs more context to continue."
                            : action.message.Trim();
                        await AppendAgentMessageAsync(needsContext).ConfigureAwait(false);
                        return new AgentActionResult { stop = true, outcomeLabel = "Needs input" };
                    }
                    var response = string.IsNullOrWhiteSpace(action.message) ? "Done." : action.message.Trim();
                    await AppendAgentMessageAsync(response).ConfigureAwait(false);
                    var outcome = string.Equals(response, "Missing API key.", StringComparison.OrdinalIgnoreCase)
                        ? response
                        : "Completed";
                    return new AgentActionResult { stop = true, outcomeLabel = outcome };
                case "stop":
                    await AppendAgentMessageAsync("Agent stopped.").ConfigureAwait(false);
                    return new AgentActionResult { stop = true, outcomeLabel = "Stopped by agent" };
                default:
                    await AppendAgentMessageAsync($"Agent returned unknown action: {action.action}.").ConfigureAwait(false);
                    var actionLabel = string.IsNullOrWhiteSpace(action.action) ? "Unknown action" : $"Unknown action: {action.action}";
                    return new AgentActionResult { stop = true, outcomeLabel = actionLabel };
            }
        }

        private async Task<string> AgentReadFileAsync(ProtoAgentAction action, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var path = action?.path;
            if (string.IsNullOrWhiteSpace(path))
            {
                return BuildAgentErrorHistory("Read File", "Missing path.");
            }

            var absolutePath = ResolveAbsolutePath(path);
            var relativePath = BuildRelativePath(absolutePath);
            if (!File.Exists(absolutePath))
            {
                return BuildAgentErrorHistory("Read File", $"File not found: {relativePath}.");
            }

            var hasRange = TryResolveLineRange(action, out var lineStart, out var lineEnd);
            var rangeLabel = hasRange ? $"{lineStart}-{lineEnd}" : "full";
            var readKey = BuildAgentReadKey(absolutePath, rangeLabel);
            if (_agentReadMemory.TryGetValue(readKey, out var snapshot))
            {
                var hint = snapshot.truncated && !hasRange
                    ? "Already read full file (truncated). Use a line range."
                    : "Already read this file range.";
                return BuildAgentStatusHistory("Read File", $"{relativePath} ({rangeLabel}): {hint}");
            }

            string content;
            try
            {
                if (hasRange)
                {
                    var lines = File.ReadAllLines(absolutePath);
                    if (lines.Length == 0)
                    {
                        content = string.Empty;
                    }
                    else
                    {
                        var startIndex = Mathf.Clamp(lineStart - 1, 0, lines.Length - 1);
                        var endIndex = Mathf.Clamp(lineEnd - 1, startIndex, lines.Length - 1);
                        var builder = new StringBuilder();
                        for (var i = startIndex; i <= endIndex; i++)
                        {
                            builder.Append(i + 1);
                            builder.Append(": ");
                            builder.AppendLine(lines[i]);
                        }
                        content = builder.ToString();
                    }
                }
                else
                {
                    content = File.ReadAllText(absolutePath);
                }
            }
            catch (Exception ex)
            {
                return BuildAgentErrorHistory("Read File", ex.Message);
            }

            var truncated = content.Length > AgentMaxToolOutputChars && AgentMaxToolOutputChars > 0;
            var preview = ClampToolOutput(content, AgentMaxToolOutputChars);
            _agentReadMemory[readKey] = new AgentReadSnapshot { truncated = truncated };
            var displayPath = hasRange ? $"{relativePath} ({rangeLabel})" : relativePath;
            await RunOnMainThread(() => AppendToolMessage("Read File", displayPath, preview)).ConfigureAwait(false);
            return BuildAgentToolHistoryEntry("Read File", displayPath, preview);
        }

        private async Task<string> AgentWriteFileAsync(string path, string content, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(path))
            {
                return BuildAgentErrorHistory("Write File", "Missing path.");
            }

            if (content == null)
            {
                return BuildAgentErrorHistory("Write File", "Missing content.");
            }

            var absolutePath = ResolveAbsolutePath(path);
            var relativePath = BuildRelativePath(absolutePath);
            var shouldWrite = await RunOnMainThread(() =>
            {
                if (ProtoToolSettings.GetAutoConfirm() || ProtoToolSettings.GetFullAgentMode())
                {
                    return true;
                }

                return EditorUtility.DisplayDialog(
                    "Confirm Write",
                    $"Overwrite {relativePath}?",
                    "Overwrite",
                    "Cancel");
            }).ConfigureAwait(false);

            if (!shouldWrite)
            {
                return BuildAgentStatusHistory("Write File", "Canceled.");
            }

            await RunOnMainThread(() =>
            {
                var directory = Path.GetDirectoryName(absolutePath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(absolutePath, content);
                AssetDatabase.Refresh();
            }).ConfigureAwait(false);

            InvalidateAgentReadMemory(absolutePath);

            var preview = ClampToolOutput(content, AgentMaxToolOutputChars);
            await RunOnMainThread(() => AppendToolMessage("Write File", relativePath, preview)).ConfigureAwait(false);
            return BuildAgentToolHistoryEntry("Write File", relativePath, preview);
        }

        private async Task<string> AgentListFolderAsync(string path, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var targetPath = string.IsNullOrWhiteSpace(path) ? Application.dataPath : ResolveAbsolutePath(path);
            var relativePath = string.IsNullOrWhiteSpace(path) ? "Assets" : BuildRelativePath(targetPath);
            if (!Directory.Exists(targetPath))
            {
                return BuildAgentErrorHistory("List Folder", $"Folder not found: {relativePath}.");
            }

            var listKey = BuildAgentToolKey("list", targetPath, null);
            if (_agentToolCallHistory.Contains(listKey))
            {
                return BuildAgentStatusHistory("List Folder", $"{relativePath}: already listed.");
            }
            _agentToolCallHistory.Add(listKey);

            var entries = Directory.GetFileSystemEntries(targetPath);
            var previewList = new List<string>(Math.Min(entries.Length, 20));
            for (var i = 0; i < entries.Length && previewList.Count < 20; i++)
            {
                previewList.Add(BuildRelativePath(entries[i]));
            }

            var body = previewList.Count == 0 ? "(empty)" : string.Join("\n", previewList);
            await RunOnMainThread(() => AppendToolMessage("List Folder", relativePath, body)).ConfigureAwait(false);
            return BuildAgentToolHistoryEntry("List Folder", relativePath, ClampToolOutput(body, AgentMaxToolOutputChars));
        }

        private async Task<string> AgentSearchTextAsync(string path, string query, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var term = (query ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(term))
            {
                return BuildAgentErrorHistory("Search Text", "Missing query.");
            }

            var root = string.IsNullOrWhiteSpace(path) ? Application.dataPath : ResolveAbsolutePath(path);
            var relativePath = string.IsNullOrWhiteSpace(path) ? "Assets" : BuildRelativePath(root);
            var searchRoot = Directory.Exists(root) ? root : Application.dataPath;
            var searchKey = BuildAgentToolKey("search", searchRoot, term);
            if (_agentToolCallHistory.Contains(searchKey))
            {
                return BuildAgentStatusHistory("Search Text", $"{relativePath}: already searched for \"{term}\".");
            }
            _agentToolCallHistory.Add(searchKey);

            var matches = new List<string>();
            await Task.Run(() =>
            {
                foreach (var file in Directory.EnumerateFiles(searchRoot, "*.*", SearchOption.AllDirectories))
                {
                    if (IsIgnoredSearchPath(file))
                    {
                        continue;
                    }

                    if (file.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string fileContent;
                    try
                    {
                        fileContent = File.ReadAllText(file);
                    }
                    catch
                    {
                        continue;
                    }

                    if (fileContent.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        matches.Add(BuildRelativePath(file));
                        if (matches.Count >= 12)
                        {
                            break;
                        }
                    }
                }
            }).ConfigureAwait(false);

            var body = matches.Count == 0 ? "(no matches)" : string.Join("\n", matches);
            await RunOnMainThread(() => AppendToolMessage("Search Text", relativePath, body)).ConfigureAwait(false);
            return BuildAgentToolHistoryEntry("Search Text", relativePath, ClampToolOutput(body, AgentMaxToolOutputChars));
        }

        private async Task<string> AgentEditSceneAsync(ProtoAgentAction action, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (action == null)
            {
                return BuildAgentErrorHistory("Scene Edit", "Missing action.");
            }

            var edits = CollectSceneEdits(action);
            if (edits.Count == 0)
            {
                return BuildAgentErrorHistory("Scene Edit", "No edits provided.");
            }

            var sceneKey = string.IsNullOrWhiteSpace(action.scene) ? action.path : action.scene;
            var shouldApply = await RunOnMainThread(() =>
            {
                if (ProtoToolSettings.GetAutoConfirm() || ProtoToolSettings.GetFullAgentMode())
                {
                    return true;
                }

                var label = string.IsNullOrWhiteSpace(sceneKey) ? "Active Scene" : sceneKey;
                return EditorUtility.DisplayDialog(
                    "Confirm Scene Edit",
                    $"Apply {edits.Count} edit(s) to scene \"{label}\"?",
                    "Apply",
                    "Cancel");
            }).ConfigureAwait(false);

            if (!shouldApply)
            {
                return BuildAgentStatusHistory("Scene Edit", "Canceled by user.");
            }

            string resolvedScenePath = null;
            string summary = null;
            string error = null;

            await RunOnMainThread(() =>
            {
                if (string.IsNullOrWhiteSpace(sceneKey))
                {
                    var activeScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                    if (activeScene.IsValid() && !string.IsNullOrWhiteSpace(activeScene.path))
                    {
                        sceneKey = activeScene.path;
                    }
                }

                var scenePath = ResolveScenePathForAgent(sceneKey);
                if (string.IsNullOrWhiteSpace(scenePath))
                {
                    error = "Scene not found. Provide a scene name or path.";
                    return;
                }

                resolvedScenePath = scenePath;

                if (!EnsureAdditiveSceneCreationPossible(out var setupError))
                {
                    error = setupError;
                    return;
                }

                UnityEngine.SceneManagement.Scene scene;
                var previousScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                var openedByTool = false;
                try
                {
                    scene = UnityEngine.SceneManagement.SceneManager.GetSceneByPath(scenePath);
                    if (!scene.IsValid() || !scene.isLoaded)
                    {
                        scene = UnityEditor.SceneManagement.EditorSceneManager.OpenScene(
                            scenePath,
                            UnityEditor.SceneManagement.OpenSceneMode.Additive);
                        openedByTool = true;
                    }
                }
                catch (Exception ex)
                {
                    error = $"Failed to open scene: {ex.Message}";
                    return;
                }

                var results = new List<string>();
                var errors = new List<string>();
                UnityEngine.SceneManagement.SceneManager.SetActiveScene(scene);

                try
                {
                    foreach (var edit in edits)
                    {
                        if (TryApplySceneEditOperation(edit, scene, out var result, out var editError))
                        {
                            if (!string.IsNullOrWhiteSpace(result))
                            {
                                results.Add(result);
                            }
                        }
                        else if (!string.IsNullOrWhiteSpace(editError))
                        {
                            errors.Add(editError);
                        }
                    }

                    if (results.Count == 0 && errors.Count > 0)
                    {
                        error = string.Join("\n", errors);
                        return;
                    }

                    UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
                    UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene);
                    summary = results.Count == 0 ? "No scene changes applied." : string.Join("; ", results);
                    if (errors.Count > 0)
                    {
                        summary = $"{summary} (with {errors.Count} error(s))";
                    }
                }
                finally
                {
                    if (previousScene.IsValid() && previousScene != scene)
                    {
                        UnityEngine.SceneManagement.SceneManager.SetActiveScene(previousScene);
                    }

                    if (openedByTool)
                    {
                        UnityEditor.SceneManagement.EditorSceneManager.CloseScene(scene, true);
                    }
                }
            }).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(error))
            {
                return BuildAgentErrorHistory("Scene Edit", error);
            }

            var relativePath = string.IsNullOrWhiteSpace(resolvedScenePath)
                ? "Scene"
                : BuildRelativePath(resolvedScenePath);
            var message = string.IsNullOrWhiteSpace(summary) ? "Scene edit complete." : summary;
            await RunOnMainThread(() => AppendToolMessage("Scene Edit", relativePath, message)).ConfigureAwait(false);
            return BuildAgentToolHistoryEntry("Scene Edit", relativePath, ClampToolOutput(message, AgentMaxToolOutputChars));
        }

        private static List<ProtoSceneEditOperation> CollectSceneEdits(ProtoAgentAction action)
        {
            var edits = new List<ProtoSceneEditOperation>();
            if (action == null)
            {
                return edits;
            }

            if (action.edits != null)
            {
                foreach (var edit in action.edits)
                {
                    if (edit != null)
                    {
                        edits.Add(edit);
                    }
                }
            }

            if (action.edit != null)
            {
                edits.Add(action.edit);
            }

            return edits;
        }

        private static string ResolveScenePathForAgent(string sceneKey)
        {
            if (string.IsNullOrWhiteSpace(sceneKey))
            {
                return string.Empty;
            }

            var normalized = sceneKey.Trim().Replace('\\', '/');
            if (Path.IsPathRooted(normalized))
            {
                normalized = BuildRelativePath(normalized);
            }

            if (normalized.Contains("/") || normalized.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
            {
                var path = normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                    ? normalized
                    : EnsureProjectPath(normalized);
                if (!path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase) && !AssetDatabase.IsValidFolder(path))
                {
                    path += ".unity";
                }

                return AssetDatabase.LoadAssetAtPath<SceneAsset>(path) != null ? path : string.Empty;
            }

            var name = Path.GetFileNameWithoutExtension(normalized);
            if (string.IsNullOrWhiteSpace(name))
            {
                return string.Empty;
            }

            var guids = AssetDatabase.FindAssets($"{name} t:Scene", new[] { "Assets" });
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.Equals(Path.GetFileNameWithoutExtension(path), name, StringComparison.OrdinalIgnoreCase))
                {
                    return path;
                }
            }

            return string.Empty;
        }

        private static bool TryApplySceneEditOperation(
            ProtoSceneEditOperation edit,
            UnityEngine.SceneManagement.Scene scene,
            out string result,
            out string error)
        {
            result = string.Empty;
            error = string.Empty;
            if (edit == null)
            {
                error = "Edit was null.";
                return false;
            }

            var type = NormalizeAgentToken(edit.type);
            switch (type)
            {
                case "add_light":
                    return TryAddSceneLight(edit, scene, out result, out error);
                case "set_light":
                    return TrySetSceneLight(edit, scene, out result, out error);
                case "add_gameobject":
                    return TryAddSceneGameObject(edit, scene, out result, out error);
                case "add_prefab":
                    return TryAddScenePrefab(edit, scene, out result, out error);
                case "add_component":
                    return TryAddSceneComponent(edit, scene, out result, out error);
                case "set_component_field":
                    return TrySetSceneComponentField(edit, scene, out result, out error);
                case "set_transform":
                    return TrySetSceneTransform(edit, scene, out result, out error);
                case "delete_object":
                    return TryDeleteSceneObject(edit, scene, out result, out error);
                default:
                    error = $"Unknown edit type \"{edit.type}\".";
                    return false;
            }
        }

        private static bool TryAddSceneLight(
            ProtoSceneEditOperation edit,
            UnityEngine.SceneManagement.Scene scene,
            out string result,
            out string error)
        {
            result = string.Empty;
            error = string.Empty;

            var name = string.IsNullOrWhiteSpace(edit.name) ? "SceneLight" : edit.name.Trim();
            var go = new GameObject(name);
            AssignSceneParent(go, scene, edit.parent);

            ApplyTransformEdits(go.transform, edit, scene);

            var light = go.AddComponent<Light>();
            light.type = ParseLightType(edit.lightType, LightType.Spot);
            if (edit.intensity > 0f)
            {
                light.intensity = edit.intensity;
            }

            if (edit.range > 0f)
            {
                light.range = edit.range;
            }

            if (edit.spotAngle > 0f)
            {
                light.spotAngle = edit.spotAngle;
            }

            if (TryParseColor(edit.color, out var color))
            {
                light.color = color;
            }

            result = $"add_light {name}";
            return true;
        }

        private static bool TrySetSceneLight(
            ProtoSceneEditOperation edit,
            UnityEngine.SceneManagement.Scene scene,
            out string result,
            out string error)
        {
            result = string.Empty;
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(edit.name))
            {
                error = "set_light requires a target name.";
                return false;
            }

            var target = FindInSceneByName(scene, edit.name.Trim());
            if (target == null)
            {
                error = $"Light target not found: {edit.name}.";
                return false;
            }

            var light = target.GetComponent<Light>();
            if (light == null)
            {
                error = $"No Light component on {target.name}.";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(edit.lightType))
            {
                light.type = ParseLightType(edit.lightType, light.type);
            }

            if (edit.intensity > 0f)
            {
                light.intensity = edit.intensity;
            }

            if (edit.range > 0f)
            {
                light.range = edit.range;
            }

            if (edit.spotAngle > 0f)
            {
                light.spotAngle = edit.spotAngle;
            }

            if (TryParseColor(edit.color, out var color))
            {
                light.color = color;
            }

            result = $"set_light {target.name}";
            return true;
        }

        private static bool TryAddSceneGameObject(
            ProtoSceneEditOperation edit,
            UnityEngine.SceneManagement.Scene scene,
            out string result,
            out string error)
        {
            result = string.Empty;
            error = string.Empty;
            var name = string.IsNullOrWhiteSpace(edit.name) ? "GameObject" : edit.name.Trim();
            var go = new GameObject(name);
            AssignSceneParent(go, scene, edit.parent);
            ApplyTransformEdits(go.transform, edit, scene);
            result = $"add_gameobject {name}";
            return true;
        }

        private static bool TryAddScenePrefab(
            ProtoSceneEditOperation edit,
            UnityEngine.SceneManagement.Scene scene,
            out string result,
            out string error)
        {
            result = string.Empty;
            error = string.Empty;
            var prefabToken = string.IsNullOrWhiteSpace(edit.prefab) ? string.Empty : edit.prefab.Trim();
            if (string.IsNullOrWhiteSpace(prefabToken))
            {
                error = "add_prefab requires a prefab path or name.";
                return false;
            }

            if (!TryResolvePrefabAsset(prefabToken, out var prefabAsset, out var resolvedPath, out var resolveError))
            {
                error = resolveError;
                return false;
            }

            GameObject instance = null;
            try
            {
                instance = PrefabUtility.InstantiatePrefab(prefabAsset, scene) as GameObject;
            }
            catch
            {
                instance = UnityEngine.Object.Instantiate(prefabAsset);
            }

            if (instance == null)
            {
                error = $"Failed to instantiate prefab: {prefabToken}.";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(edit.name))
            {
                instance.name = edit.name.Trim();
            }

            AssignSceneParent(instance, scene, edit.parent);
            ApplyTransformEdits(instance.transform, edit, scene);

            var label = string.IsNullOrWhiteSpace(resolvedPath) ? instance.name : $"{instance.name} ({resolvedPath})";
            result = $"add_prefab {label}";
            return true;
        }

        private static bool TryAddSceneComponent(
            ProtoSceneEditOperation edit,
            UnityEngine.SceneManagement.Scene scene,
            out string result,
            out string error)
        {
            result = string.Empty;
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(edit.name))
            {
                error = "add_component requires a target name.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(edit.component))
            {
                error = "add_component requires a component type.";
                return false;
            }

            var target = FindInSceneByName(scene, edit.name.Trim());
            if (target == null)
            {
                error = $"Target not found: {edit.name}.";
                return false;
            }

            if (!TryResolveComponentType(edit.component.Trim(), out var componentType, out var resolveError))
            {
                error = resolveError;
                return false;
            }

            var existing = target.GetComponent(componentType);
            if (existing != null)
            {
                result = $"add_component {target.name} {componentType.Name} (already present)";
                return true;
            }

            target.AddComponent(componentType);
            result = $"add_component {target.name} {componentType.Name}";
            return true;
        }

        private static bool TrySetSceneComponentField(
            ProtoSceneEditOperation edit,
            UnityEngine.SceneManagement.Scene scene,
            out string result,
            out string error)
        {
            result = string.Empty;
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(edit.name))
            {
                error = "set_component_field requires a target name.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(edit.component))
            {
                error = "set_component_field requires a component type.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(edit.field))
            {
                error = "set_component_field requires a field name.";
                return false;
            }

            var target = FindInSceneByName(scene, edit.name.Trim());
            if (target == null)
            {
                error = $"Target not found: {edit.name}.";
                return false;
            }

            if (!TryResolveComponentType(edit.component.Trim(), out var componentType, out var resolveError))
            {
                error = resolveError;
                return false;
            }

            var component = target.GetComponent(componentType);
            if (component == null)
            {
                error = $"Component {componentType.Name} not found on {target.name}.";
                return false;
            }

            if (!TryResolveFieldOrProperty(componentType, edit.field.Trim(), out var field, out var property))
            {
                error = $"Field or property not found: {edit.field}.";
                return false;
            }

            var fieldType = field != null ? field.FieldType : property.PropertyType;
            if (!TryConvertSceneFieldValue(fieldType, edit, scene, out var value, out var valueError))
            {
                error = valueError;
                return false;
            }

            Undo.RecordObject(component, "Proto Scene Edit");
            try
            {
                if (field != null)
                {
                    field.SetValue(component, value);
                }
                else
                {
                    property.SetValue(component, value);
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            EditorUtility.SetDirty(component);
            result = $"set_component_field {target.name} {componentType.Name}.{field?.Name ?? property.Name}";
            return true;
        }

        private static bool TrySetSceneTransform(
            ProtoSceneEditOperation edit,
            UnityEngine.SceneManagement.Scene scene,
            out string result,
            out string error)
        {
            result = string.Empty;
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(edit.name))
            {
                error = "set_transform requires a target name.";
                return false;
            }

            var target = FindInSceneByName(scene, edit.name.Trim());
            if (target == null)
            {
                error = $"Target not found: {edit.name}.";
                return false;
            }

            ApplyTransformEdits(target.transform, edit, scene);
            result = $"set_transform {target.name}";
            return true;
        }

        private static bool TryDeleteSceneObject(
            ProtoSceneEditOperation edit,
            UnityEngine.SceneManagement.Scene scene,
            out string result,
            out string error)
        {
            result = string.Empty;
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(edit.name))
            {
                error = "delete_object requires a target name.";
                return false;
            }

            var target = FindInSceneByName(scene, edit.name.Trim());
            if (target == null)
            {
                error = $"Target not found: {edit.name}.";
                return false;
            }

            UnityEngine.Object.DestroyImmediate(target);
            result = $"delete_object {edit.name.Trim()}";
            return true;
        }

        private static void AssignSceneParent(GameObject go, UnityEngine.SceneManagement.Scene scene, string parentName)
        {
            if (go == null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(parentName))
            {
                if (go.scene != scene && go.transform.parent == null)
                {
                    UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(go, scene);
                }
                return;
            }

            var parent = FindInSceneByName(scene, parentName.Trim());
            if (parent != null)
            {
                go.transform.SetParent(parent.transform);
                return;
            }

            if (go.scene != scene && go.transform.parent == null)
            {
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(go, scene);
            }
        }

        private static void ApplyTransformEdits(
            Transform transform,
            ProtoSceneEditOperation edit,
            UnityEngine.SceneManagement.Scene scene)
        {
            if (transform == null || edit == null)
            {
                return;
            }

            if (TryParseVector3(edit.position, out var position))
            {
                transform.position = position;
            }

            if (TryParseVector3(edit.rotation, out var rotation))
            {
                transform.rotation = Quaternion.Euler(rotation);
            }

            if (TryParseVector3(edit.scale, out var scale))
            {
                transform.localScale = scale;
            }

            var targetName = edit.target;
            if (!string.IsNullOrWhiteSpace(targetName))
            {
                var target = FindInSceneByName(scene, targetName.Trim());
                if (target != null)
                {
                    if (!TryParseVector3(edit.position, out position) && TryParseVector3(edit.offset, out var offset))
                    {
                        transform.position = target.transform.position + offset;
                    }

                    if (!TryParseVector3(edit.rotation, out rotation))
                    {
                        transform.LookAt(target.transform.position);
                    }
                }
            }
        }

        private static bool TryParseVector3(float[] values, out Vector3 result)
        {
            result = Vector3.zero;
            if (values == null || values.Length < 3)
            {
                return false;
            }

            result = new Vector3(values[0], values[1], values[2]);
            return true;
        }

        private static bool TryParseVector2(float[] values, out Vector2 result)
        {
            result = Vector2.zero;
            if (values == null || values.Length < 2)
            {
                return false;
            }

            result = new Vector2(values[0], values[1]);
            return true;
        }

        private static bool TryParseColor(float[] values, out Color result)
        {
            result = Color.white;
            if (values == null || values.Length < 3)
            {
                return false;
            }

            var r = Mathf.Clamp01(values[0]);
            var g = Mathf.Clamp01(values[1]);
            var b = Mathf.Clamp01(values[2]);
            var a = values.Length >= 4 ? Mathf.Clamp01(values[3]) : 1f;
            result = new Color(r, g, b, a);
            return true;
        }

        private static bool TryParseVector3(string text, out Vector3 result)
        {
            result = Vector3.zero;
            if (!TryParseFloatList(text, 3, out var values))
            {
                return false;
            }

            result = new Vector3(values[0], values[1], values[2]);
            return true;
        }

        private static bool TryParseVector2(string text, out Vector2 result)
        {
            result = Vector2.zero;
            if (!TryParseFloatList(text, 2, out var values))
            {
                return false;
            }

            result = new Vector2(values[0], values[1]);
            return true;
        }

        private static bool TryParseColor(string text, out Color result)
        {
            result = Color.white;
            if (!TryParseFloatList(text, 3, out var values))
            {
                return false;
            }

            var r = Mathf.Clamp01(values[0]);
            var g = Mathf.Clamp01(values[1]);
            var b = Mathf.Clamp01(values[2]);
            var a = values.Length >= 4 ? Mathf.Clamp01(values[3]) : 1f;
            result = new Color(r, g, b, a);
            return true;
        }

        private static bool TryParseFloatList(string text, int minCount, out float[] values)
        {
            values = Array.Empty<float>();
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            var cleaned = text.Trim().Trim('(', ')', '[', ']', '{', '}');
            var parts = cleaned.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var list = new List<float>(parts.Length);
            foreach (var part in parts)
            {
                if (float.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                {
                    list.Add(value);
                }
            }

            if (list.Count < minCount)
            {
                return false;
            }

            values = list.ToArray();
            return true;
        }

        private static LightType ParseLightType(string value, LightType fallback)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return fallback;
            }

            var normalized = NormalizeAgentToken(value);
            if (normalized.Contains("directional") || normalized.Contains("dir"))
            {
                return LightType.Directional;
            }

            if (normalized.Contains("point"))
            {
                return LightType.Point;
            }

            if (normalized.Contains("spot"))
            {
                return LightType.Spot;
            }

            if (normalized.Contains("area"))
            {
                return LightType.Rectangle;
            }

            return fallback;
        }

        private static bool TryResolvePrefabAsset(
            string prefabToken,
            out GameObject prefab,
            out string resolvedPath,
            out string error)
        {
            prefab = null;
            resolvedPath = string.Empty;
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(prefabToken))
            {
                error = "Prefab name is missing.";
                return false;
            }

            var normalized = prefabToken.Trim().Replace('\\', '/');
            if (normalized.Contains("/") || normalized.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                var path = normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                    ? normalized
                    : $"Assets/{normalized.TrimStart('/')}";
                if (!path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                {
                    path += ".prefab";
                }

                prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null)
                {
                    error = $"Prefab not found at {path}.";
                    return false;
                }

                resolvedPath = path;
                return true;
            }

            var guids = AssetDatabase.FindAssets($"{normalized} t:prefab", new[] { "Assets" });
            if (guids.Length == 0)
            {
                error = $"Prefab not found: {normalized}.";
                return false;
            }

            var firstPath = AssetDatabase.GUIDToAssetPath(guids[0]);
            prefab = AssetDatabase.LoadAssetAtPath<GameObject>(firstPath);
            if (prefab == null)
            {
                error = $"Prefab not found at {firstPath}.";
                return false;
            }

            resolvedPath = guids.Length > 1 ? $"{firstPath} (first match)" : firstPath;
            return true;
        }

        private static bool TryResolveComponentType(string componentName, out Type componentType, out string error)
        {
            componentType = null;
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(componentName))
            {
                error = "Component name is missing.";
                return false;
            }

            if (TryResolveComponentTypeName(componentName, out var resolvedName))
            {
                componentType = FindTypeByName(resolvedName);
            }
            else
            {
                componentType = FindTypeByName(componentName);
                if (!IsValidComponentType(componentType) &&
                    TryResolveAbstractComponentFallback(componentType, componentName, out var fallbackName))
                {
                    componentType = FindTypeByName(fallbackName);
                }
            }

            if (!IsValidComponentType(componentType))
            {
                error = $"Component not found or invalid: {componentName}.";
                return false;
            }

            return true;
        }

        private static bool TryResolveFieldOrProperty(
            Type componentType,
            string fieldName,
            out FieldInfo field,
            out PropertyInfo property)
        {
            field = null;
            property = null;
            if (componentType == null || string.IsNullOrWhiteSpace(fieldName))
            {
                return false;
            }

            var binding = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (var candidate in componentType.GetFields(binding))
            {
                if (string.Equals(candidate.Name, fieldName, StringComparison.OrdinalIgnoreCase))
                {
                    field = candidate;
                    return true;
                }
            }

            foreach (var candidate in componentType.GetProperties(binding))
            {
                if (!candidate.CanWrite)
                {
                    continue;
                }

                if (string.Equals(candidate.Name, fieldName, StringComparison.OrdinalIgnoreCase))
                {
                    property = candidate;
                    return true;
                }
            }

            return false;
        }

        private static bool TryConvertSceneFieldValue(
            Type fieldType,
            ProtoSceneEditOperation edit,
            UnityEngine.SceneManagement.Scene scene,
            out object value,
            out string error)
        {
            value = null;
            error = string.Empty;
            if (fieldType == null)
            {
                error = "Field type is missing.";
                return false;
            }

            var rawValue = edit == null ? string.Empty : edit.value;
            var reference = string.IsNullOrWhiteSpace(edit?.reference) ? rawValue : edit.reference;
            var vectorValues = edit?.vector;
            var colorValues = edit?.color;

            if (fieldType == typeof(string))
            {
                value = rawValue ?? string.Empty;
                return true;
            }

            if (fieldType == typeof(int))
            {
                if (TryParseInt(rawValue, out var intValue))
                {
                    value = intValue;
                    return true;
                }

                error = $"Invalid int value: {rawValue}.";
                return false;
            }

            if (fieldType == typeof(float))
            {
                if (TryParseFloat(rawValue, out var floatValue))
                {
                    value = floatValue;
                    return true;
                }

                error = $"Invalid float value: {rawValue}.";
                return false;
            }

            if (fieldType == typeof(double))
            {
                if (TryParseFloat(rawValue, out var doubleValue))
                {
                    value = (double)doubleValue;
                    return true;
                }

                error = $"Invalid double value: {rawValue}.";
                return false;
            }

            if (fieldType == typeof(bool))
            {
                if (TryParseBool(rawValue, out var boolValue))
                {
                    value = boolValue;
                    return true;
                }

                error = $"Invalid bool value: {rawValue}.";
                return false;
            }

            if (fieldType == typeof(Vector3))
            {
                if (TryParseVector3(vectorValues, out var vector) || TryParseVector3(rawValue, out vector))
                {
                    value = vector;
                    return true;
                }

                error = $"Invalid Vector3 value: {rawValue}.";
                return false;
            }

            if (fieldType == typeof(Vector2))
            {
                if (TryParseVector2(vectorValues, out var vector2) || TryParseVector2(rawValue, out vector2))
                {
                    value = vector2;
                    return true;
                }

                error = $"Invalid Vector2 value: {rawValue}.";
                return false;
            }

            if (fieldType == typeof(Color))
            {
                if (TryParseColor(colorValues, out var color) || TryParseColor(rawValue, out color))
                {
                    value = color;
                    return true;
                }

                error = $"Invalid Color value: {rawValue}.";
                return false;
            }

            if (fieldType == typeof(LayerMask))
            {
                if (TryParseInt(rawValue, out var maskValue))
                {
                    value = (LayerMask)maskValue;
                    return true;
                }

                error = $"Invalid LayerMask value: {rawValue}.";
                return false;
            }

            if (fieldType.IsEnum)
            {
                if (!string.IsNullOrWhiteSpace(rawValue) &&
                    Enum.TryParse(fieldType, rawValue.Trim(), true, out var enumValue))
                {
                    value = enumValue;
                    return true;
                }

                if (TryParseInt(rawValue, out var enumInt))
                {
                    value = Enum.ToObject(fieldType, enumInt);
                    return true;
                }

                error = $"Invalid enum value: {rawValue}.";
                return false;
            }

            if (typeof(GameObject).IsAssignableFrom(fieldType))
            {
                if (TryResolveSceneObject(reference, scene, out var obj))
                {
                    value = obj;
                    return true;
                }

                error = $"GameObject not found: {reference}.";
                return false;
            }

            if (typeof(Transform).IsAssignableFrom(fieldType))
            {
                if (TryResolveSceneObject(reference, scene, out var obj))
                {
                    value = obj.transform;
                    return true;
                }

                error = $"Transform not found: {reference}.";
                return false;
            }

            if (typeof(Component).IsAssignableFrom(fieldType))
            {
                if (TryResolveSceneComponent(fieldType, reference, scene, out var component))
                {
                    value = component;
                    return true;
                }

                error = $"Component not found: {reference}.";
                return false;
            }

            if (typeof(UnityEngine.Object).IsAssignableFrom(fieldType))
            {
                if (!string.IsNullOrWhiteSpace(rawValue) &&
                    rawValue.Trim().StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                {
                    var asset = AssetDatabase.LoadAssetAtPath(rawValue.Trim(), fieldType);
                    if (asset != null)
                    {
                        value = asset;
                        return true;
                    }
                }

                error = $"Unsupported object reference value: {rawValue}.";
                return false;
            }

            error = $"Unsupported field type: {fieldType.Name}.";
            return false;
        }

        private static bool TryResolveSceneObject(
            string name,
            UnityEngine.SceneManagement.Scene scene,
            out GameObject result)
        {
            result = null;
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            var obj = FindInSceneByName(scene, name.Trim());
            if (obj == null)
            {
                return false;
            }

            result = obj;
            return true;
        }

        private static bool TryResolveSceneComponent(
            Type componentType,
            string name,
            UnityEngine.SceneManagement.Scene scene,
            out Component component)
        {
            component = null;
            if (componentType == null || string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            var obj = FindInSceneByName(scene, name.Trim());
            if (obj != null)
            {
                component = obj.GetComponent(componentType);
                if (component != null)
                {
                    return true;
                }
            }

            var matches = new List<Component>();
            var roots = scene.GetRootGameObjects();
            foreach (var root in roots)
            {
                if (root == null)
                {
                    continue;
                }

                matches.AddRange(root.GetComponentsInChildren(componentType, true));
            }

            if (matches.Count == 1)
            {
                component = matches[0];
                return true;
            }

            return false;
        }

        private static bool TryParseFloat(string text, out float value)
        {
            return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static bool TryParseInt(string text, out int value)
        {
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        private static bool TryParseBool(string text, out bool value)
        {
            if (bool.TryParse(text, out value))
            {
                return true;
            }

            if (string.Equals(text, "1", StringComparison.OrdinalIgnoreCase))
            {
                value = true;
                return true;
            }

            if (string.Equals(text, "0", StringComparison.OrdinalIgnoreCase))
            {
                value = false;
                return true;
            }

            return false;
        }

        private static void AppendAgentHistory(List<string> history, string entry)
        {
            if (history == null || string.IsNullOrWhiteSpace(entry))
            {
                return;
            }

            history.Add(entry.Trim());
            while (history.Count > AgentMaxHistoryEntries)
            {
                history.RemoveAt(0);
            }
        }

        private static string ClampToolOutput(string content, int maxChars)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return string.Empty;
            }

            if (content.Length <= maxChars || maxChars <= 0)
            {
                return content;
            }

            return $"{content.Substring(0, maxChars)}\n... (truncated)";
        }

        private static bool TryResolveLineRange(ProtoAgentAction action, out int lineStart, out int lineEnd)
        {
            lineStart = 0;
            lineEnd = 0;
            if (action == null)
            {
                return false;
            }

            lineStart = action.lineStart;
            lineEnd = action.lineEnd;

            if (lineStart > 0 && lineEnd >= lineStart)
            {
                return true;
            }

            if (TryParseLineRange(action.range, out var parsedStart, out var parsedEnd))
            {
                lineStart = parsedStart;
                lineEnd = parsedEnd;
                return true;
            }

            return false;
        }

        private static bool TryParseLineRange(string range, out int start, out int end)
        {
            start = 0;
            end = 0;
            if (string.IsNullOrWhiteSpace(range))
            {
                return false;
            }

            var normalized = range.Trim();
            var match = Regex.Match(normalized, @"^(?<start>\d+)\s*[-:\.]{1,2}\s*(?<end>\d+)$");
            if (!match.Success)
            {
                return false;
            }

            if (!int.TryParse(match.Groups["start"].Value, out start) ||
                !int.TryParse(match.Groups["end"].Value, out end))
            {
                return false;
            }

            if (start <= 0 || end < start)
            {
                return false;
            }

            return true;
        }

        private static bool TryParseAgentAction(string response, out ProtoAgentAction action)
        {
            action = null;
            if (string.IsNullOrWhiteSpace(response))
            {
                return false;
            }

            var json = ExtractJson(response);
            if (!string.IsNullOrWhiteSpace(json))
            {
                action = TryParseAgentActionJson(json);
                if (IsValidAgentAction(action))
                {
                    return true;
                }

                var sanitized = SanitizeAgentJson(json);
                if (!string.Equals(sanitized, json, StringComparison.Ordinal))
                {
                    action = TryParseAgentActionJson(sanitized);
                    if (IsValidAgentAction(action))
                    {
                        return true;
                    }
                }
            }

            var fallbackInput = string.IsNullOrWhiteSpace(json) ? response : json;
            return TryParseAgentActionFallback(fallbackInput, out action);
        }

        private static ProtoAgentAction TryParseAgentActionJson(string json)
        {
            try
            {
                return JsonUtility.FromJson<ProtoAgentAction>(json);
            }
            catch
            {
                return null;
            }
        }

        private static bool TryParseAgentActionFallback(string input, out ProtoAgentAction action)
        {
            action = null;
            if (string.IsNullOrWhiteSpace(input))
            {
                return false;
            }

            var actionValue = ExtractLooseJsonStringValue(input, "action");
            if (string.IsNullOrWhiteSpace(actionValue))
            {
                return false;
            }

            action = new ProtoAgentAction
            {
                action = actionValue,
                path = ExtractLooseJsonStringValue(input, "path"),
                content = ExtractLooseJsonStringValue(input, "content", true),
                query = ExtractLooseJsonStringValue(input, "query"),
                stage = ExtractLooseJsonStringValue(input, "stage"),
                scope = ExtractLooseJsonStringValue(input, "scope"),
                message = ExtractLooseJsonStringValue(input, "message"),
                range = ExtractLooseJsonStringValue(input, "range"),
                lineStart = ExtractLooseJsonIntValue(input, "lineStart", "line_start", "start"),
                lineEnd = ExtractLooseJsonIntValue(input, "lineEnd", "line_end", "end")
            };
            return true;
        }

        private static bool IsValidAgentAction(ProtoAgentAction action)
        {
            return action != null && !string.IsNullOrWhiteSpace(action.action);
        }

        private static string SanitizeAgentJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return string.Empty;
            }

            var cleaned = Regex.Replace(json, ",\\s*(\\}|\\])", "$1");
            if (cleaned.IndexOf('\"') < 0 && cleaned.IndexOf('\'') >= 0)
            {
                cleaned = cleaned.Replace('\'', '\"');
            }
            return cleaned;
        }

        private static string ExtractLooseJsonStringValue(string input, string key, bool requireQuotes = false)
        {
            if (string.IsNullOrWhiteSpace(input) || string.IsNullOrWhiteSpace(key))
            {
                return string.Empty;
            }

            string pattern;
            if (requireQuotes)
            {
                pattern = $"(?i)(?:\\\"|')?{Regex.Escape(key)}(?:\\\"|')?\\s*:\\s*(?:\\\"(?<value>(?:\\\\.|[^\\\"\\\\])*)\\\"|'(?<value>(?:\\\\.|[^'\\\\])*)')";
            }
            else
            {
                pattern = $"(?i)(?:\\\"|')?{Regex.Escape(key)}(?:\\\"|')?\\s*:\\s*(?:\\\"(?<value>(?:\\\\.|[^\\\"\\\\])*)\\\"|'(?<value>(?:\\\\.|[^'\\\\])*)'|(?<value>[^,\\r\\n\\}}]+))";
            }

            var match = Regex.Match(input, pattern);
            if (!match.Success)
            {
                return string.Empty;
            }

            var value = match.Groups["value"].Value.Trim();
            if (value.IndexOf("\\", StringComparison.Ordinal) >= 0)
            {
                try
                {
                    value = Regex.Unescape(value);
                }
                catch
                {
                    // Leave as-is if unescape fails.
                }
            }

            return value.Trim();
        }

        private static int ExtractLooseJsonIntValue(string input, params string[] keys)
        {
            if (string.IsNullOrWhiteSpace(input) || keys == null || keys.Length == 0)
            {
                return 0;
            }

            foreach (var key in keys)
            {
                var value = ExtractLooseJsonStringValue(input, key);
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                if (int.TryParse(value.Trim(), out var parsed))
                {
                    return parsed;
                }
            }

            return 0;
        }

        private static string BuildAgentToolHistoryEntry(string title, string relativePath, string content)
        {
            var builder = new StringBuilder();
            builder.AppendLine($"[{title}] {relativePath}");
            if (!string.IsNullOrWhiteSpace(content))
            {
                builder.AppendLine(content.Trim());
            }
            return builder.ToString().Trim();
        }

        private static string BuildAgentStatusHistory(string title, string message)
        {
            var summary = string.IsNullOrWhiteSpace(message) ? "Completed." : message.Trim();
            return $"[{title}] {summary}";
        }

        private static string BuildAgentErrorHistory(string title, string message)
        {
            var summary = string.IsNullOrWhiteSpace(message) ? "Unknown error." : message.Trim();
            return $"[{title}] Error: {summary}";
        }

        private string BuildAgentReadKey(string absolutePath, string rangeLabel)
        {
            var normalized = string.IsNullOrWhiteSpace(absolutePath)
                ? string.Empty
                : Path.GetFullPath(absolutePath).Replace('\\', '/');
            var range = string.IsNullOrWhiteSpace(rangeLabel) ? "full" : rangeLabel.Trim();
            return $"{normalized}|{range}";
        }

        private string BuildAgentToolKey(string tool, string absolutePath, string query)
        {
            var normalizedPath = string.IsNullOrWhiteSpace(absolutePath)
                ? string.Empty
                : Path.GetFullPath(absolutePath).Replace('\\', '/');
            var normalizedQuery = string.IsNullOrWhiteSpace(query) ? string.Empty : query.Trim().ToLowerInvariant();
            return $"{tool}|{normalizedPath}|{normalizedQuery}";
        }

        private void InvalidateAgentReadMemory(string absolutePath)
        {
            if (string.IsNullOrWhiteSpace(absolutePath) || _agentReadMemory.Count == 0)
            {
                return;
            }

            var normalized = Path.GetFullPath(absolutePath).Replace('\\', '/');
            var prefix = $"{normalized}|";
            var toRemove = new List<string>();
            foreach (var key in _agentReadMemory.Keys)
            {
                if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    toRemove.Add(key);
                }
            }

            foreach (var key in toRemove)
            {
                _agentReadMemory.Remove(key);
            }
        }

        private static bool IsContinuationPrompt(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return false;
            }

            var normalized = NormalizeAgentToken(input);
            return string.Equals(normalized, "continue", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalized, "continuar", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalized, "continua", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalized, "sigue", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalized, "seguir", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalized, "resume", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalized, "retomar", StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildRecentChatContext(List<ProtoChatMessage> messages, int maxMessages)
        {
            if (messages == null || messages.Count == 0 || maxMessages <= 0)
            {
                return string.Empty;
            }

            var collected = new List<string>();
            for (var i = messages.Count - 1; i >= 0 && collected.Count < maxMessages; i--)
            {
                var message = messages[i];
                if (message == null || string.IsNullOrWhiteSpace(message.content))
                {
                    continue;
                }

                if (IsToolMessage(message.content) || message.content == "..." || message.content.StartsWith("Agent loop", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var prefix = string.Equals(message.role, RoleUser, StringComparison.OrdinalIgnoreCase) ? "User" : "Assistant";
                collected.Add($"{prefix}: {message.content.Trim()}");
            }

            if (collected.Count == 0)
            {
                return string.Empty;
            }

            collected.Reverse();
            return string.Join("\n", collected);
        }

        private static bool IsToolMessage(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return false;
            }

            return content.StartsWith("[Read File]", StringComparison.Ordinal) ||
                   content.StartsWith("[Write File]", StringComparison.Ordinal) ||
                   content.StartsWith("[List Folder]", StringComparison.Ordinal) ||
                   content.StartsWith("[Search Text]", StringComparison.Ordinal);
        }

        private async Task MaybeCompactSessionAsync(CancellationToken token)
        {
            if (_isCompactingSession || !ShouldCompactSession())
            {
                return;
            }

            await CompactSessionAsync(token).ConfigureAwait(false);
        }

        private bool ShouldCompactSession()
        {
            if (_messages == null || _messages.Count == 0)
            {
                return false;
            }

            return CountConversationMessages() >= SessionCompactionThreshold;
        }

        private int CountConversationMessages()
        {
            var count = 0;
            foreach (var message in _messages)
            {
                if (message == null || string.IsNullOrWhiteSpace(message.content))
                {
                    continue;
                }

                if (IsToolMessage(message.content) ||
                    message.content == "..." ||
                    message.content.StartsWith("Agent loop", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                count++;
            }

            return count;
        }

        private async Task CompactSessionAsync(CancellationToken token)
        {
            _isCompactingSession = true;
            try
            {
                await RunOnMainThread(() =>
                {
                    _toolStatus = "Compacting session...";
                    _status = "Compacting session...";
                    Repaint();
                }).ConfigureAwait(false);

                var settings = await RunOnMainThread(GetSettingsSnapshot).ConfigureAwait(false);
                if (settings == null || string.IsNullOrWhiteSpace(settings.ApiKey))
                {
                    await RunOnMainThread(() => { _toolStatus = "Skipping compaction: missing API key."; }).ConfigureAwait(false);
                    return;
                }

                var messages = BuildCompactionMessages();
                if (messages.Count == 0)
                {
                    return;
                }

                messages.Add(new ProtoChatMessage
                {
                    role = "user",
                    content = ProtoPrompts.SessionCompactionInstruction
                });

                await RunOnMainThread(() => BeginApiWait("session compaction")).ConfigureAwait(false);
                var response = await ProtoProviderClient.SendChatAsync(
                    settings,
                    messages.ToArray(),
                    _apiWaitTimeoutSeconds,
                    token).ConfigureAwait(false);
                await RunOnMainThread(EndApiWait).ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(response))
                {
                    _sessionSummary = response.Trim();
                    TrimSessionAfterCompaction();
                    MarkChatHistoryDirty();
                }
            }
            catch (OperationCanceledException)
            {
                // Ignore cancellation.
            }
            catch (Exception ex)
            {
                await RunOnMainThread(() => { _toolStatus = $"Compaction failed: {ex.Message}"; }).ConfigureAwait(false);
            }
            finally
            {
                await RunOnMainThread(() =>
                {
                    _isCompactingSession = false;
                    _status = string.Empty;
                    if (_toolStatus == "Compacting session...")
                    {
                        _toolStatus = string.Empty;
                    }
                    EndApiWait();
                    Repaint();
                }).ConfigureAwait(false);
            }
        }

        private List<ProtoChatMessage> BuildCompactionMessages()
        {
            var baseMessages = BuildBaseContextMessages(false);
            var messages = new List<ProtoChatMessage>(baseMessages);
            var conversation = ExtractConversationMessages();
            messages.AddRange(conversation);
            return messages;
        }

        private List<ProtoChatMessage> ExtractConversationMessages()
        {
            var results = new List<ProtoChatMessage>();
            var charCount = 0;

            for (var i = _messages.Count - 1; i >= 0; i--)
            {
                var message = _messages[i];
                if (message == null || string.IsNullOrWhiteSpace(message.content))
                {
                    continue;
                }

                if (IsToolMessage(message.content) ||
                    message.content == "..." ||
                    message.content.StartsWith("Agent loop", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var length = message.content.Length;
                if (results.Count > 0 && charCount + length > SessionCompactionMaxChars)
                {
                    break;
                }

                charCount += length;
                results.Add(message);
            }

            results.Reverse();
            return results;
        }

        private void TrimSessionAfterCompaction()
        {
            if (_messages.Count <= SessionCompactionKeepMessages)
            {
                return;
            }

            var trimmed = new List<ProtoChatMessage>();
            for (var i = _messages.Count - 1; i >= 0 && trimmed.Count < SessionCompactionKeepMessages; i--)
            {
                trimmed.Add(_messages[i]);
            }

            trimmed.Reverse();
            _messages.Clear();
            _messages.AddRange(trimmed);
        }

        private static bool IsIgnoredSearchPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return true;
            }

            var normalized = path.Replace('\\', '/');
            return normalized.IndexOf("/Library/", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   normalized.IndexOf("/Temp/", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   normalized.IndexOf("/Obj/", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   normalized.IndexOf("/Build/", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   normalized.IndexOf("/Logs/", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   normalized.IndexOf("/UserSettings/", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   normalized.IndexOf("/ProjectSettings/", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   normalized.IndexOf("/Packages/", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool AgentResponseRequestsUserInput(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return false;
            }

            var lower = message.ToLowerInvariant();
            if (lower.Contains("?"))
            {
                return true;
            }

            if (lower.Contains("necesito") ||
                lower.Contains("need to") ||
                lower.Contains("need the") ||
                lower.Contains("i need") ||
                lower.Contains("no puedo") ||
                lower.Contains("cannot") ||
                lower.Contains("can't") ||
                lower.Contains("if you want") ||
                lower.Contains("si quieres") ||
                lower.Contains("please") ||
                lower.Contains("por favor") ||
                lower.Contains("pasame") ||
                lower.Contains("pasa el") ||
                lower.Contains("copia") ||
                lower.Contains("copy") ||
                lower.Contains("pega") ||
                lower.Contains("paste") ||
                lower.Contains("abre") ||
                lower.Contains("open"))
            {
                return true;
            }

            return false;
        }

        private static string NormalizeAgentActionName(string action)
        {
            var normalized = NormalizeAgentToken(action);
            switch (normalized)
            {
                case "applyplan":
                    return "apply_plan";
                case "applystage":
                    return "apply_stage";
                case "readfile":
                    return "read_file";
                case "writefile":
                    return "write_file";
                case "listfolder":
                    return "list_folder";
                case "searchtext":
                    return "search_text";
                case "fixpass":
                    return "fix_pass";
                case "sceneedit":
                    return "scene_edit";
                default:
                    return normalized;
            }
        }

        private static string NormalizeAgentToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var normalized = value.Trim().ToLowerInvariant();
            normalized = normalized.Replace('-', '_');
            normalized = Regex.Replace(normalized, "\\s+", "_");
            return normalized;
        }

        private static bool TryResolveAgentStage(string stage, out string label, out string[] types)
        {
            label = null;
            types = null;

            if (string.IsNullOrWhiteSpace(stage))
            {
                return false;
            }

            var normalized = NormalizeAgentToken(stage);
            if (normalized.Contains("folder"))
            {
                label = "folders";
                types = new[] { "folder" };
                return true;
            }
            if (normalized.Contains("script"))
            {
                label = "scripts";
                types = new[] { "script" };
                return true;
            }
            if (normalized.Contains("material"))
            {
                label = "materials";
                types = new[] { "material" };
                return true;
            }
            if (normalized.Contains("prefab"))
            {
                label = "prefabs";
                types = new[] { "prefab", "prefab_component" };
                return true;
            }
            if (normalized.Contains("scene"))
            {
                label = "scenes";
                types = new[] { "scene", "scene_prefab", "scene_manager" };
                return true;
            }
            if (normalized.Contains("asset"))
            {
                label = "assets";
                types = new[] { "asset" };
                return true;
            }

            return false;
        }

        private Task AppendAgentMessageAsync(string message)
        {
            var content = string.IsNullOrWhiteSpace(message) ? "Done." : message.Trim();
            return RunOnMainThread(() =>
            {
                AddChatMessage(new ProtoChatMessage
                {
                    role = RoleAssistant,
                    content = content
                });
                Repaint();
            });
        }

        private async Task ReviewScriptAsync(string userText)
        {
            _isSending = true;
            _status = "Reviewing script...";
            _cancelRequested = false;
            var token = BeginOperation("review");
            _apiWaitTimeoutSeconds = 320;

            AddChatMessage(new ProtoChatMessage { role = RoleUser, content = userText });
            var assistantIndex = _messages.Count;
            AddChatMessage(new ProtoChatMessage { role = RoleAssistant, content = "..." });
            Repaint();

            try
            {
                var settings = await RunOnMainThread(GetSettingsSnapshot).ConfigureAwait(false);
                if (settings == null || string.IsNullOrWhiteSpace(settings.ApiKey))
                {
                    UpdateChatMessageContent(assistantIndex, "Missing API key.");
                    _status = "Missing API key.";
                    return;
                }

                var resolution = await RunOnMainThread(() => ResolveScriptPath(userText)).ConfigureAwait(false);
                if (!resolution.success)
                {
                    UpdateChatMessageContent(assistantIndex, resolution.error);
                    _status = resolution.error;
                    return;
                }

                var assetPath = resolution.assetPath;
                var fullPath = Path.GetFullPath(assetPath);
                if (!File.Exists(fullPath))
                {
                    UpdateChatMessageContent(assistantIndex, $"Script not found at {assetPath}.");
                    _status = "Script not found.";
                    return;
                }

                var original = File.ReadAllText(fullPath);
                var baseMessages = await RunOnMainThread(() => BuildBaseContextMessages(false).ToArray()).ConfigureAwait(false);

                var messages = new List<ProtoChatMessage>(baseMessages ?? Array.Empty<ProtoChatMessage>());
                messages.Add(new ProtoChatMessage
                {
                    role = "user",
                    content = ProtoPrompts.ReviewScriptInstruction
                });
                messages.Add(new ProtoChatMessage
                {
                    role = "user",
                    content = string.Format(ProtoPrompts.ReviewScriptPayloadFormat, assetPath, original)
                });

                await RunOnMainThread(() => BeginApiWait($"review {Path.GetFileName(assetPath)}")).ConfigureAwait(false);
                var response = await ProtoProviderClient.SendChatAsync(settings, messages.ToArray(), _apiWaitTimeoutSeconds, token).ConfigureAwait(false);
                await RunOnMainThread(EndApiWait).ConfigureAwait(false);

                var summary = ExtractReviewSummary(response);
                var code = ExtractCode(response);

                if (string.IsNullOrWhiteSpace(code))
                {
                    if (response.IndexOf("NO_CHANGES", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        UpdateChatMessageContent(assistantIndex, string.IsNullOrWhiteSpace(summary)
                            ? "NO_CHANGES."
                            : summary);
                        _status = string.Empty;
                        return;
                    }

                    UpdateChatMessageContent(assistantIndex, "Review failed: no code returned.");
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

                UpdateChatMessageContent(assistantIndex, summary);
                _status = string.Empty;
            }
            catch (OperationCanceledException)
            {
                UpdateChatMessageContent(assistantIndex, "Canceled.");
                _status = "Canceled.";
            }
            catch (Exception ex)
            {
                UpdateChatMessageContent(assistantIndex, "Error reviewing script.");
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

            var projectGoal = ProtoProjectSettings.GetProjectGoal();
            if (!string.IsNullOrWhiteSpace(projectGoal))
            {
                messages.Add(new ProtoChatMessage
                {
                    role = "system",
                    content = string.Format(ProtoPrompts.ProjectGoalFormat, projectGoal.Trim())
                });
            }

            var summary = ProtoProjectSettings.GetProjectSummary();
            if (string.IsNullOrWhiteSpace(summary))
            {
                summary = ProtoProjectContext.BuildProjectSummary();
                ProtoProjectSettings.SetProjectSummary(summary);
            }

            if (!string.IsNullOrWhiteSpace(summary))
            {
                messages.Add(new ProtoChatMessage
                {
                    role = "system",
                    content = string.Format(ProtoPrompts.ProjectSummaryFormat, summary.Trim())
                });
            }

            if (!string.IsNullOrWhiteSpace(_sessionSummary))
            {
                messages.Add(new ProtoChatMessage
                {
                    role = "system",
                    content = string.Format(ProtoPrompts.SessionSummaryFormat, _sessionSummary.Trim())
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
                        content = string.Format(ProtoPrompts.ContextSnapshotFormat, dynamicContext.Trim())
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
            return Path.GetFullPath(ProtoPlanStorage.GetPlanRawPath());
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
                    ? ProtoPrompts.DefaultPlanPrompt
                    : _planPrompt.Trim();

                var scriptIndex = await RunOnMainThread(BuildScriptIndexMarkdown).ConfigureAwait(false);
                var prefabIndex = await RunOnMainThread(BuildPrefabIndexMarkdown).ConfigureAwait(false);
                var sceneIndex = await RunOnMainThread(BuildSceneIndexMarkdown).ConfigureAwait(false);
                var assetIndex = await RunOnMainThread(BuildAssetIndexMarkdown).ConfigureAwait(false);
                var baseMessages = await RunOnMainThread(() => BuildBaseContextMessages(true).ToArray()).ConfigureAwait(false);
                var phasePlan = await GeneratePhasedPlanAsync(settings, baseMessages, prompt, token, scriptIndex, prefabIndex, sceneIndex, assetIndex).ConfigureAwait(false);

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
                    featureRequests = ExpandFeatureSteps(featureRequests);
                    featureRequests = FilterExistingScriptRequests(featureRequests);
                    featureRequests = ExpandPrefabAndSceneDetails(featureRequests);
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
                    featureRequests = ExpandFeatureSteps(featureRequests);
                    featureRequests = FilterExistingScriptRequests(featureRequests);
                    featureRequests = ExpandPrefabAndSceneDetails(featureRequests);
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
            var token = BeginOperation("apply plan");
            await ApplyPlanInternalAsync(token, true).ConfigureAwait(false);
        }

        private async Task ApplyPlanInternalAsync(CancellationToken token, bool ownsOperation)
        {
            _isApplyingPlan = true;
            _toolStatus = "Applying feature requests...";
            _cancelRequested = false;
            _lastFixPassTargets = null;
            _lastFixPassLabel = string.Empty;
            if (ownsOperation)
            {
                Repaint();
            }

            try
            {
                var featureRequests = await RunOnMainThread(LoadFeatureRequestsFromDisk).ConfigureAwait(false);
                if (featureRequests == null || featureRequests.Count == 0)
                {
                    _toolStatus = "No feature requests found. Create them first.";
                    _isApplyingPlan = false;
                    if (ownsOperation)
                    {
                        EndOperation();
                        Repaint();
                    }
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
                    if (ownsOperation)
                    {
                        EndOperation();
                    }
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
                    if (ownsOperation)
                    {
                        EndOperation();
                    }
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
                    if (ownsOperation)
                    {
                        EndOperation();
                    }
                    Repaint();
                };
            }
        }

        private async Task RunFixPassAsync(HashSet<string> targetScriptPaths = null, string passLabel = null)
        {
            var token = BeginOperation("fix pass");
            await RunFixPassInternalAsync(targetScriptPaths, passLabel, token, true).ConfigureAwait(false);
        }

        private async Task RunFixPassInternalAsync(HashSet<string> targetScriptPaths, string passLabel, CancellationToken token, bool ownsOperation)
        {
            var effectiveTargets = targetScriptPaths ?? _lastFixPassTargets;
            var effectiveLabel = !string.IsNullOrWhiteSpace(passLabel)
                ? passLabel
                : (targetScriptPaths == null ? _lastFixPassLabel : string.Empty);
            _isFixingErrors = true;
            var labelSuffix = string.IsNullOrWhiteSpace(effectiveLabel) ? string.Empty : $" ({effectiveLabel})";
            _toolStatus = $"Fix pass{labelSuffix} running...";
            _cancelRequested = false;
            _apiWaitTimeoutSeconds = 320;
            if (ownsOperation)
            {
                Repaint();
            }

            try
            {
                var targetPaths = effectiveTargets != null && effectiveTargets.Count > 0
                    ? new HashSet<string>(effectiveTargets, StringComparer.OrdinalIgnoreCase)
                    : null;

                for (var iteration = 0; iteration < _fixPassIterations; iteration++)
                {
                    token.ThrowIfCancellationRequested();
                    await WaitForCompilationAsync(token).ConfigureAwait(false);
                    var errors = await RunOnMainThread(GetConsoleErrorItems).ConfigureAwait(false);
                    if (targetPaths != null)
                    {
                        errors = FilterErrorsByScriptPaths(errors, targetPaths);
                    }
                    if (errors.Count == 0)
                    {
                        _toolStatus = targetPaths == null
                            ? "Fix pass complete. No console errors."
                            : $"Fix pass{labelSuffix} complete. No errors in target scripts.";
                        break;
                    }

                    _toolStatus = $"Fixing errors{labelSuffix} (pass {iteration + 1}/{_fixPassIterations})...";
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

                        await FixScriptErrorAsync(error, settings, token, scriptIndex).ConfigureAwait(false);
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
                if (ownsOperation)
                {
                    EndOperation();
                }
                Repaint();
            }
        }

        private async Task FixScriptErrorAsync(ConsoleErrorItem error, ProtoProviderSnapshot settings, CancellationToken token, string scriptIndex)
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

            var fixContext = await RunOnMainThread(() => BuildFixPassContext(error)).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(fixContext))
            {
                messages.Add(new ProtoChatMessage
                {
                    role = "system",
                    content = string.Format(ProtoPrompts.FixContextFormat, fixContext)
                });
            }

            messages.Add(new ProtoChatMessage
            {
                role = "system",
                content = ProtoPrompts.FixAutoModeInstruction
            });

            messages.Add(new ProtoChatMessage
            {
                role = "system",
                content = ProtoPrompts.FixUnityConstraints
            });

            messages.Add(new ProtoChatMessage
            {
                role = "user",
                content = ProtoPrompts.FixScriptInstruction
            });
            messages.Add(new ProtoChatMessage
            {
                role = "user",
                content = string.Format(ProtoPrompts.FixErrorPayloadFormat, error.message, assetPath, error.line, original)
            });

            await RunOnMainThread(() => BeginApiWait($"fix {Path.GetFileName(assetPath)}")).ConfigureAwait(false);
            var response = await ProtoProviderClient.SendChatAsync(
                settings,
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
                var typeValue = typeField?.GetValue(entry) as string ?? string.Empty;

                results.Add(new ConsoleErrorItem
                {
                    message = message,
                    filePath = file,
                    line = line,
                    type = typeValue
                });
            }

            return results;
        }

        private static List<ConsoleErrorItem> FilterErrorsByScriptPaths(List<ConsoleErrorItem> errors, HashSet<string> targetScriptPaths)
        {
            if (errors == null || errors.Count == 0 || targetScriptPaths == null || targetScriptPaths.Count == 0)
            {
                return errors ?? new List<ConsoleErrorItem>();
            }

            var results = new List<ConsoleErrorItem>();
            foreach (var error in errors)
            {
                var assetPath = NormalizeErrorAssetPath(error.filePath);
                if (string.IsNullOrWhiteSpace(assetPath))
                {
                    continue;
                }

                if (targetScriptPaths.Contains(assetPath))
                {
                    results.Add(error);
                }
            }

            return results;
        }

        private struct ConsoleErrorItem
        {
            public string message;
            public string filePath;
            public int line;
            public string type;
        }

        private async Task ApplyPlanStageAsync(string label, string[] types)
        {
            var token = BeginOperation($"apply {label}");
            await ApplyPlanStageInternalAsync(label, types, token, true).ConfigureAwait(false);
        }

        private async Task ApplyPlanStageInternalAsync(string label, string[] types, CancellationToken token, bool ownsOperation)
        {
            _isApplyingStage = true;
            _toolStatus = $"Applying {label}...";
            _cancelRequested = false;
            if (ownsOperation)
            {
                Repaint();
            }

            try
            {
                var featureRequests = await RunOnMainThread(LoadFeatureRequestsFromDisk).ConfigureAwait(false);
                if (featureRequests == null || featureRequests.Count == 0)
                {
                    _toolStatus = "No feature requests found. Create them first.";
                    _isApplyingStage = false;
                    if (ownsOperation)
                    {
                        EndOperation();
                        Repaint();
                    }
                    return;
                }

                featureRequests = await RunOnMainThread(() => PreflightRequestsForExecution(featureRequests)).ConfigureAwait(false);
                _scriptIndexDirty = true;
                var requestLookup = BuildRequestLookup(featureRequests);
                var scriptByName = BuildScriptRequestMap(featureRequests);

                var phaseId = GetSelectedPhaseId();
                var phaseLabel = GetSelectedPhaseLabel();
                var phaseRequests = FilterRequestsByPhase(featureRequests, phaseId);
                if (phaseRequests.Count == 0)
                {
                    var phaseMessage = !string.IsNullOrWhiteSpace(phaseLabel)
                        ? $" for {phaseLabel}"
                        : (!string.IsNullOrWhiteSpace(phaseId) ? " for the selected phase" : string.Empty);
                    _toolStatus = $"No feature requests found{phaseMessage}.";
                    _isApplyingStage = false;
                    if (ownsOperation)
                    {
                        EndOperation();
                        Repaint();
                    }
                    return;
                }

                var stageRequests = FilterRequestsByTypes(phaseRequests, types);
                if (stageRequests.Count == 0)
                {
                    var phaseSuffix = !string.IsNullOrWhiteSpace(phaseLabel)
                        ? $" in {phaseLabel}"
                        : (!string.IsNullOrWhiteSpace(phaseId) ? " in the selected phase" : string.Empty);
                    _toolStatus = $"No {label} requests found{phaseSuffix}.";
                    _isApplyingStage = false;
                    if (ownsOperation)
                    {
                        EndOperation();
                        Repaint();
                    }
                    return;
                }

                var stageScriptTargets = CollectStageScriptPaths(stageRequests, requestLookup, scriptByName);
                var shouldApplyStage = await RunOnMainThread(() =>
                {
                    if (ProtoToolSettings.GetFullAgentMode())
                    {
                        return true;
                    }

                    var phaseDescription = !string.IsNullOrWhiteSpace(phaseLabel)
                        ? phaseLabel
                        : (!string.IsNullOrWhiteSpace(phaseId) ? $"phase {phaseId}" : "all phases");
                    var stageDescription = $"{label} stage for {phaseDescription}";
                    return EditorUtility.DisplayDialog(
                        "Confirm Stage",
                        $"Apply {stageDescription}? This may modify assets in your project.",
                        "Apply",
                        "Cancel");
                }).ConfigureAwait(false);

                if (!shouldApplyStage)
                {
                    await RunOnMainThread(() =>
                    {
                        _toolStatus = "Stage canceled.";
                        _isApplyingStage = false;
                        if (ownsOperation)
                        {
                            EndOperation();
                        }
                        Repaint();
                    }).ConfigureAwait(false);
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
                    if (ownsOperation)
                    {
                        EndOperation();
                    }
                    Repaint();
                }).ConfigureAwait(false);

                _lastFixPassTargets = stageScriptTargets != null && stageScriptTargets.Count > 0 ? stageScriptTargets : null;
                _lastFixPassLabel = _lastFixPassTargets == null ? string.Empty : label;
                await MaybeRunStageFixPassAsync(stageScriptTargets, label).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                EditorApplication.delayCall += () =>
                {
                    _toolStatus = "Canceled.";
                    _isApplyingStage = false;
                    if (ownsOperation)
                    {
                        EndOperation();
                    }
                    Repaint();
                };
            }
            catch (Exception ex)
            {
                EditorApplication.delayCall += () =>
                {
                    _toolStatus = ex.Message;
                    _isApplyingStage = false;
                    if (ownsOperation)
                    {
                        EndOperation();
                    }
                    Repaint();
                };
            }
        }

        private async Task MaybeRunStageFixPassAsync(HashSet<string> stageScriptTargets, string stageLabel)
        {
            if (stageScriptTargets == null || stageScriptTargets.Count == 0 || _isFixingErrors)
            {
                return;
            }

            await WaitForCompilationAsync(CancellationToken.None).ConfigureAwait(false);
            var errors = await RunOnMainThread(GetConsoleErrorItems).ConfigureAwait(false);
            var stageErrors = FilterErrorsByScriptPaths(errors, stageScriptTargets);
            if (stageErrors.Count == 0)
            {
                return;
            }

            if (!ProtoProviderSettings.HasApiKey())
            {
                _toolStatus = $"Fix pass skipped for {stageLabel}: missing API key.";
                return;
            }

            await RunFixPassAsync(stageScriptTargets, stageLabel).ConfigureAwait(false);
        }

        private static HashSet<string> CollectStageScriptPaths(
            List<ProtoFeatureRequest> stageRequests,
            Dictionary<string, ProtoFeatureRequest> requestLookup,
            Dictionary<string, ProtoFeatureRequest> scriptByName)
        {
            var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (stageRequests == null || stageRequests.Count == 0)
            {
                return results;
            }

            foreach (var request in stageRequests)
            {
                if (request == null)
                {
                    continue;
                }

                AddScriptPathFromRequest(request, results);

                if (string.Equals(request.type, "prefab_component", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(request.type, "scene_manager", StringComparison.OrdinalIgnoreCase))
                {
                    AddScriptPathFromName(request.name, scriptByName, results);
                }

                if (request.dependsOn != null && requestLookup != null)
                {
                    foreach (var depId in request.dependsOn)
                    {
                        if (string.IsNullOrWhiteSpace(depId))
                        {
                            continue;
                        }

                        if (requestLookup.TryGetValue(depId, out var depRequest))
                        {
                            AddScriptPathFromRequest(depRequest, results);
                        }
                    }
                }

                if (request.steps != null)
                {
                    foreach (var step in request.steps)
                    {
                        if (step == null)
                        {
                            continue;
                        }

                        if (step.dependsOn != null && requestLookup != null)
                        {
                            foreach (var depId in step.dependsOn)
                            {
                                if (string.IsNullOrWhiteSpace(depId))
                                {
                                    continue;
                                }

                                if (requestLookup.TryGetValue(depId, out var depRequest))
                                {
                                    AddScriptPathFromRequest(depRequest, results);
                                }
                            }
                        }

                        if (string.Equals(step.type, "prefab_component", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(step.type, "scene_manager", StringComparison.OrdinalIgnoreCase))
                        {
                            AddScriptPathFromName(step.name, scriptByName, results);
                        }
                    }
                }
            }

            return results;
        }

        private static void AddScriptPathFromName(string scriptName, Dictionary<string, ProtoFeatureRequest> scriptByName, HashSet<string> results)
        {
            if (string.IsNullOrWhiteSpace(scriptName) || scriptByName == null || results == null)
            {
                return;
            }

            if (scriptByName.TryGetValue(scriptName.Trim(), out var scriptRequest))
            {
                AddScriptPathFromRequest(scriptRequest, results);
            }
        }

        private static void AddScriptPathFromRequest(ProtoFeatureRequest request, HashSet<string> results)
        {
            if (request == null || results == null)
            {
                return;
            }

            if (!string.Equals(request.type, "script", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var name = request.name?.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            var folder = NormalizeFolderPath(request.path);
            if (string.IsNullOrWhiteSpace(folder))
            {
                return;
            }

            var assetPath = EnsureProjectPath($"{folder}/{name}.cs");
            results.Add(assetPath);
        }

        private async Task WaitForCompilationAsync(CancellationToken token)
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var isCompiling = await RunOnMainThread(() => EditorApplication.isCompiling).ConfigureAwait(false);
                if (!isCompiling)
                {
                    return;
                }

                await Task.Delay(200, token).ConfigureAwait(false);
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
                case "scene_prefab":
                    return "[ScenePrefab]";
                case "scene_manager":
                    return "[SceneMgr]";
                case "asset":
                    return "[Asset]";
                case "prefab":
                    return "[Prefab]";
                case "prefab_component":
                    return "[PrefabComp]";
                case "material":
                    return "[Material]";
                default:
                    return "[?]";
            }
        }

        private static string NormalizeStatus(string status)
        {
            return status.ToStatus().ToNormalizedString();
        }

        private async Task<bool> ApplyRequestAsync(ProtoFeatureRequest request, ProtoProviderSnapshot settings, ProtoChatMessage[] baseMessages, Dictionary<string, ProtoFeatureRequest> requestLookup, CancellationToken cancellationToken)
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
                    if (settings == null || string.IsNullOrWhiteSpace(settings.ApiKey))
                    {
                        request.notes = AppendNote(request.notes, "Missing API key.");
                        return false;
                    }
                    return await GenerateScriptForRequestAsync(request, settings, baseMessages, requestLookup, cancellationToken).ConfigureAwait(false);
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
                case "prefab_component":
                    return await ApplyPrefabComponentRequestAsync(request, requestLookup).ConfigureAwait(false);
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
                    if (settings != null && !string.IsNullOrWhiteSpace(settings.ApiKey))
                    {
                        var suggestedNotes = await RequestSceneNotesAsync(request, requestLookup, settings, cancellationToken).ConfigureAwait(false);
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
                    if (string.IsNullOrWhiteSpace(sceneError) && settings != null && !string.IsNullOrWhiteSpace(settings.ApiKey))
                    {
                        EnqueueSceneLayout(request, requestLookup, settings, scenePath);
                    }
                    return string.IsNullOrWhiteSpace(sceneError);
                case "scene_prefab":
                    return await ApplyScenePrefabRequestAsync(request, requestLookup).ConfigureAwait(false);
                case "scene_manager":
                    return await ApplySceneManagerRequestAsync(request, requestLookup).ConfigureAwait(false);
                case "asset":
                    return await ApplyAssetFallbackAsync(request, requestLookup).ConfigureAwait(false);
                default:
                    request.notes = AppendNote(request.notes, "Type not supported by automation yet.");
                    return false;
            }
        }

        private async Task<bool> ApplyPrefabComponentRequestAsync(ProtoFeatureRequest request, Dictionary<string, ProtoFeatureRequest> requestLookup)
        {
            if (request == null)
            {
                return false;
            }

            var unresolvedNames = new List<string>();
            var propertyFlags = ExtractComponentPropertyFlags(request.name);
            List<string> componentNames = null;
            await RunOnMainThread(() =>
            {
                componentNames = ResolveComponentNamesForRequest(request, unresolvedNames);
            }).ConfigureAwait(false);
            if (componentNames == null || componentNames.Count == 0)
            {
                request.notes = AppendNote(request.notes, "Prefab component name is missing.");
                return false;
            }

            var prefabPath = ResolvePrefabPathForComponentRequest(request, requestLookup);
            if (string.IsNullOrWhiteSpace(prefabPath))
            {
                request.notes = AppendNote(request.notes, "Prefab path is missing.");
                return false;
            }

            AttachmentResult result = default;
            var propertyError = string.Empty;
            await RunOnMainThread(() =>
            {
                result = AttachComponentsToPrefab(prefabPath, componentNames);
                if (propertyFlags.Count > 0)
                {
                    propertyError = ApplyPrefabComponentPropertyFlags(prefabPath, componentNames, propertyFlags);
                }
            }).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(result.error) &&
                !string.Equals(result.error, "No prefab components added.", StringComparison.OrdinalIgnoreCase))
            {
                request.notes = AppendNote(request.notes, result.error);
                return false;
            }

            if (!string.IsNullOrWhiteSpace(propertyError))
            {
                request.notes = AppendNote(request.notes, propertyError);
            }

            if (unresolvedNames.Count > 0)
            {
                request.notes = AppendNote(request.notes, $"Unresolved component names: {string.Join(", ", unresolvedNames)}.");
            }

            return true;
        }

        private async Task<bool> ApplyScenePrefabRequestAsync(ProtoFeatureRequest request, Dictionary<string, ProtoFeatureRequest> requestLookup)
        {
            return await ApplySceneDetailRequestAsync(request, requestLookup, AddScenePrefabs).ConfigureAwait(false);
        }

        private async Task<bool> ApplySceneManagerRequestAsync(ProtoFeatureRequest request, Dictionary<string, ProtoFeatureRequest> requestLookup)
        {
            return await ApplySceneDetailRequestAsync(request, requestLookup, AddSceneManagers).ConfigureAwait(false);
        }

        private async Task<bool> ApplySceneDetailRequestAsync(
            ProtoFeatureRequest request,
            Dictionary<string, ProtoFeatureRequest> requestLookup,
            Action<ProtoFeatureRequest, Dictionary<string, ProtoFeatureRequest>, UnityEngine.SceneManagement.Scene> applyAction)
        {
            if (request == null)
            {
                return false;
            }

            var scenePath = ResolveScenePathForDetailRequest(request, requestLookup);
            if (string.IsNullOrWhiteSpace(scenePath))
            {
                request.notes = AppendNote(request.notes, "Scene path is missing.");
                return false;
            }

            var error = string.Empty;
            await RunOnMainThread(() =>
            {
                if (!EnsureAdditiveSceneCreationPossible(out error))
                {
                    return;
                }

                var scene = UnityEditor.SceneManagement.EditorSceneManager.OpenScene(
                    scenePath,
                    UnityEditor.SceneManagement.OpenSceneMode.Additive);
                if (!scene.IsValid())
                {
                    error = $"Failed to open scene at {scenePath}.";
                    return;
                }

                applyAction?.Invoke(request, requestLookup, scene);
                HydrateSceneReferences(request, requestLookup, scene, scenePath);

                var saved = UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene);
                UnityEditor.SceneManagement.EditorSceneManager.CloseScene(scene, true);
                if (!saved)
                {
                    error = "Failed to save scene asset.";
                }
            }).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(error))
            {
                request.notes = AppendNote(request.notes, error);
                return false;
            }

            return true;
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

        private static async Task<string> RequestSceneNotesAsync(ProtoFeatureRequest request, Dictionary<string, ProtoFeatureRequest> requestLookup, ProtoProviderSnapshot settings, CancellationToken cancellationToken)
        {
            if (request == null || settings == null || string.IsNullOrWhiteSpace(settings.ApiKey))
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
                    content = ProtoPrompts.SceneNotesSystemInstruction
                }
            };

            AppendPrefabIndexMessage(messages, ReadPrefabIndexMarkdown());

            messages.Add(new ProtoChatMessage
            {
                role = "user",
                content = string.Format(ProtoPrompts.SceneNotesUserPromptFormat, request.name, prefabsList, managersList)
            });

            return await ProtoProviderClient.SendChatAsync(settings, messages.ToArray(), 320, cancellationToken).ConfigureAwait(false);
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
            var hasScenePrefabSteps = HasSceneDetailRequests(request, requestLookup, "scene_prefab");
            var hasSceneManagerSteps = HasSceneDetailRequests(request, requestLookup, "scene_manager");
            if (!hasScenePrefabSteps)
            {
                AddScenePrefabs(request, requestLookup, newScene);
            }
            if (!hasSceneManagerSteps)
            {
                AddSceneManagers(request, requestLookup, newScene);
            }
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
            var hasScenePrefabSteps = HasSceneDetailRequests(request, requestLookup, "scene_prefab");
            var hasSceneManagerSteps = HasSceneDetailRequests(request, requestLookup, "scene_manager");
            if (!hasScenePrefabSteps)
            {
                AddScenePrefabs(request, requestLookup, scene);
            }
            if (!hasSceneManagerSteps)
            {
                AddSceneManagers(request, requestLookup, scene);
            }
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

        private static void EnqueueSceneLayout(ProtoFeatureRequest request, Dictionary<string, ProtoFeatureRequest> requestLookup, ProtoProviderSnapshot settings, string scenePath)
        {
            if (request == null || string.IsNullOrWhiteSpace(scenePath) || settings == null || string.IsNullOrWhiteSpace(settings.ApiKey))
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
                settings = settings,
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
                    content = ProtoPrompts.SceneLayoutSystemInstruction
                },
                new ProtoChatMessage
                {
                    role = "user",
                    content = string.Format(ProtoPrompts.SceneLayoutUserPromptFormat, list, notes)
                }
            };

            if (job.settings == null || string.IsNullOrWhiteSpace(job.settings.ApiKey))
            {
                return string.Empty;
            }

            return await ProtoProviderClient.SendChatAsync(job.settings, messages.ToArray(), 320).ConfigureAwait(false);
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
            public ProtoProviderSnapshot settings;
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

        private static string ResolvePrefabPathForComponentRequest(ProtoFeatureRequest request, Dictionary<string, ProtoFeatureRequest> requestLookup)
        {
            if (request == null)
            {
                return string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(request.path))
            {
                var normalized = EnsureProjectPath(request.path.Trim());
                if (normalized.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                {
                    return normalized;
                }
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

                    var isPrefab = string.Equals(depRequest.type, "prefab", StringComparison.OrdinalIgnoreCase) ||
                                   (string.Equals(depRequest.type, "asset", StringComparison.OrdinalIgnoreCase) && LooksLikePrefabRequest(depRequest));
                    if (!isPrefab)
                    {
                        continue;
                    }

                    var prefabName = GetPrefabName(depRequest);
                    return BuildPrefabPath(depRequest.path, prefabName, out _);
                }
            }

            return string.Empty;
        }

        private static string ResolveScenePathForDetailRequest(ProtoFeatureRequest request, Dictionary<string, ProtoFeatureRequest> requestLookup)
        {
            if (request == null)
            {
                return string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(request.path))
            {
                var normalized = EnsureProjectPath(request.path.Trim());
                if (normalized.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                {
                    return normalized;
                }
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

                    var isScene = string.Equals(depRequest.type, "scene", StringComparison.OrdinalIgnoreCase) ||
                                  (string.Equals(depRequest.type, "asset", StringComparison.OrdinalIgnoreCase) && LooksLikeSceneRequest(depRequest));
                    if (!isScene)
                    {
                        continue;
                    }

                    var sceneName = GetSceneName(depRequest);
                    return BuildScenePath(depRequest.path, sceneName, out _);
                }
            }

            return string.Empty;
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

        private async Task<bool> GenerateScriptForRequestAsync(ProtoFeatureRequest request, ProtoProviderSnapshot settings, ProtoChatMessage[] baseMessages, Dictionary<string, ProtoFeatureRequest> requestLookup, CancellationToken cancellationToken)
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
                    ? string.Format(ProtoPrompts.ScriptDefaultPromptFormat, request.name)
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
                    role = "system",
                    content = ProtoPrompts.ScriptGenerationSystemInstruction
                });
                messages.Add(new ProtoChatMessage
                {
                    role = "user",
                    content = string.Format(ProtoPrompts.ScriptGenerationUserInstructionFormat, request.name)
                });
                messages.Add(new ProtoChatMessage
                {
                    role = "user",
                    content = scriptPrompt
                });

                _apiWaitTimeoutSeconds = 320;
                await RunOnMainThread(() => BeginApiWait($"script {request.name}")).ConfigureAwait(false);
                var response = await ProtoProviderClient.SendChatAsync(settings, messages.ToArray(), _apiWaitTimeoutSeconds, cancellationToken).ConfigureAwait(false);
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
            var total = ordered.Count;
            if (total > 0)
            {
                await RunOnMainThread(() => SetOperationProgress("Executing requests", 0, total)).ConfigureAwait(false);
            }

            for (var i = 0; i < ordered.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var request = ordered[i];
                if (request == null)
                {
                    continue;
                }

                if (total > 0)
                {
                    await RunOnMainThread(() => SetOperationProgress("Executing requests", i + 1, total)).ConfigureAwait(false);
                }

                if (request.HasStatus(ProtoAgentRequestStatus.Done))
                {
                    continue;
                }

                await RunOnMainThread(() =>
                {
                    request.SetStatus(ProtoAgentRequestStatus.InProgress);
                    request.updatedAt = DateTime.UtcNow.ToString("o");
                    ProtoFeatureRequestStore.SaveRequest(request);
                }).ConfigureAwait(false);

                _toolStatus = $"Applying {request.type} {i + 1}/{ordered.Count}: {request.name}";
                EditorApplication.delayCall += Repaint;

                var succeeded = false;
                try
                {
                    succeeded = await ApplyRequestAsync(request, settings, baseMessages, requestLookup, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    request.notes = AppendNote(request.notes, ex.Message);
                    succeeded = false;
                }

                await RunOnMainThread(() =>
                {
                    request.SetStatus(succeeded ? ProtoAgentRequestStatus.Done : ProtoAgentRequestStatus.Blocked);
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
                if (request != null && request.HasStatus(ProtoAgentRequestStatus.Blocked))
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

            var prefabsWithDetailRequests = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var request in featureRequests)
            {
                if (request == null || !string.Equals(request.type, "prefab_component", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (request.dependsOn == null)
                {
                    continue;
                }

                foreach (var depId in request.dependsOn)
                {
                    if (string.IsNullOrWhiteSpace(depId))
                    {
                        continue;
                    }

                    if (requestById.TryGetValue(depId, out var depRequest) &&
                        (string.Equals(depRequest.type, "prefab", StringComparison.OrdinalIgnoreCase) ||
                         (string.Equals(depRequest.type, "asset", StringComparison.OrdinalIgnoreCase) && LooksLikePrefabRequest(depRequest))))
                    {
                        if (!string.IsNullOrWhiteSpace(depRequest.id))
                        {
                            prefabsWithDetailRequests.Add(depRequest.id);
                        }
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

                if (!string.IsNullOrWhiteSpace(request.id) && prefabsWithDetailRequests.Contains(request.id))
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
                if (!IsValidComponentType(type))
                {
                    if (type != null && TryResolveAbstractComponentFallback(type, name, out var fallbackName))
                    {
                        type = FindTypeByName(fallbackName);
                    }
                }

                if (!IsValidComponentType(type))
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

        private static Dictionary<string, bool> ExtractComponentPropertyFlags(string text)
        {
            var flags = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(text))
            {
                return flags;
            }

            foreach (Match match in Regex.Matches(text, @"\((?<inner>[^)]+)\)"))
            {
                var inner = match.Groups["inner"].Value;
                if (string.IsNullOrWhiteSpace(inner))
                {
                    continue;
                }

                foreach (var entry in inner.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var token = entry.Trim();
                    if (string.IsNullOrWhiteSpace(token))
                    {
                        continue;
                    }

                    var name = token;
                    var value = true;
                    var separatorIndex = token.IndexOf('=');
                    if (separatorIndex < 0)
                    {
                        separatorIndex = token.IndexOf(':');
                    }

                    if (separatorIndex >= 0)
                    {
                        name = token.Substring(0, separatorIndex).Trim();
                        var valueText = token.Substring(separatorIndex + 1).Trim();
                        if (!string.IsNullOrWhiteSpace(valueText))
                        {
                            if (bool.TryParse(valueText, out var boolValue))
                            {
                                value = boolValue;
                            }
                            else if (string.Equals(valueText, "1", StringComparison.OrdinalIgnoreCase))
                            {
                                value = true;
                            }
                            else if (string.Equals(valueText, "0", StringComparison.OrdinalIgnoreCase))
                            {
                                value = false;
                            }
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        flags[name] = value;
                    }
                }
            }

            return flags;
        }

        private static string ApplyPrefabComponentPropertyFlags(
            string prefabPath,
            List<string> componentNames,
            Dictionary<string, bool> propertyFlags)
        {
            if (string.IsNullOrWhiteSpace(prefabPath) || propertyFlags == null || propertyFlags.Count == 0)
            {
                return string.Empty;
            }

            var root = PrefabUtility.LoadPrefabContents(prefabPath);
            if (root == null)
            {
                return "Failed to load prefab contents for property updates.";
            }

            var pending = new HashSet<string>(propertyFlags.Keys, StringComparer.OrdinalIgnoreCase);
            var appliedAny = false;
            if (componentNames != null)
            {
                foreach (var componentName in componentNames)
                {
                    if (string.IsNullOrWhiteSpace(componentName))
                    {
                        continue;
                    }

                    var type = FindTypeByName(componentName);
                    if (type == null || !typeof(Component).IsAssignableFrom(type))
                    {
                        continue;
                    }

                    var component = root.GetComponent(type);
                    if (component == null)
                    {
                        continue;
                    }

                    foreach (var flag in propertyFlags)
                    {
                        if (TrySetComponentBoolProperty(component, flag.Key, flag.Value))
                        {
                            appliedAny = true;
                            pending.Remove(flag.Key);
                        }
                    }
                }
            }

            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            PrefabUtility.UnloadPrefabContents(root);

            if (!appliedAny)
            {
                return "Prefab component flags were not applied.";
            }

            if (pending.Count > 0)
            {
                return $"Prefab component flags not found: {string.Join(", ", pending)}.";
            }

            return string.Empty;
        }

        private static bool TrySetComponentBoolProperty(Component component, string propertyName, bool value)
        {
            if (component == null || string.IsNullOrWhiteSpace(propertyName))
            {
                return false;
            }

            var type = component.GetType();
            var properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            foreach (var property in properties)
            {
                if (!string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!property.CanWrite || property.PropertyType != typeof(bool))
                {
                    continue;
                }

                property.SetValue(component, value);
                return true;
            }

            var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            foreach (var field in fields)
            {
                if (!string.Equals(field.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (field.FieldType != typeof(bool))
                {
                    continue;
                }

                field.SetValue(component, value);
                return true;
            }

            return false;
        }

        private static List<string> ResolveComponentNamesForRequest(ProtoFeatureRequest request, List<string> unresolvedNames)
        {
            var results = new List<string>();
            var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (request == null)
            {
                return results;
            }

            var rawName = request.name;
            if (!string.IsNullOrWhiteSpace(rawName))
            {
                foreach (var candidate in EnumerateComponentCandidates(rawName))
                {
                    if (TryResolveComponentTypeName(candidate, out var typeName))
                    {
                        if (unique.Add(typeName))
                        {
                            results.Add(typeName);
                        }
                    }
                    else if (LooksLikeIdentifierCandidate(candidate))
                    {
                        unresolvedNames?.Add(candidate.Trim());
                    }
                }
            }

            foreach (var candidate in ParseComponentNamesFromNotes(request.notes))
            {
                if (TryResolveComponentTypeName(candidate, out var typeName))
                {
                    if (unique.Add(typeName))
                    {
                        results.Add(typeName);
                    }
                }
                else if (LooksLikeIdentifierCandidate(candidate))
                {
                    unresolvedNames?.Add(candidate.Trim());
                }
            }

            if (results.Count == 0 && !string.IsNullOrWhiteSpace(rawName))
            {
                var trimmed = rawName.Trim();
                if (IdentifierRegex.IsMatch(trimmed) && unique.Add(trimmed))
                {
                    results.Add(trimmed);
                }
            }

            return results;
        }

        private static IEnumerable<string> EnumerateComponentCandidates(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                yield break;
            }

            var split = text.Split(new[] { '\n', '\r', ',', ';', '|', '/' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var entry in split)
            {
                var trimmed = entry.Trim();
                if (string.IsNullOrWhiteSpace(trimmed))
                {
                    continue;
                }

                var parenIndex = trimmed.IndexOf('(');
                if (parenIndex > 0)
                {
                    var baseName = trimmed.Substring(0, parenIndex).Trim();
                    if (!string.IsNullOrWhiteSpace(baseName))
                    {
                        yield return baseName;
                    }
                }

                if (trimmed.Contains(". "))
                {
                    var subparts = trimmed.Split(new[] { ". " }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var sub in subparts)
                    {
                        var cleaned = sub.Trim().Trim('.', ':');
                        if (!string.IsNullOrWhiteSpace(cleaned))
                        {
                            yield return cleaned;
                        }
                    }
                }
                else
                {
                    yield return trimmed.Trim('.', ':');
                }
            }
        }

        private static bool TryResolveComponentTypeName(string candidate, out string typeName)
        {
            typeName = null;
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return false;
            }

            foreach (var variant in BuildComponentNameVariants(candidate))
            {
                var type = FindTypeByName(variant);
                if (type != null)
                {
                    if (IsValidComponentType(type))
                    {
                        typeName = type.Name;
                        return true;
                    }

                    if (TryResolveAbstractComponentFallback(type, variant, out var fallback))
                    {
                        typeName = fallback;
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool IsValidComponentType(Type type)
        {
            return type != null && typeof(Component).IsAssignableFrom(type) && !type.IsAbstract;
        }

        private static bool TryResolveAbstractComponentFallback(Type type, string candidate, out string fallbackName)
        {
            fallbackName = null;
            if (type == null)
            {
                return false;
            }

            var typeName = type.Name;
            if (string.Equals(typeName, "Collider", StringComparison.OrdinalIgnoreCase))
            {
                fallbackName = "BoxCollider";
                return true;
            }

            if (string.Equals(typeName, "Collider2D", StringComparison.OrdinalIgnoreCase))
            {
                fallbackName = "BoxCollider2D";
                return true;
            }

            if (string.Equals(typeName, "Renderer", StringComparison.OrdinalIgnoreCase))
            {
                fallbackName = "MeshRenderer";
                return true;
            }

            if (!string.IsNullOrWhiteSpace(candidate) &&
                candidate.IndexOf("2d", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (string.Equals(typeName, "Rigidbody", StringComparison.OrdinalIgnoreCase))
                {
                    fallbackName = "Rigidbody2D";
                    return true;
                }
            }

            return false;
        }

        private static IEnumerable<string> BuildComponentNameVariants(string candidate)
        {
            var results = new List<string>();
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return results;
            }

            void AddVariant(string value)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    return;
                }

                var trimmed = value.Trim();
                if (results.Exists(existing => string.Equals(existing, trimmed, StringComparison.OrdinalIgnoreCase)))
                {
                    return;
                }
                results.Add(trimmed);
            }

            var trimmedCandidate = candidate.Trim().Trim('.', ':');
            AddVariant(trimmedCandidate);

            if (trimmedCandidate.Contains(".") && trimmedCandidate.IndexOf(' ') < 0)
            {
                var lastDot = trimmedCandidate.LastIndexOf('.');
                if (lastDot >= 0 && lastDot < trimmedCandidate.Length - 1)
                {
                    AddVariant(trimmedCandidate.Substring(lastDot + 1));
                }
            }

            var cleaned = Regex.Replace(trimmedCandidate, @"[^A-Za-z0-9_\. ]", " ").Trim();
            AddVariant(cleaned);

            var pascal = ToPascalCase(cleaned);
            AddVariant(pascal);

            var collapsed = Regex.Replace(cleaned, @"\s+", string.Empty);
            AddVariant(collapsed);

            return results;
        }

        private static string ToPascalCase(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var parts = Regex.Split(value, @"[^A-Za-z0-9_]+");
            var builder = new StringBuilder();
            foreach (var part in parts)
            {
                if (string.IsNullOrWhiteSpace(part))
                {
                    continue;
                }

                var cleaned = part.Trim('_');
                if (string.IsNullOrWhiteSpace(cleaned))
                {
                    continue;
                }

                builder.Append(char.ToUpperInvariant(cleaned[0]));
                if (cleaned.Length > 1)
                {
                    builder.Append(cleaned.Substring(1));
                }
            }

            return builder.ToString();
        }

        private static bool LooksLikeIdentifierCandidate(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var trimmed = value.Trim().Trim('.', ':');
            if (IdentifierRegex.IsMatch(trimmed))
            {
                return true;
            }

            return Regex.IsMatch(trimmed, @"^[A-Za-z][A-Za-z0-9_ ]+$");
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

        private static bool HasSceneDetailRequests(ProtoFeatureRequest sceneRequest, Dictionary<string, ProtoFeatureRequest> requestLookup, string detailType)
        {
            if (sceneRequest == null || string.IsNullOrWhiteSpace(sceneRequest.id) || requestLookup == null)
            {
                return false;
            }

            foreach (var request in requestLookup.Values)
            {
                if (request == null || !string.Equals(request.type, detailType, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (request.dependsOn == null)
                {
                    continue;
                }

                foreach (var depId in request.dependsOn)
                {
                    if (string.Equals(depId, sceneRequest.id, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            return false;
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
            builder.AppendLine(ProtoPrompts.ContractContextHeader);
            builder.AppendLine(ProtoPrompts.ContractContextApiInstruction);
            builder.AppendLine(ProtoPrompts.ContractContextNestedTypeInstruction);
            builder.AppendLine(ProtoPrompts.ContractContextEnumInstruction);

            if (!string.IsNullOrWhiteSpace(dependencySummary))
            {
                builder.AppendLine(ProtoPrompts.ContractContextDependencyHeader);
                builder.AppendLine(dependencySummary);
            }

            if (!string.IsNullOrWhiteSpace(indexSummary))
            {
                builder.AppendLine(ProtoPrompts.ContractContextProjectHeader);
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

                var kindLabel = string.IsNullOrWhiteSpace(entry.kind) ? string.Empty : $"{entry.kind} ";
                builder.AppendLine($"- {kindLabel}{entry.DisplayName} ({entry.path})");
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

        private struct MissingMemberInfo
        {
            public string typeName;
            public string memberName;
        }

        private static MissingMemberInfo ExtractMissingMemberInfo(string message)
        {
            var result = new MissingMemberInfo();
            if (string.IsNullOrWhiteSpace(message))
            {
                return result;
            }

            var match = Regex.Match(message, @"'([^']+)'\s+does not contain a definition for\s+'([^']+)'", RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                match = Regex.Match(message, @"`([^`]+)`\s+does not contain a definition for\s+`([^`]+)`", RegexOptions.IgnoreCase);
            }

            if (match.Success)
            {
                result.typeName = match.Groups[1].Value.Trim();
                result.memberName = match.Groups[2].Value.Trim();
            }

            return result;
        }

        private static string ExtractMissingSymbolName(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return string.Empty;
            }

            var match = Regex.Match(message, @"The name '([^']+)' does not exist in the current context", RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                match = Regex.Match(message, @"The name `([^`]+)` does not exist", RegexOptions.IgnoreCase);
            }

            return match.Success ? match.Groups[1].Value.Trim() : string.Empty;
        }

        private string BuildFixPassContext(ConsoleErrorItem error)
        {
            var index = GetScriptContractIndex();
            return BuildFixContext(error, index);
        }

        private static string BuildFixContext(ConsoleErrorItem error, ScriptContractIndex index)
        {
            if (index == null || string.IsNullOrWhiteSpace(error.message))
            {
                return string.Empty;
            }

            var builder = new StringBuilder(512);
            var usedEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var missingType = ExtractMissingTypeName(error.message);
            if (!string.IsNullOrWhiteSpace(missingType))
            {
                var matches = FindContractEntriesByTypeName(index, missingType, 3);
                if (matches.Count > 0)
                {
                    builder.AppendLine($"Type lookup for '{missingType}':");
                    foreach (var entry in matches)
                    {
                        AppendContractEntrySummary(builder, entry, usedEntries, null);
                    }
                }
                else
                {
                    builder.AppendLine($"Type lookup for '{missingType}': not found in project scripts.");
                }
            }

            var missingMember = ExtractMissingMemberInfo(error.message);
            if (!string.IsNullOrWhiteSpace(missingMember.memberName))
            {
                var memberLabel = string.IsNullOrWhiteSpace(missingMember.typeName)
                    ? $"Member lookup for '{missingMember.memberName}':"
                    : $"Member lookup for '{missingMember.typeName}.{missingMember.memberName}':";
                builder.AppendLine(memberLabel);

                if (!string.IsNullOrWhiteSpace(missingMember.typeName))
                {
                    var matches = FindContractEntriesByTypeName(index, missingMember.typeName, 2);
                    if (matches.Count == 0)
                    {
                        builder.AppendLine($"- No type match for '{missingMember.typeName}'.");
                    }
                    foreach (var entry in matches)
                    {
                        AppendContractEntrySummary(builder, entry, usedEntries, missingMember.memberName);
                    }
                }
                else
                {
                    var matches = FindEntriesWithMemberName(index, missingMember.memberName, 3);
                    if (matches.Count == 0)
                    {
                        builder.AppendLine($"- No member matches for '{missingMember.memberName}'.");
                    }
                    foreach (var entry in matches)
                    {
                        AppendContractEntrySummary(builder, entry, usedEntries, missingMember.memberName);
                    }
                }
            }

            var missingSymbol = ExtractMissingSymbolName(error.message);
            if (!string.IsNullOrWhiteSpace(missingSymbol) &&
                !string.Equals(missingSymbol, missingMember.memberName, StringComparison.OrdinalIgnoreCase))
            {
                builder.AppendLine($"Symbol lookup for '{missingSymbol}':");
                var memberMatches = FindEntriesWithMemberName(index, missingSymbol, 3);
                if (memberMatches.Count == 0)
                {
                    var typeMatches = FindContractEntriesByTypeName(index, missingSymbol, 2);
                    if (typeMatches.Count == 0)
                    {
                        builder.AppendLine("- No matches in project scripts.");
                    }
                    foreach (var entry in typeMatches)
                    {
                        AppendContractEntrySummary(builder, entry, usedEntries, null);
                    }
                }
                else
                {
                    foreach (var entry in memberMatches)
                    {
                        AppendContractEntrySummary(builder, entry, usedEntries, missingSymbol);
                    }
                }
            }

            return builder.ToString().Trim();
        }

        private static List<ScriptContractEntry> FindContractEntriesByTypeName(ScriptContractIndex index, string typeName, int maxResults)
        {
            var results = new List<ScriptContractEntry>();
            if (index == null || string.IsNullOrWhiteSpace(typeName) || maxResults <= 0)
            {
                return results;
            }

            var trimmed = typeName.Trim();
            foreach (var entry in index.entries)
            {
                if (results.Count >= maxResults)
                {
                    break;
                }

                if (!string.IsNullOrWhiteSpace(entry.fullName) &&
                    string.Equals(entry.fullName, trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(entry);
                }
            }

            var simpleName = trimmed;
            var lastDot = trimmed.LastIndexOf('.');
            if (lastDot >= 0 && lastDot < trimmed.Length - 1)
            {
                simpleName = trimmed.Substring(lastDot + 1);
            }

            if (!string.IsNullOrWhiteSpace(simpleName))
            {
                foreach (var entry in index.entries)
                {
                    if (results.Count >= maxResults)
                    {
                        break;
                    }

                    if (string.Equals(entry.name, simpleName, StringComparison.OrdinalIgnoreCase) && !results.Contains(entry))
                    {
                        results.Add(entry);
                    }
                }
            }

            return results;
        }

        private static List<ScriptContractEntry> FindEntriesWithMemberName(ScriptContractIndex index, string memberName, int maxResults)
        {
            var results = new List<ScriptContractEntry>();
            if (index == null || string.IsNullOrWhiteSpace(memberName) || maxResults <= 0)
            {
                return results;
            }

            foreach (var entry in index.entries)
            {
                if (entry == null || entry.members.Count == 0)
                {
                    continue;
                }

                foreach (var member in entry.members)
                {
                    if (member.IndexOf(memberName, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    results.Add(entry);
                    break;
                }

                if (results.Count >= maxResults)
                {
                    break;
                }
            }

            return results;
        }

        private static void AppendContractEntrySummary(
            StringBuilder builder,
            ScriptContractEntry entry,
            HashSet<string> usedEntries,
            string memberFilter)
        {
            if (builder == null || entry == null)
            {
                return;
            }

            var key = $"{entry.DisplayName}:{entry.path}";
            if (usedEntries != null && usedEntries.Contains(key))
            {
                return;
            }

            usedEntries?.Add(key);
            builder.AppendLine($"- {entry.DisplayName} ({entry.path})");

            if (entry.members.Count == 0)
            {
                return;
            }

            var filtered = new List<string>();
            foreach (var member in entry.members)
            {
                if (string.IsNullOrWhiteSpace(memberFilter) ||
                    member.IndexOf(memberFilter, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    filtered.Add(member);
                }
            }

            var source = filtered.Count > 0 ? filtered : entry.members;
            var count = 0;
            foreach (var member in source)
            {
                if (count >= MaxMembersPerScript)
                {
                    break;
                }

                builder.AppendLine($"  - {member}");
                count++;
            }
        }

        private static bool ScriptIndexContainsType(string scriptIndex, string typeName)
        {
            if (string.IsNullOrWhiteSpace(scriptIndex) || string.IsNullOrWhiteSpace(typeName))
            {
                return false;
            }

            var escaped = Regex.Escape(typeName);
            var pattern = typeName.IndexOfAny(new[] { '.', '+' }) >= 0
                ? $@"^-\s+.*{escaped}.*\("
                : $@"^-\s+.*\b{escaped}\b.*\(";
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
            var typeStack = new Stack<TypeScope>();
            ScriptContractEntry pendingEntry = null;
            var braceDepth = 0;

            foreach (var rawLine in File.ReadAllLines(fullPath))
            {
                var line = rawLine;
                var commentIndex = line.IndexOf("//", StringComparison.Ordinal);
                if (commentIndex >= 0)
                {
                    line = line.Substring(0, commentIndex);
                }

                var trimmed = line.Trim();

                while (typeStack.Count > 0 && braceDepth < typeStack.Peek().depth)
                {
                    typeStack.Pop();
                }

                var isTypeDeclaration = false;
                if (!string.IsNullOrWhiteSpace(trimmed) && !trimmed.StartsWith("[", StringComparison.Ordinal))
                {
                    var namespaceMatch = NamespaceRegex.Match(trimmed);
                    if (namespaceMatch.Success)
                    {
                        currentNamespace = namespaceMatch.Groups[1].Value.Trim();
                    }
                    else
                    {
                        var typeMatch = TypeRegex.Match(trimmed);
                        if (typeMatch.Success)
                        {
                            isTypeDeclaration = true;
                            var typeName = typeMatch.Groups[3].Value.Trim();
                            var typeKind = typeMatch.Groups[2].Value.Trim();
                            var nestedPrefix = BuildNestedTypePrefix(typeStack);
                            var entry = new ScriptContractEntry
                            {
                                name = typeName,
                                fullName = BuildFullTypeName(currentNamespace, nestedPrefix, typeName),
                                path = assetPath,
                                kind = typeKind
                            };
                            results.Add(entry);
                            pendingEntry = entry;
                        }
                    }
                }

                if (!isTypeDeclaration &&
                    !string.IsNullOrWhiteSpace(trimmed) &&
                    typeStack.Count > 0 &&
                    !trimmed.StartsWith("{", StringComparison.Ordinal) &&
                    !trimmed.StartsWith("}", StringComparison.Ordinal) &&
                    !trimmed.StartsWith("[", StringComparison.Ordinal))
                {
                    TryAddMember(typeStack.Peek().entry, trimmed);
                }

                var openCount = CountChar(line, '{');
                var closeCount = CountChar(line, '}');
                if (pendingEntry != null && openCount > 0)
                {
                    typeStack.Push(new TypeScope(pendingEntry, braceDepth + 1));
                    pendingEntry = null;
                }

                braceDepth += openCount - closeCount;

                while (typeStack.Count > 0 && braceDepth < typeStack.Peek().depth)
                {
                    typeStack.Pop();
                }
            }

            return results;
        }

        private static string BuildNestedTypePrefix(Stack<TypeScope> typeStack)
        {
            if (typeStack == null || typeStack.Count == 0)
            {
                return string.Empty;
            }

            var scopes = typeStack.ToArray();
            var builder = new StringBuilder(128);
            for (var i = scopes.Length - 1; i >= 0; i--)
            {
                if (builder.Length > 0)
                {
                    builder.Append('.');
                }
                builder.Append(scopes[i].entry.name);
            }

            return builder.ToString();
        }

        private static string BuildFullTypeName(string currentNamespace, string nestedPrefix, string typeName)
        {
            var fullName = typeName;
            if (!string.IsNullOrWhiteSpace(nestedPrefix))
            {
                fullName = $"{nestedPrefix}.{typeName}";
            }

            if (!string.IsNullOrWhiteSpace(currentNamespace))
            {
                fullName = $"{currentNamespace}.{fullName}";
            }

            return fullName;
        }

        private static int CountChar(string text, char target)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0;
            }

            var count = 0;
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] == target)
                {
                    count++;
                }
            }

            return count;
        }

        private static void TryAddMember(ScriptContractEntry entry, string line)
        {
            if (entry == null || string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            if (entry.IsEnum)
            {
                TryAddEnumValue(entry, line);
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

        private static void TryAddEnumValue(ScriptContractEntry entry, string line)
        {
            if (entry == null || string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                return;
            }

            if (trimmed.StartsWith("[", StringComparison.Ordinal) ||
                trimmed.StartsWith("{", StringComparison.Ordinal) ||
                trimmed.StartsWith("}", StringComparison.Ordinal))
            {
                return;
            }

            var parts = trimmed.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                var token = part.Trim();
                if (string.IsNullOrWhiteSpace(token))
                {
                    continue;
                }

                var equalsIndex = token.IndexOf('=');
                if (equalsIndex >= 0)
                {
                    token = token.Substring(0, equalsIndex).Trim();
                }

                if (!IsValidIdentifier(token))
                {
                    continue;
                }

                AddMember(entry, $"value: {token}");
            }
        }

        private static bool IsValidIdentifier(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            return IdentifierRegex.IsMatch(value);
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

        private struct TypeScope
        {
            public ScriptContractEntry entry;
            public int depth;

            public TypeScope(ScriptContractEntry entry, int depth)
            {
                this.entry = entry;
                this.depth = depth;
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
            public string kind;
            public readonly List<string> members = new List<string>();

            public string DisplayName => string.IsNullOrWhiteSpace(fullName) ? name : fullName;
            public bool IsEnum => string.Equals(kind, "enum", StringComparison.OrdinalIgnoreCase);
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
            ClearOperationProgress();
        }

        private void SetOperationProgress(string label, int current, int total)
        {
            _operationProgressLabel = label ?? string.Empty;
            _operationProgressCurrent = Mathf.Max(0, current);
            _operationProgressTotal = Mathf.Max(0, total);
        }

        private void ClearOperationProgress()
        {
            _operationProgressLabel = string.Empty;
            _operationProgressCurrent = 0;
            _operationProgressTotal = 0;
        }

        private void RequestCancel()
        {
            if (_isCanceling)
            {
                return;
            }

            _isCanceling = true;
            _cancelRequested = true;
            _toolStatus = "Canceling...";
            if (_operationCts != null && !_operationCts.IsCancellationRequested)
            {
                _operationCts.Cancel();
            }
            if (_isAgentLoopActive)
            {
                FinalizeAgentLoopState("Canceled");
            }
            else
            {
                _status = string.Empty;
                _toolStatus = string.Empty;
                EndOperation();
                Repaint();
            }
            EndApiWait();
            _isCanceling = false;
        }

        private bool IsOperationActive()
        {
            return _isSending || _isGeneratingPlan || _isCreatingRequests || _isApplyingPlan || _isApplyingStage || _isFixingErrors || _isAgentLoopActive;
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

        private void CheckApiWaitTimeout(bool forceClear)
        {
            if (!_isApiWaiting)
            {
                return;
            }

            var timeoutSeconds = _apiWaitTimeoutSeconds;
            var elapsed = EditorApplication.timeSinceStartup - _apiWaitStarted;
            var buffer = 5.0f;
            if (!forceClear)
            {
                if (timeoutSeconds <= 0)
                {
                    return;
                }

                if (elapsed < timeoutSeconds + buffer)
                {
                    return;
                }
            }

            _toolStatus = "OpenAI request timed out.";
            if (_operationCts != null && !_operationCts.IsCancellationRequested)
            {
                _operationCts.Cancel();
            }
            ResetOperationFlags();
            EndApiWait();
            Repaint();
        }

        private void ResetOperationFlags()
        {
            _isSending = false;
            _isGeneratingPlan = false;
            _isCreatingRequests = false;
            _isApplyingPlan = false;
            _isApplyingStage = false;
            _isFixingErrors = false;
            _isAgentLoopActive = false;
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

            CheckApiWaitTimeout(false);
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

        private static ProtoProviderSnapshot GetSettingsSnapshot()
        {
            return ProtoProviderSettings.GetSnapshot();
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

        private async Task<ProtoPhasePlan> GeneratePhasedPlanAsync(ProtoProviderSnapshot settings, ProtoChatMessage[] baseMessages, string prompt, CancellationToken cancellationToken, string scriptIndex, string prefabIndex, string sceneIndex, string assetIndex)
        {
            if (settings == null || string.IsNullOrWhiteSpace(settings.ApiKey))
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
            var phaseOutline = await RequestPhaseOutlineAsync(settings, baseMessages, prompt, cancellationToken, currentScriptIndex, currentPrefabIndex, currentSceneIndex, currentAssetIndex).ConfigureAwait(false);
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

                var overview = await RequestPhaseOverviewAsync(settings, baseMessages, prompt, outline, cancellationToken, currentScriptIndex, currentPrefabIndex, currentSceneIndex, currentAssetIndex).ConfigureAwait(false);
                var requests = await RequestPhaseFeatureRequestsAsync(settings, baseMessages, prompt, outline, cancellationToken, currentScriptIndex, currentPrefabIndex, currentSceneIndex, currentAssetIndex).ConfigureAwait(false);
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

        private async Task<ProtoPhaseOutlinePlan> RequestPhaseOutlineAsync(ProtoProviderSnapshot settings, ProtoChatMessage[] baseMessages, string prompt, CancellationToken cancellationToken, string scriptIndex, string prefabIndex, string sceneIndex, string assetIndex)
        {
            if (settings == null || string.IsNullOrWhiteSpace(settings.ApiKey))
            {
                return null;
            }

            var messages = new List<ProtoChatMessage>(baseMessages ?? Array.Empty<ProtoChatMessage>())
            {
                new ProtoChatMessage
                {
                    role = "user",
                    content = ProtoPrompts.PhaseOutlineInstruction
                },
                new ProtoChatMessage
                {
                    role = "user",
                    content = string.Format(ProtoPrompts.ProjectIntentFormat, prompt)
                }
            };
            AppendPlanIndexMessages(messages, scriptIndex, prefabIndex, sceneIndex, assetIndex);

            await RunOnMainThread(() => BeginApiWait("phase count")).ConfigureAwait(false);
            var response = await ProtoProviderClient.SendChatAsync(settings, messages.ToArray(), _apiWaitTimeoutSeconds, cancellationToken).ConfigureAwait(false);
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

        private async Task<string> RequestPhaseOverviewAsync(ProtoProviderSnapshot settings, ProtoChatMessage[] baseMessages, string prompt, ProtoPhaseOutline outline, CancellationToken cancellationToken, string scriptIndex, string prefabIndex, string sceneIndex, string assetIndex)
        {
            if (settings == null || string.IsNullOrWhiteSpace(settings.ApiKey))
            {
                return string.Empty;
            }

            var messages = new List<ProtoChatMessage>(baseMessages ?? Array.Empty<ProtoChatMessage>())
            {
                new ProtoChatMessage
                {
                    role = "user",
                    content = ProtoPrompts.PhaseOverviewInstruction
                },
                new ProtoChatMessage
                {
                    role = "user",
                    content = string.Format(ProtoPrompts.PhaseOverviewContextFormat, prompt, outline.id, outline.name, outline.goal)
                }
            };
            AppendPlanIndexMessages(messages, scriptIndex, prefabIndex, sceneIndex, assetIndex);

            await RunOnMainThread(() => BeginApiWait($"phase {outline.id} overview")).ConfigureAwait(false);
            var response = await ProtoProviderClient.SendChatAsync(settings, messages.ToArray(), _apiWaitTimeoutSeconds, cancellationToken).ConfigureAwait(false);
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

        private async Task<ProtoFeatureRequest[]> RequestPhaseFeatureRequestsAsync(ProtoProviderSnapshot settings, ProtoChatMessage[] baseMessages, string prompt, ProtoPhaseOutline outline, CancellationToken cancellationToken, string scriptIndex, string prefabIndex, string sceneIndex, string assetIndex)
        {
            if (settings == null || string.IsNullOrWhiteSpace(settings.ApiKey))
            {
                return Array.Empty<ProtoFeatureRequest>();
            }

            var messages = new List<ProtoChatMessage>(baseMessages ?? Array.Empty<ProtoChatMessage>())
            {
                new ProtoChatMessage
                {
                    role = "user",
                    content = ProtoPrompts.FeatureRequestsInstruction
                },
                new ProtoChatMessage
                {
                    role = "user",
                    content = string.Format(ProtoPrompts.FeatureRequestsContextFormat, prompt, outline.id, outline.name, outline.goal)
                }
            };
            AppendPlanIndexMessages(messages, scriptIndex, prefabIndex, sceneIndex, assetIndex);

            await RunOnMainThread(() => BeginApiWait($"phase {outline.id} requests")).ConfigureAwait(false);
            var response = await ProtoProviderClient.SendChatAsync(settings, messages.ToArray(), _apiWaitTimeoutSeconds, cancellationToken).ConfigureAwait(false);
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
                    TagRequestsWithPhase(phase.featureRequests, phase);
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
                TagRequestsWithPhase(selected.featureRequests, selected);
                results.AddRange(selected.featureRequests);
            }

            return results;
        }

        private static List<ProtoFeatureRequest> ExpandFeatureSteps(List<ProtoFeatureRequest> requests)
        {
            if (requests == null || requests.Count == 0)
            {
                return requests ?? new List<ProtoFeatureRequest>();
            }

            var results = new List<ProtoFeatureRequest>(requests);
            var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var request in results)
            {
                if (!string.IsNullOrWhiteSpace(request?.id))
                {
                    usedIds.Add(request.id);
                }
            }

            var identitySet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var request in results)
            {
                var key = BuildRequestIdentity(request);
                if (!string.IsNullOrWhiteSpace(key))
                {
                    identitySet.Add(key);
                }
            }

            var now = DateTime.UtcNow.ToString("o");
            foreach (var request in requests)
            {
                if (request?.steps == null || request.steps.Length == 0)
                {
                    continue;
                }

                var parentType = (request.type ?? string.Empty).Trim();
                var parentTypeMatches = false;
                var generatedAny = false;

                foreach (var step in request.steps)
                {
                    if (step == null)
                    {
                        continue;
                    }

                    var stepType = string.IsNullOrWhiteSpace(step.type) ? parentType : step.type.Trim();
                    if (!IsPrimaryStepType(stepType))
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(parentType) &&
                        string.Equals(stepType, parentType, StringComparison.OrdinalIgnoreCase))
                    {
                        parentTypeMatches = true;
                    }

                    var stepName = step.name?.Trim();
                    var stepPath = string.IsNullOrWhiteSpace(step.path) ? request.path : step.path.Trim();
                    if (string.IsNullOrWhiteSpace(stepName) && string.IsNullOrWhiteSpace(stepPath))
                    {
                        continue;
                    }

                    var detail = new ProtoFeatureRequest
                    {
                        id = string.IsNullOrWhiteSpace(step.id) ? null : step.id.Trim(),
                        type = stepType,
                        name = stepName,
                        path = stepPath,
                        phaseId = request.phaseId,
                        phaseName = request.phaseName,
                        notes = string.IsNullOrWhiteSpace(step.notes)
                            ? $"Step for {request.name ?? request.type ?? "request"}."
                            : step.notes.Trim(),
                        createdAt = now,
                        updatedAt = now
                    };
                    detail.type = detail.type?.Trim();
                    detail.path = NormalizePathForType(detail.type, detail.path);
                    NormalizeScriptRequestName(detail, true);
                    detail.SetStatus(ProtoAgentRequestStatus.Todo);

                    var deps = new List<string>();
                    if (!string.IsNullOrWhiteSpace(request.id))
                    {
                        deps.Add(request.id);
                    }
                    if (step.dependsOn != null)
                    {
                        deps.AddRange(step.dependsOn);
                    }
                    detail.dependsOn = BuildDependencyArray(deps);

                    var detailKey = BuildRequestIdentity(detail);
                    if (!string.IsNullOrWhiteSpace(detailKey) && identitySet.Contains(detailKey))
                    {
                        continue;
                    }

                    AddDetailRequest(results, detail, usedIds, identitySet);
                    generatedAny = true;
                }

                if (generatedAny && parentTypeMatches)
                {
                    request.SetStatus(ProtoAgentRequestStatus.Done);
                    request.notes = AppendNote(request.notes, "Expanded into steps.");
                    request.updatedAt = now;
                }
            }

            return results;
        }

        private static bool IsPrimaryStepType(string type)
        {
            if (string.IsNullOrWhiteSpace(type))
            {
                return false;
            }

            switch (type.Trim().ToLowerInvariant())
            {
                case "folder":
                case "script":
                case "scene":
                case "prefab":
                case "material":
                case "asset":
                    return true;
                default:
                    return false;
            }
        }

        private static List<ProtoFeatureRequest> ExpandPrefabAndSceneDetails(List<ProtoFeatureRequest> requests)
        {
            if (requests == null || requests.Count == 0)
            {
                return requests ?? new List<ProtoFeatureRequest>();
            }

            var results = new List<ProtoFeatureRequest>(requests);
            var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var request in results)
            {
                if (!string.IsNullOrWhiteSpace(request?.id))
                {
                    usedIds.Add(request.id);
                }
            }

            var requestLookup = BuildRequestLookup(results);
            var scriptByName = BuildScriptRequestMap(results);
            var identitySet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var request in results)
            {
                var key = BuildRequestIdentity(request);
                if (!string.IsNullOrWhiteSpace(key))
                {
                    identitySet.Add(key);
                }
            }

            var now = DateTime.UtcNow.ToString("o");
            foreach (var request in requests)
            {
                if (request == null || string.IsNullOrWhiteSpace(request.type))
                {
                    continue;
                }

                if (string.Equals(request.type, "prefab", StringComparison.OrdinalIgnoreCase) ||
                    (string.Equals(request.type, "asset", StringComparison.OrdinalIgnoreCase) && LooksLikePrefabRequest(request)))
                {
                    AddPrefabComponentRequests(request, requestLookup, scriptByName, results, usedIds, identitySet, now);
                    continue;
                }

                if (string.Equals(request.type, "scene", StringComparison.OrdinalIgnoreCase) ||
                    (string.Equals(request.type, "asset", StringComparison.OrdinalIgnoreCase) && LooksLikeSceneRequest(request)))
                {
                    AddSceneDetailRequests(request, requestLookup, scriptByName, results, usedIds, identitySet, now);
                }
            }

            return results;
        }

        private static Dictionary<string, ProtoFeatureRequest> BuildScriptRequestMap(List<ProtoFeatureRequest> requests)
        {
            var results = new Dictionary<string, ProtoFeatureRequest>(StringComparer.OrdinalIgnoreCase);
            if (requests == null)
            {
                return results;
            }

            foreach (var request in requests)
            {
                if (request == null || !string.Equals(request.type, "script", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var name = request.name?.Trim();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    results[name] = request;
                }
            }

            return results;
        }

        private static void AddPrefabComponentRequests(
            ProtoFeatureRequest request,
            Dictionary<string, ProtoFeatureRequest> requestLookup,
            Dictionary<string, ProtoFeatureRequest> scriptByName,
            List<ProtoFeatureRequest> results,
            HashSet<string> usedIds,
            HashSet<string> identitySet,
            string timestamp)
        {
            var componentNames = CollectPrefabComponentNames(request, requestLookup);
            var prefabName = GetPrefabName(request);
            var prefabPath = BuildPlannedPrefabPath(request.path, prefabName);
            var uniqueNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var prefabLabel = string.IsNullOrWhiteSpace(prefabName) ? "Prefab" : prefabName;
            var hasStepComponents = false;

            if (request?.steps != null)
            {
                foreach (var step in request.steps)
                {
                    if (step == null || !string.Equals(step.type, "prefab_component", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var stepName = step.name?.Trim();
                    if (string.IsNullOrWhiteSpace(stepName) || !uniqueNames.Add(stepName))
                    {
                        continue;
                    }

                    hasStepComponents = true;
                    var detail = new ProtoFeatureRequest
                    {
                        id = string.IsNullOrWhiteSpace(step.id) ? null : step.id.Trim(),
                        type = "prefab_component",
                        name = stepName,
                        path = prefabPath,
                        phaseId = request.phaseId,
                        phaseName = request.phaseName,
                        notes = string.IsNullOrWhiteSpace(step.notes) ? $"Prefab component for {prefabLabel}." : step.notes,
                        createdAt = timestamp,
                        updatedAt = timestamp
                    };
                    detail.SetStatus(ProtoAgentRequestStatus.Todo);

                    var deps = new List<string>();
                    if (!string.IsNullOrWhiteSpace(request.id))
                    {
                        deps.Add(request.id);
                    }
                    if (step.dependsOn != null)
                    {
                        deps.AddRange(step.dependsOn);
                    }
                    if (scriptByName.TryGetValue(stepName, out var scriptRequest) && !string.IsNullOrWhiteSpace(scriptRequest.id))
                    {
                        deps.Add(scriptRequest.id);
                    }
                    detail.dependsOn = BuildDependencyArray(deps);

                    AddDetailRequest(results, detail, usedIds, identitySet);
                }
            }

            if (componentNames.Count == 0 && !hasStepComponents)
            {
                return;
            }

            foreach (var componentName in componentNames)
            {
                var trimmed = componentName?.Trim();
                if (string.IsNullOrWhiteSpace(trimmed) || !uniqueNames.Add(trimmed))
                {
                    continue;
                }

                var detail = new ProtoFeatureRequest
                {
                    type = "prefab_component",
                    name = trimmed,
                    path = prefabPath,
                    phaseId = request.phaseId,
                    phaseName = request.phaseName,
                    notes = $"Prefab component for {prefabLabel}.",
                    createdAt = timestamp,
                    updatedAt = timestamp
                };
                detail.SetStatus(ProtoAgentRequestStatus.Todo);

                var deps = new List<string>();
                if (!string.IsNullOrWhiteSpace(request.id))
                {
                    deps.Add(request.id);
                }
                if (scriptByName.TryGetValue(trimmed, out var scriptRequest) && !string.IsNullOrWhiteSpace(scriptRequest.id))
                {
                    deps.Add(scriptRequest.id);
                }
                detail.dependsOn = BuildDependencyArray(deps);

                AddDetailRequest(results, detail, usedIds, identitySet);
            }
        }

        private static void AddSceneDetailRequests(
            ProtoFeatureRequest request,
            Dictionary<string, ProtoFeatureRequest> requestLookup,
            Dictionary<string, ProtoFeatureRequest> scriptByName,
            List<ProtoFeatureRequest> results,
            HashSet<string> usedIds,
            HashSet<string> identitySet,
            string timestamp)
        {
            var sceneName = GetSceneName(request);
            var scenePath = BuildPlannedScenePath(request.path, sceneName);
            var sceneId = request.id ?? string.Empty;

            var prefabNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var managerNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (request?.steps != null)
            {
                foreach (var step in request.steps)
                {
                    if (step == null || string.IsNullOrWhiteSpace(step.type))
                    {
                        continue;
                    }

                    if (string.Equals(step.type, "scene_prefab", StringComparison.OrdinalIgnoreCase))
                    {
                        var stepName = step.name?.Trim();
                        if (string.IsNullOrWhiteSpace(stepName) || !prefabNames.Add(stepName))
                        {
                            continue;
                        }

                        var detail = new ProtoFeatureRequest
                        {
                            id = string.IsNullOrWhiteSpace(step.id) ? null : step.id.Trim(),
                            type = "scene_prefab",
                            name = stepName,
                            path = scenePath,
                            phaseId = request.phaseId,
                            phaseName = request.phaseName,
                            notes = string.IsNullOrWhiteSpace(step.notes) ? $"prefabs: {stepName}" : step.notes,
                            createdAt = timestamp,
                            updatedAt = timestamp
                        };
                        detail.SetStatus(ProtoAgentRequestStatus.Todo);

                        var deps = new List<string>();
                        if (!string.IsNullOrWhiteSpace(sceneId))
                        {
                            deps.Add(sceneId);
                        }
                        if (step.dependsOn != null)
                        {
                            deps.AddRange(step.dependsOn);
                        }

                        var prefabRequestId = FindPrefabRequestIdByName(stepName, requestLookup);
                        if (!string.IsNullOrWhiteSpace(prefabRequestId))
                        {
                            deps.Add(prefabRequestId);
                        }

                        detail.dependsOn = BuildDependencyArray(deps);
                        AddDetailRequest(results, detail, usedIds, identitySet);
                        continue;
                    }

                    if (string.Equals(step.type, "scene_manager", StringComparison.OrdinalIgnoreCase))
                    {
                        var stepName = step.name?.Trim();
                        if (string.IsNullOrWhiteSpace(stepName) || !managerNames.Add(stepName))
                        {
                            continue;
                        }

                        var detail = new ProtoFeatureRequest
                        {
                            id = string.IsNullOrWhiteSpace(step.id) ? null : step.id.Trim(),
                            type = "scene_manager",
                            name = stepName,
                            path = scenePath,
                            phaseId = request.phaseId,
                            phaseName = request.phaseName,
                            notes = string.IsNullOrWhiteSpace(step.notes) ? $"managers: {stepName}" : step.notes,
                            createdAt = timestamp,
                            updatedAt = timestamp
                        };
                        detail.SetStatus(ProtoAgentRequestStatus.Todo);

                        var deps = new List<string>();
                        if (!string.IsNullOrWhiteSpace(sceneId))
                        {
                            deps.Add(sceneId);
                        }
                        if (step.dependsOn != null)
                        {
                            deps.AddRange(step.dependsOn);
                        }
                        if (scriptByName.TryGetValue(stepName, out var scriptRequest) && !string.IsNullOrWhiteSpace(scriptRequest.id))
                        {
                            deps.Add(scriptRequest.id);
                        }

                        detail.dependsOn = BuildDependencyArray(deps);
                        AddDetailRequest(results, detail, usedIds, identitySet);
                    }
                }
            }

            if (request.dependsOn != null && requestLookup != null)
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

                    if (!string.Equals(depRequest.type, "prefab", StringComparison.OrdinalIgnoreCase) &&
                        !(string.Equals(depRequest.type, "asset", StringComparison.OrdinalIgnoreCase) && LooksLikePrefabRequest(depRequest)))
                    {
                        continue;
                    }

                    var prefabName = GetPrefabName(depRequest);
                    if (string.IsNullOrWhiteSpace(prefabName) || !prefabNames.Add(prefabName))
                    {
                        continue;
                    }

                    var detail = new ProtoFeatureRequest
                    {
                        type = "scene_prefab",
                        name = prefabName,
                        path = scenePath,
                        phaseId = request.phaseId,
                        phaseName = request.phaseName,
                        createdAt = timestamp,
                        updatedAt = timestamp
                    };
                    detail.SetStatus(ProtoAgentRequestStatus.Todo);

                    var deps = new List<string>();
                    if (!string.IsNullOrWhiteSpace(sceneId))
                    {
                        deps.Add(sceneId);
                    }
                    deps.Add(depId);
                    detail.dependsOn = BuildDependencyArray(deps);

                    AddDetailRequest(results, detail, usedIds, identitySet);
                }
            }

            foreach (var name in ParseScenePrefabNamesFromNotes(request.notes))
            {
                if (string.IsNullOrWhiteSpace(name) || !prefabNames.Add(name.Trim()))
                {
                    continue;
                }

                var detail = new ProtoFeatureRequest
                {
                    type = "scene_prefab",
                    name = name.Trim(),
                    path = scenePath,
                    phaseId = request.phaseId,
                    phaseName = request.phaseName,
                    notes = $"prefabs: {name.Trim()}",
                    createdAt = timestamp,
                    updatedAt = timestamp
                };
                detail.SetStatus(ProtoAgentRequestStatus.Todo);

                if (!string.IsNullOrWhiteSpace(sceneId))
                {
                    detail.dependsOn = BuildDependencyArray(new List<string> { sceneId });
                }

                AddDetailRequest(results, detail, usedIds, identitySet);
            }

            var sceneManagerNames = CollectSceneManagerComponents(request, requestLookup);
            foreach (var managerName in sceneManagerNames)
            {
                var trimmed = managerName?.Trim();
                if (string.IsNullOrWhiteSpace(trimmed) || !managerNames.Add(trimmed))
                {
                    continue;
                }

                var detail = new ProtoFeatureRequest
                {
                    type = "scene_manager",
                    name = trimmed,
                    path = scenePath,
                    phaseId = request.phaseId,
                    phaseName = request.phaseName,
                    notes = $"managers: {trimmed}",
                    createdAt = timestamp,
                    updatedAt = timestamp
                };
                detail.SetStatus(ProtoAgentRequestStatus.Todo);

                var deps = new List<string>();
                if (!string.IsNullOrWhiteSpace(sceneId))
                {
                    deps.Add(sceneId);
                }
                if (scriptByName.TryGetValue(trimmed, out var scriptRequest) && !string.IsNullOrWhiteSpace(scriptRequest.id))
                {
                    deps.Add(scriptRequest.id);
                }
                detail.dependsOn = BuildDependencyArray(deps);

                AddDetailRequest(results, detail, usedIds, identitySet);
            }
        }

        private static string[] BuildDependencyArray(List<string> dependencies)
        {
            if (dependencies == null || dependencies.Count == 0)
            {
                return null;
            }

            var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var results = new List<string>();
            foreach (var dep in dependencies)
            {
                var trimmed = dep?.Trim();
                if (string.IsNullOrWhiteSpace(trimmed))
                {
                    continue;
                }

                if (unique.Add(trimmed))
                {
                    results.Add(trimmed);
                }
            }

            return results.Count == 0 ? null : results.ToArray();
        }

        private static string FindPrefabRequestIdByName(string prefabName, Dictionary<string, ProtoFeatureRequest> requestLookup)
        {
            if (string.IsNullOrWhiteSpace(prefabName) || requestLookup == null)
            {
                return string.Empty;
            }

            var trimmed = prefabName.Trim();
            foreach (var request in requestLookup.Values)
            {
                if (request == null)
                {
                    continue;
                }

                var isPrefab = string.Equals(request.type, "prefab", StringComparison.OrdinalIgnoreCase)
                    || (string.Equals(request.type, "asset", StringComparison.OrdinalIgnoreCase) && LooksLikePrefabRequest(request));
                if (!isPrefab)
                {
                    continue;
                }

                var name = GetPrefabName(request);
                if (string.Equals(name?.Trim(), trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    return request.id ?? string.Empty;
                }
            }

            return string.Empty;
        }

        private static void AddDetailRequest(
            List<ProtoFeatureRequest> results,
            ProtoFeatureRequest detail,
            HashSet<string> usedIds,
            HashSet<string> identitySet)
        {
            if (detail == null)
            {
                return;
            }

            var key = BuildRequestIdentity(detail);
            if (!string.IsNullOrWhiteSpace(key) && identitySet.Contains(key))
            {
                return;
            }

            detail.id = EnsureRequestId(detail, usedIds);
            if (!string.IsNullOrWhiteSpace(key))
            {
                identitySet.Add(key);
            }
            results.Add(detail);
        }

        private static void TagRequestsWithPhase(ProtoFeatureRequest[] requests, ProtoPlanPhase phase)
        {
            if (requests == null || phase == null)
            {
                return;
            }

            var phaseId = phase.id?.Trim();
            var phaseName = string.IsNullOrWhiteSpace(phase.name) ? phase.id : phase.name;
            foreach (var request in requests)
            {
                if (request == null)
                {
                    continue;
                }

                request.phaseId = phaseId;
                request.phaseName = phaseName;
            }
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
                request.SetStatus(ProtoAgentRequestStatus.Todo);
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
                        status = ProtoAgentRequestStatus.Todo.ToNormalizedString(),
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

                request.SetStatus(ProtoAgentRequestStatus.Done);
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
                case "prefab_component":
                {
                    var prefabPath = request.path ?? string.Empty;
                    var componentName = request.name ?? string.Empty;
                    var normalizedPrefab = string.IsNullOrWhiteSpace(prefabPath) ? string.Empty : EnsureProjectPath(prefabPath);
                    return string.IsNullOrWhiteSpace(normalizedPrefab) || string.IsNullOrWhiteSpace(componentName)
                        ? string.Empty
                        : $"prefab_component:{normalizedPrefab}:{componentName.Trim()}";
                }
                case "scene":
                {
                    var name = GetSceneName(request);
                    var path = BuildPlannedScenePath(request.path, name);
                    return string.IsNullOrWhiteSpace(path) ? string.Empty : $"scene:{path}";
                }
                case "scene_prefab":
                {
                    var scenePath = string.IsNullOrWhiteSpace(request.path) ? string.Empty : EnsureProjectPath(request.path);
                    var prefabName = request.name ?? string.Empty;
                    return string.IsNullOrWhiteSpace(scenePath) || string.IsNullOrWhiteSpace(prefabName)
                        ? string.Empty
                        : $"scene_prefab:{scenePath}:{prefabName.Trim()}";
                }
                case "scene_manager":
                {
                    var scenePath = string.IsNullOrWhiteSpace(request.path) ? string.Empty : EnsureProjectPath(request.path);
                    var managerName = request.name ?? string.Empty;
                    return string.IsNullOrWhiteSpace(scenePath) || string.IsNullOrWhiteSpace(managerName)
                        ? string.Empty
                        : $"scene_manager:{scenePath}:{managerName.Trim()}";
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

        private struct AgentActionResult
        {
            public bool stop;
            public string historyEntry;
            public string outcomeLabel;
        }

        private struct AgentReadSnapshot
        {
            public bool truncated;
        }

        [Serializable]
        private sealed class ProtoSceneEditOperation
        {
            public string type;
            public string name;
            public string parent;
            public string prefab;
            public string component;
            public string field;
            public string value;
            public string reference;
            public string lightType;
            public float intensity;
            public float range;
            public float spotAngle;
            public float[] position;
            public float[] rotation;
            public float[] scale;
            public float[] offset;
            public string target;
            public float[] vector;
            public float[] color;
        }

        private sealed class ProtoAgentCommandWindow : EditorWindow
        {
            private ProtoChatWindow _owner;
            private Vector2 _scroll;
            private string _reviewScriptQuery = string.Empty;

            public static void Show(ProtoChatWindow owner)
            {
                if (owner == null)
                {
                    return;
                }

                var window = CreateInstance<ProtoAgentCommandWindow>();
                window._owner = owner;
                window.titleContent = new GUIContent("Command");
                window.minSize = new Vector2(360f, 420f);
                window.ShowUtility();
            }

            private void OnGUI()
            {
                if (_owner == null)
                {
                    EditorGUILayout.HelpBox("Proto Chat is not available.", MessageType.Info);
                    if (GUILayout.Button("Close"))
                    {
                        Close();
                    }
                    return;
                }

                var isBusy = _owner.IsOperationActive() || _owner._isCompactingSession;
                var hasApiKey = ProtoProviderSettings.HasApiKey();
                var hasPlan = !string.IsNullOrWhiteSpace(_owner._lastPlanJson);

                EditorGUILayout.HelpBox("Choose a command to run. Some actions require an API key.", MessageType.Info);

                using (var scroll = new EditorGUILayout.ScrollViewScope(_scroll))
                {
                    _scroll = scroll.scrollPosition;

                    EditorGUILayout.LabelField("Agent", EditorStyles.boldLabel);
                    using (new EditorGUI.DisabledScope(isBusy || !hasApiKey))
                    {
                        if (GUILayout.Button("Continue Agent"))
                        {
                            _owner.StartAgentLoop("continue");
                            Close();
                            return;
                        }
                    }

                    EditorGUILayout.Space(6f);
                    EditorGUILayout.LabelField("Planning", EditorStyles.boldLabel);
                    using (new EditorGUI.DisabledScope(isBusy || !hasApiKey))
                    {
                        if (GUILayout.Button("Generate Plan"))
                        {
                            _ = _owner.GeneratePlanAsync();
                            Close();
                            return;
                        }
                    }

                    using (new EditorGUI.DisabledScope(isBusy || !hasApiKey || !hasPlan))
                    {
                        if (GUILayout.Button("Create Feature Requests"))
                        {
                            _ = _owner.CreateFeatureRequestsAsync(_owner._lastPlanJson);
                            Close();
                            return;
                        }

                        if (GUILayout.Button("Apply Plan"))
                        {
                            _ = _owner.ApplyPlanAsync();
                            Close();
                            return;
                        }
                    }

                    EditorGUILayout.Space(6f);
                    EditorGUILayout.LabelField("Apply Stage", EditorStyles.boldLabel);
                    using (new EditorGUI.DisabledScope(isBusy || !hasApiKey || !hasPlan))
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            if (GUILayout.Button("Folders"))
                            {
                                _ = _owner.ApplyPlanStageAsync("folders", new[] { "folder" });
                                Close();
                                return;
                            }
                            if (GUILayout.Button("Scripts"))
                            {
                                _ = _owner.ApplyPlanStageAsync("scripts", new[] { "script" });
                                Close();
                                return;
                            }
                        }

                        using (new EditorGUILayout.HorizontalScope())
                        {
                            if (GUILayout.Button("Materials"))
                            {
                                _ = _owner.ApplyPlanStageAsync("materials", new[] { "material" });
                                Close();
                                return;
                            }
                            if (GUILayout.Button("Prefabs"))
                            {
                                _ = _owner.ApplyPlanStageAsync("prefabs", new[] { "prefab", "prefab_component" });
                                Close();
                                return;
                            }
                        }

                        using (new EditorGUILayout.HorizontalScope())
                        {
                            if (GUILayout.Button("Scenes"))
                            {
                                _ = _owner.ApplyPlanStageAsync("scenes", new[] { "scene", "scene_prefab", "scene_manager" });
                                Close();
                                return;
                            }
                            if (GUILayout.Button("Assets"))
                            {
                                _ = _owner.ApplyPlanStageAsync("assets", new[] { "asset" });
                                Close();
                                return;
                            }
                        }
                    }

                    EditorGUILayout.Space(6f);
                    EditorGUILayout.LabelField("Fix", EditorStyles.boldLabel);
                    using (new EditorGUI.DisabledScope(isBusy || !hasApiKey))
                    {
                        if (GUILayout.Button("Fix Step"))
                        {
                            _ = _owner.RunFixPassAsync();
                            Close();
                            return;
                        }
                    }

                    EditorGUILayout.Space(6f);
                    EditorGUILayout.LabelField("Review Script", EditorStyles.boldLabel);
                    _reviewScriptQuery = EditorGUILayout.TextField("Script", _reviewScriptQuery);
                    using (new EditorGUI.DisabledScope(isBusy || !hasApiKey || string.IsNullOrWhiteSpace(_reviewScriptQuery)))
                    {
                        if (GUILayout.Button("Review"))
                        {
                            var request = $"review {_reviewScriptQuery.Trim()}";
                            _ = _owner.ReviewScriptAsync(request);
                            Close();
                            return;
                        }
                    }
                }
            }
        }

        [Serializable]
        private sealed class ProtoAgentAction
        {
            public string action;
            public string path;
            public string content;
            public string query;
            public string stage;
            public string scope;
            public string message;
            public int lineStart;
            public int lineEnd;
            public string range;
            public string scene;
            public ProtoSceneEditOperation[] edits;
            public ProtoSceneEditOperation edit;
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

    }
}
