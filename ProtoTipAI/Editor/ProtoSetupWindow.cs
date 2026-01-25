using System;
using UnityEditor;
using UnityEngine;

namespace ProtoTipAI.Editor
{
    public sealed class ProtoSetupWindow : EditorWindow
    {
        private static readonly string[] ModelOptions =
        {
            "gpt-5.2",
            "gpt-5.1",
            "gpt-5",
            "gpt-4.1",
            "gpt-4o",
            "gpt-4o-mini",
            "gpt-4-turbo",
            "gpt-4"
        };

        private string _apiKey;
        private string _model;
        private int _modelIndex;
        private string _projectGoal;
        private string _projectSummary;
        private int _selectedProviderIndex;
        private string[] _providerDisplayNames = Array.Empty<string>();
        private string _customBaseUrl;
        private bool _autoConfirmTools;
        private bool _fullAgentMode;
        private Vector2 _projectGoalScroll;
        private Vector2 _projectSummaryScroll;
        private static GUIStyle _multiLineStyle;

        private static GUIStyle MultiLineStyle
        {
            get
            {
                if (_multiLineStyle == null)
                {
                    _multiLineStyle = new GUIStyle(EditorStyles.textArea)
                    {
                        wordWrap = true
                    };
                }

                return _multiLineStyle;
            }
        }

        [MenuItem("Proto/Setup")]
        public static void ShowWindow()
        {
            var window = GetWindow<ProtoSetupWindow>(true, "Proto Setup");
            window.minSize = new Vector2(420f, 320f);
            window.ShowUtility();
        }

        private void OnEnable()
        {
            _apiKey = ProtoProviderSettings.GetApiKey();
            _model = ProtoProviderSettings.GetModel();
            _modelIndex = Array.IndexOf(ModelOptions, _model);
            if (_modelIndex < 0)
            {
                _modelIndex = 0;
                _model = ModelOptions[_modelIndex];
            }
            _projectGoal = ProtoProjectSettings.GetProjectGoal();
            _projectSummary = ProtoProjectSettings.GetProjectSummary();
            _providerDisplayNames = ProtoProviderRegistry.DisplayNames;
            _selectedProviderIndex = ProtoProviderRegistry.IndexOf(ProtoProviderSettings.GetProviderId());
            _customBaseUrl = ProtoProviderSettings.GetCustomBaseUrl();
            _autoConfirmTools = ProtoToolSettings.GetAutoConfirm();
            _fullAgentMode = ProtoToolSettings.GetFullAgentMode();
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Provider", EditorStyles.boldLabel);
            EditorGUILayout.Space(4f);

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                _selectedProviderIndex = Mathf.Clamp(_selectedProviderIndex, 0, Math.Max(0, _providerDisplayNames.Length - 1));
                _selectedProviderIndex = EditorGUILayout.Popup("Provider", _selectedProviderIndex, _providerDisplayNames);
                var provider = ProtoProviderRegistry.All[_selectedProviderIndex];

                _model = EditorGUILayout.TextField("Model", _model);
                _apiKey = EditorGUILayout.PasswordField("API Key", _apiKey);
                _customBaseUrl = EditorGUILayout.TextField("Custom API URL", _customBaseUrl);

                EditorGUILayout.LabelField("Endpoint", provider.GetChatEndpoint());
                if (provider.RecommendedModels.Length > 0)
                {
                    EditorGUILayout.LabelField("Recommended", string.Join(", ", provider.RecommendedModels));
                }
            }

            EditorGUILayout.Space(6f);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Agent Tools", EditorStyles.boldLabel);
                using (new EditorGUI.DisabledScope(_fullAgentMode))
                {
                    _autoConfirmTools = EditorGUILayout.ToggleLeft(
                        "Apply tool changes automatically",
                        _autoConfirmTools);
                }
                _fullAgentMode = EditorGUILayout.ToggleLeft(
                    "Full agent mode (skip stage confirmations)",
                    _fullAgentMode);
                if (_fullAgentMode)
                {
                    _autoConfirmTools = true;
                }
                EditorGUILayout.HelpBox("When disabled, Unity asks before tool operations modify files. Full agent mode keeps stage/apply confirmations off.", MessageType.Info);
            }

            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("Project Goal", EditorStyles.boldLabel);
            var goalHeight = Mathf.Max(72f, EditorGUIUtility.singleLineHeight * 4f);
            using (var scroll = new EditorGUILayout.ScrollViewScope(_projectGoalScroll, GUILayout.Height(goalHeight)))
            {
                _projectGoalScroll = scroll.scrollPosition;
                _projectGoal = EditorGUILayout.TextArea(_projectGoal, MultiLineStyle, GUILayout.ExpandHeight(true));
            }

            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("Project Summary", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Refresh Summary", GUILayout.Width(140f)))
                {
                    _projectSummary = ProtoProjectContext.BuildProjectSummary();
                }
            }
            var summaryHeight = Mathf.Max(120f, EditorGUIUtility.singleLineHeight * 6f);
            using (var scroll = new EditorGUILayout.ScrollViewScope(_projectSummaryScroll, GUILayout.Height(summaryHeight)))
            {
                _projectSummaryScroll = scroll.scrollPosition;
                _projectSummary = EditorGUILayout.TextArea(_projectSummary, MultiLineStyle, GUILayout.ExpandHeight(true));
            }

            EditorGUILayout.Space(8f);
            EditorGUILayout.HelpBox("API keys are stored locally in EditorPrefs for this machine.", MessageType.Info);

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Save", GUILayout.Width(120f)))
                {
                    var provider = ProtoProviderRegistry.All[_selectedProviderIndex];
                    ProtoProviderSettings.SetProviderId(provider.Id);
                    ProtoProviderSettings.SetModel(_model);
                    ProtoProviderSettings.SetApiKey(_apiKey);
                    ProtoProviderSettings.SetCustomBaseUrl(_customBaseUrl);
                    ProtoProjectSettings.SetProjectGoal(_projectGoal);
                    ProtoProjectSettings.SetProjectSummary(_projectSummary);
                    ProtoToolSettings.SetAutoConfirm(_autoConfirmTools);
                    ProtoToolSettings.SetFullAgentMode(_fullAgentMode);
                    Close();
                }
            }
        }
    }
}
