using UnityEditor;
using UnityEngine;

namespace ProtoTipAI.Editor
{
    public sealed class ProtoControlWindow : EditorWindow
    {
        private ProtoChatWindow _chatWindow;

        [MenuItem("Proto/Control")]
        public static void ShowWindow()
        {
            var window = GetWindow<ProtoControlWindow>("Proto Control");
            window.minSize = new Vector2(520f, 420f);
        }

        private void OnEnable()
        {
            _chatWindow = ProtoChatWindow.FindWindow();
            EditorApplication.update += OnEditorUpdate;
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
        }

        private void OnEditorUpdate()
        {
            Repaint();
        }

        private void OnGUI()
        {
            _chatWindow = ProtoChatWindow.FindWindow();
            if (_chatWindow == null)
            {
                EditorGUILayout.HelpBox("Open Proto Chat to use control tools.", MessageType.Info);
                if (GUILayout.Button("Open Proto Chat"))
                {
                    ProtoChatWindow.ShowWindow();
                }
                return;
            }

            _chatWindow.DrawControlPanel();
        }
    }
}
