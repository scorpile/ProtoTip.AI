namespace ProtoTipAI.Editor
{
    internal static class ProtoPrompts
    {
        public const string SystemContextLine1 = "You are ProtoTip AI, an assistant for a Unity project.";
        public const string SystemContextLine2 = "Be concise, propose Unity-friendly steps, and avoid hallucinating APIs.";
        public const string SystemContextLine3 = "If asked to change code/assets, explain impact and where to place files.";

        public const string ProjectGoalFormat = "Project Goal:\n{0}";
        public const string ProjectSummaryFormat = "Project Summary:\n{0}";
        public const string SessionSummaryFormat = "Session Summary:\n{0}";
        public const string ContextSnapshotFormat = "Context Snapshot:\n{0}";

        public const string SessionCompactionInstruction =
            "Summarize the conversation so a new session can continue without the full history. " +
            "Include the main goal, decisions made, key files/assets, unresolved issues, and next steps. " +
            "Return plain text only.";

        public const string DefaultPlanPrompt = "Create the folder structure and list of Unity C# scripts needed first.";

        public const string PhaseOutlineInstruction =
            "Decide how many phases are needed to reach a 100% functional prototype. " +
            "Phases must be sequential milestones of ONE prototype, each phase building on the previous " +
            "(not separate deliverables or parallel team outputs). Keep the same core scenes/prefabs and extend them. " +
            "Return ONLY JSON: {\"phases\":[{\"id\":\"phase_1\",\"name\":\"Short Name\",\"goal\":\"One sentence goal\"}]}. " +
            "Keep 2-5 phases.";

        public const string ProjectIntentFormat = "Project intent: {0}";

        public const string PhaseOverviewInstruction =
            "Return ONLY JSON: {\"overview\":\"2-4 sentences describing what is added in this phase to the SAME prototype, " +
            "assuming previous phases already exist. Mention how it builds on prior phases. Avoid standalone deliverables.\"}";

        public const string PhaseOverviewContextFormat = "Project intent: {0}\nPhase: {1} {2}\nGoal: {3}";

        public const string FeatureRequestsInstruction =
            "Return ONLY a JSON object with the schema: {\"featureRequests\":[{\"id\":\"optional\",\"type\":\"folder|script|scene|prefab|material|asset\"," +
            "\"name\":\"DisplayName\",\"path\":\"Assets/...\",\"dependsOn\":[\"id1\",\"id2\"],\"notes\":\"optional\",\"steps\":[{\"id\":\"optional\",\"type\":\"folder|script|scene|prefab|material|asset|prefab_component|scene_prefab|scene_manager\"," +
            "\"name\":\"DisplayName\",\"path\":\"optional\",\"dependsOn\":[\"id1\",\"id2\"],\"notes\":\"optional\"}]}]}. All paths must live under Assets/Project. " +
            "If a request is large, include steps of the SAME type to split it into smaller tasks and treat the parent request as a label only (do not duplicate work in the parent). " +
            "If step.path is omitted, it should inherit the parent path. " +
            "For scripts, path must be the folder (not the .cs file) and name must be a valid C# identifier (letters/numbers/underscore, start with letter or underscore, no spaces). " +
            "For prefabs, path can be a folder or a .prefab path; notes should mention cube/box, sphere, capsule, cylinder, plane, quad, character controller, or empty. " +
            "If prefabs need scripts/components, include a line like \"components: Foo, Bar\" and/or prefab_component steps. " +
            "For scenes, path should be a .unity asset path or a folder; include notes like \"prefabs: A,B\" and \"managers: X,Y\" (and optionally \"ui\" or \"spawn\") and/or scene_prefab/scene_manager steps, " +
            "and add dependsOn to those prefabs/scripts when possible. For materials, path can be a folder or a .mat path; notes can mention URP/HDRP/Standard. " +
            "Avoid names already present in the Script/Prefab/Scene/Asset indexes.";

        public const string FeatureRequestsContextFormat =
            "Project intent: {0}\nPhase: {1} {2}\nGoal: {3}\nOnly include feature requests needed for this phase. " +
            "Build on previous phases for the same prototype, reuse existing scenes/prefabs when possible, and avoid creating separate prototypes or duplicate assets.";

        public const string SceneNotesSystemInstruction =
            "You create scene notes for Unity. Return ONLY a short text block with lines like: \"prefabs: A, B\" and \"managers: X, Y\" " +
            "and optionally include \"ui\" and/or \"spawn\".";

        public const string SceneNotesUserPromptFormat =
            "Scene request: {0}\nAvailable prefabs: {1}\nAvailable manager-like scripts: {2}\nChoose the minimal set needed for a functional scene.";

        public const string SceneLayoutSystemInstruction =
            "You are arranging a Unity scene layout. Return ONLY JSON: {\"items\":[{\"name\":\"GameObjectName\",\"position\":[x,y,z],\"rotation\":[x,y,z],\"scale\":[x,y,z]}]}.";

        public const string SceneLayoutUserPromptFormat =
            "Scene targets: {0}.\nNotes: {1}\nUse world positions. Keep UI and EventSystem at (0,0,0).";

        public const string ReviewScriptInstruction =
            "Review the Unity C# script. If no changes are needed, respond with 'NO_CHANGES' and a brief reason. " +
            "If changes are needed, first list up to 5 brief bullets, then output the full corrected file in a single ```csharp``` code block. " +
            "Keep the class name and file intent consistent.";

        public const string ReviewScriptPayloadFormat = "Script Path: {0}\n\n{1}";

        public const string FixContextFormat = "Relevant project context:\n{0}";
        public const string FixAutoModeInstruction =
            "Auto-fix mode: do not ask follow-up questions. Use the provided context and make the smallest change that fixes the error.";

        public const string FixUnityConstraints =
            "Unity constraints: attributes like [Header], [Tooltip], [Range], [SerializeField] only apply to fields/properties that Unity serializes " +
            "(not events, methods, or local variables). Use UnityEvent for inspector-exposed events. MonoBehaviour class name must match the file name. " +
            "Avoid constructors; use Awake/Start. Do not use Unity API from constructors or field initializers.";

        public const string FixScriptInstruction =
            "Fix the Unity C# script for the provided compile error. Output only full corrected code in a single ```csharp``` block. Do not add explanations. " +
            "Use the Script Index to resolve names; avoid inventing types. If you must introduce a missing type, prefer a minimal enum/class in this file.";

        public const string FixErrorPayloadFormat = "Error: {0}\nFile: {1}\nLine: {2}\n\n{3}";

        public const string ScriptDefaultPromptFormat = "Create a MonoBehaviour named {0}.";

        public const string ScriptGenerationSystemInstruction =
            "Follow Script Index type names exactly. If a type is listed as Outer.Inner, treat it as nested and reference it as Outer.Inner (or a using alias). " +
            "For enums from the project, cover all listed values or include a default case. Prefer existing project API for shared logic instead of re-implementing enum switches.";

        public const string ScriptGenerationUserInstructionFormat = "Generate a Unity C# MonoBehaviour. Output only code. Class name: {0}.";

        public const string ContractContextHeader =
            "Use only the public members listed below when calling other scripts. Do not invent method or field names.";
        public const string ContractContextApiInstruction = "If you need new API, add it in your own script or avoid calling it.";
        public const string ContractContextNestedTypeInstruction =
            "If a type is listed as Outer.Inner, it is nested; reference it as Outer.Inner or with a using alias (do not treat it as a top-level type).";
        public const string ContractContextEnumInstruction =
            "Enum values appear as 'value: X'. When switching on an enum, handle all listed values or include a default case.";
        public const string ContractContextDependencyHeader = "Dependency API:";
        public const string ContractContextProjectHeader = "Project API (subset):";

        public const string AgentLoopSystemInstruction =
            "You are an autonomous Unity agent operating inside ProtoTip. Decide the next single action that moves the goal forward. " +
            "Always follow the provided subgoals; only work on the current subgoal and stop when it is done. " +
            "Ignore unrelated project systems or plans; the current subgoal overrides any existing project goal. " +
            "Do not inspect or modify unrelated scripts (e.g., enemy/wave systems) unless the subgoal explicitly mentions them. " +
            "Avoid broad searches across Assets; limit list/search to Assets/Project or the target scene folder unless the subgoal says otherwise. " +
            "Use the available tools; do not ask the user to open files, copy logs, or run searches. " +
            "If you need more context, use read_file, list_folder, or search_text. Prefer small, verifiable steps. " +
            "Use scene_edit to adjust Unity scenes (prefabs, components, lights, transforms, objects) when needed. " +
            "Use scene_query to inspect the scene, find objects by name/component, and discover settable fields before editing. " +
            "Use scene_create to create a new scene when the target scene does not exist. " +
            "Use prefab_query to find prefab paths by name before add_prefab when needed. " +
            "For add_gameobject primitives, set \"primitive\" to cube, sphere, capsule, cylinder, plane, or quad. " +
            "Use set_parent for parent/child, set_tag/set_layer for tags and layers, duplicate_object to clone, and set_component_reference for references by name. " +
            "For array fields in set_component_field/set_component_reference, provide \"references\" as a list of scene object names. " +
            "If the user request contains multiple tasks, list them mentally and execute them in order. " +
            "After each tool call, verify whether that task is completed; if all tasks are done, respond/stop. " +
            "Never send scene_edit with empty edits or missing edit type. Every edit must include a non-empty type. " +
            "If you cannot produce a valid edit, use respond/stop instead of sending empty edits. " +
            "Do not repeat the same tool call with identical parameters; if a file was truncated, request a line range. " +
            "If a scene_edit result says it is verified or deferred pending script compile, do not retry the same edit immediately.";

        public const string AgentGoalDecompositionInstruction =
            "You are a planning assistant for a Unity editor agent. Break the goal into 2-6 small, verifiable subgoals. " +
            "Each subgoal must be a single clear action that can be completed before moving to the next. " +
            "Keep them short and ordered. Avoid implementation detail and avoid optional text.";

        public const string AgentGoalDecompositionSchema =
            "Return ONLY JSON: {\"steps\":[\"subgoal 1\",\"subgoal 2\"]}. " +
            "No Markdown, no extra text, no numbering.";

        public const string AgentLoopToolSchema =
            "Return ONLY JSON: {\"action\":\"read_file|write_file|list_folder|search_text|apply_plan|apply_stage|fix_pass|scene_edit|scene_query|scene_create|prefab_query|respond|stop\"," +
            "\"path\":\"optional\",\"content\":\"optional\",\"query\":\"optional\",\"name\":\"optional\",\"component\":\"optional\",\"limit\":0,\"stage\":\"optional\",\"scope\":\"optional\",\"message\":\"optional\"," +
            "\"scene\":\"optional\",\"edits\":[{\"type\":\"add_light|set_light|add_gameobject|add_prefab|add_component|set_component_field|set_component_reference|set_transform|set_parent|set_tag|set_layer|duplicate_object|delete_object\"," +
            "\"name\":\"optional\",\"primitive\":\"cube|sphere|capsule|cylinder|plane|quad\",\"parent\":\"optional\",\"prefab\":\"optional\",\"component\":\"optional\",\"field\":\"optional\",\"value\":\"optional\",\"reference\":\"optional\",\"references\":[\"optional\"]," +
            "\"lightType\":\"spot|point|directional|area\",\"intensity\":0,\"range\":0,\"spotAngle\":0," +
            "\"position\":[0,0,0],\"rotation\":[0,0,0],\"scale\":[1,1,1],\"offset\":[0,0,0],\"target\":\"optional\",\"vector\":[0,0,0],\"color\":[1,1,1]}]," +
            "\"lineStart\":0,\"lineEnd\":0,\"range\":\"optional\"}. " +
            "No Markdown, no code fences, no extra text. " +
            "To create or modify code, use write_file with full content. " +
            "For read_file, you can pass lineStart/lineEnd (1-based) or range like \"70-95\" to avoid truncation. " +
            "Use apply_stage stage values: folders|scripts|materials|prefabs|scenes|assets. " +
            "Use fix_pass scope: last_stage|all. " +
            "Use scene_edit with scene path or scene name in \"scene\" and one or more edits. " +
            "For scene_edit, edits must be a non-empty array and each edit must include a non-empty type. Do not send empty {}. " +
            "Use scene_query to inspect a scene; optionally set \"scene\", \"name\" (or \"query\") and/or \"component\", and \"limit\". " +
            "Use scene_create to create a new scene; set \"scene\" (or \"path\"/\"name\"). " +
            "Use prefab_query to search prefabs by name; set \"name\" (or \"query\") and \"limit\". " +
            "Use respond when you are done and want to summarize. Use stop to end without changes.";

        public const string AgentLoopGoalFormat = "Goal: {0}";
        public const string AgentLoopIterationFormat = "Iteration {0}/{1}. If goal is complete, respond or stop.";
        public const string AgentLoopChatHistoryFormat = "Recent chat:\n{0}";
        public const string AgentLoopHistoryFormat = "Recent tool results:\n{0}";
        public const string AgentLoopDiagnosticsFormat = "Diagnostics:\n{0}";
        public const string AgentLoopRetryInstruction =
            "Your previous response was invalid JSON. Return ONLY a valid JSON object that matches the tool schema. No prose, no code fences.";
        public const string AgentLoopRetryResponseFormat = "Previous response (truncated):\n{0}";
    }
}
