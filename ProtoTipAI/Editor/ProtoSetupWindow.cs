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

        private string _token;
        private string _model;
        private int _modelIndex;
        private string _projectGoal;
        private string _projectSummary;

        [MenuItem("Proto/Setup")]
        public static void ShowWindow()
        {
            var window = GetWindow<ProtoSetupWindow>(true, "Proto Setup");
            window.minSize = new Vector2(420f, 180f);
            window.ShowUtility();
        }

        private void OnEnable()
        {
            _token = ProtoOpenAISettings.GetToken();
            _model = ProtoOpenAISettings.GetModel();
            _modelIndex = Array.IndexOf(ModelOptions, _model);
            if (_modelIndex < 0)
            {
                _modelIndex = 0;
                _model = ModelOptions[_modelIndex];
            }
            _projectGoal = ProtoOpenAISettings.GetProjectGoal();
            _projectSummary = ProtoOpenAISettings.GetProjectSummary();
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("OpenAI", EditorStyles.boldLabel);
            EditorGUILayout.Space(4f);

            _token = EditorGUILayout.PasswordField("Token", _token);
            _modelIndex = EditorGUILayout.Popup("Model", _modelIndex, ModelOptions);
            _model = ModelOptions[_modelIndex];

            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("Project Goal", EditorStyles.boldLabel);
            _projectGoal = EditorGUILayout.TextArea(_projectGoal, GUILayout.MinHeight(50f));

            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("Project Summary", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Refresh Summary", GUILayout.Width(140f)))
                {
                    _projectSummary = ProtoProjectContext.BuildProjectSummary();
                }
            }
            _projectSummary = EditorGUILayout.TextArea(_projectSummary, GUILayout.MinHeight(90f));

            EditorGUILayout.Space(8f);
            EditorGUILayout.HelpBox("Token is stored locally in EditorPrefs for this machine.", MessageType.Info);

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Save", GUILayout.Width(120f)))
                {
                    ProtoOpenAISettings.SetToken(_token);
                    ProtoOpenAISettings.SetModel(_model);
                    ProtoOpenAISettings.SetProjectGoal(_projectGoal);
                    ProtoOpenAISettings.SetProjectSummary(_projectSummary);
                    Close();
                }
            }
        }
    }
}
