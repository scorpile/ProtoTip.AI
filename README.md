# ProtoTip.AI

ProtoTip.AI is a Unity Editor agent that uses OpenAI to plan and scaffold a playable prototype. It creates project structure, scripts, prefabs, and scene skeletons under `Assets/Project`, and stores plan/feature request artifacts under `Assets/Plan`.

## Installation

Copy the `ProtoTipAI` folder into your project's `Assets` folder.

## Requirements

- Unity 2022.3+ (tested)
- OpenAI API key

## Features

ProtoTip is a Unity-native agentic workflow: describe the goal, the agent plans, breaks the work into feature requests, and applies changes while keeping a persistent session you can resume anytime.

- Agentic Chat: tool-using loop that can read/write/list/search and run plan/stage/fix actions, with retries and knowing when to stop.
- Session memory: per-session history saved to disk, continue-last flow, and automatic compaction summaries to keep context focused.
- Guided planning: configurable plan prompt, optional phased plans, and structured feature requests with dependencies and steps.
- Safe execution controls: apply the full plan or run by stage (folders/scripts/materials/prefabs/scenes/assets) with progress, confirmations, and post-stage fix passes.
- Unity scaffolding: generates folders, scripts, prefabs, scenes, materials, generic assets, plus prefab components and scene managers/prefabs.
- Context awareness: project goal + summary and toggles for selection/scene/recent assets/console context.
- Diagnostics + indexes: surfaces Unity Console errors and builds Script/Prefab/Scene/Asset indexes to reduce hallucinated types.
- Command palette + tool bench: quick commands for common actions and manual read/write/list/search without leaving the editor.
- Clean UI: Setup, Chat, Control, and Plan Tracking windows for each stage of the workflow.
- Provider flexibility: OpenAI and OpenCode Zen (OpenAI-compatible), custom API URL, model selection, API key stored in EditorPrefs.
- Script review: review an existing script by name/path and get a corrected file.

## Artifacts & Storage

- Generated content: `Assets/Project`.
- Plan raw JSON: `Assets/Plan/PlanRaw.json`.
- Feature requests (per item): `Assets/Plan/*.json`.
- Indexes: `Assets/Plan/ScriptIndex.md`, `Assets/Plan/PrefabIndex.md`, `Assets/Plan/SceneIndex.md`, `Assets/Plan/AssetIndex.md`.
- Chat sessions: `Assets/Plan/ChatSessions/ChatSessions.json` and `Assets/Plan/ChatSessions/<sessionId>.json`.

## Quick Start

1. Open menu `Proto -> Setup`:
   - Add OpenAI API Token and select model (I'm testing 5.2)
   - Add Project goal
   - Refresh Summary
   - Save and close
2. Open menu `Proto -> Chat` and dock it next to the Inspector tab:
   - Set the Plan Prompt
   - Press Generate Plan and wait for the agent to finish asking the plan to the AI. It will create a Plan folder and capture MD files with inventory of different items for the Project.
   - Press Create Feature Requests to generate all the feature requests of the plan.
   - Press Apply Plan and wait for it to finish.
   - It should end without script errors, but you can press Fix Step with 1 or more fix step iterations.
   - Wait for the end of the implementation.

### After Apply Plan

You should end with a Project folder that includes project folder structure, Scripts, Prefabs and Scenes skeletons. Some feature requests may need to be applied again, so open menu `Proto -> Plan Tracking` and use drop down filters to find blocked feature requests that need retry or re-do. Using the different Plan Stages buttons on the Proto Chat tab should try only on pending (todo/blocked) feature requests.

I've been developing this agent, and you will have a playable prototype at the end, but with basic shapes, nothing fancy. It all depends on prompts, and you need to review variables and hydrate them on scenes and/or prefabs.

This Agent has a lot of stuff to be implemented, and I realised I cannot continue this project alone. The Unity team started to add AI directly to Unity, so I'm not sure what's the future for this. I do feel I was more advanced than Unity's team ;) but this is far from being even a prototype.

If you want to collaborate, I will be really happy to hear from you.

## License

This repository is public for collaboration and evaluation. Non-commercial use is allowed. Commercial use, redistribution, or creating a commercial derivative project (in whole or in part) is not allowed without explicit written permission from the maintainer.

This is not an OSI open-source license. See `LICENSE`.
