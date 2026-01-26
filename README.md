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
   - Add OpenAI API Token and select model (I'm testing gpt-5.2, if you want low token consumption try gpt-5.1-codex-mini)
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

If you want to collaborate, I will be really happy to hear from you (on this repo or write to me on scorpile@gmail.com).

## Demo Video

Everything on this scene was created, placed, and configured by the plugin. It created the Player and objects scripts, added colliders and rigidbodies, and hydrated script values.  
[![Everything on this scene was created, placed, and configured by the plugin. It created the Player and objects scripts, added colliders and rigidbodies, and hydrated script values.](https://img.youtube.com/vi/NM0tmGDOUYQ/0.jpg)](https://www.youtube.com/watch?v=NM0tmGDOUYQ)

## Agent Examples

Paste any of these into the Chat window and press **Agent**. Adjust asset names/paths to match your project.

### Planning & workflow

```text
Create a plan for a top-down Archero-style prototype in Unity.
Break the plan into feature requests.
Apply the full plan now.
Apply stage: folders.
Apply stage: scripts.
Apply stage: prefabs.
Apply stage: scenes.
Apply stage: materials.
Apply stage: assets.
Run a fix pass for the last stage.
Run a fix pass for all scripts.
Continue.
```

### Core gameplay & scripts

```text
Create a PlayerController MonoBehaviour for WASD movement and dodge.
Create an EnemySpawner script that spawns enemies every 5 seconds.
Add a ScriptableObject named UpgradeDefinition for upgrade data.
Generate a basic UI canvas with Health and XP bars.
Add a WaveManager that coordinates spawner difficulty over time.
```

### Prefabs & assets

```text
Create a Player prefab with a Capsule, Rigidbody, and PlayerController.
Create an Enemy prefab with a red material.
Generate a material for the player (Standard, blue tint).
Create a simple pickup prefab for health.
```

### Scene edits (agent tool)

```text
In ArenaPhase1, add a Spotlight named PlayerKeyLight 3 units above Player and point it at Player.
In ArenaPhase1, move PlayerSpawn to (0, 0, 0).
In ArenaPhase1, rotate Main Camera to (20, 0, 0).
In ArenaPhase1, create an empty GameObject named Managers.
In ArenaPhase1, delete the GameObject DebugLight.
```

### Inspection & troubleshooting

```text
Find the script that defines WaveSpawner and summarize its public API.
List files under Assets/Project/Scripts.
Read Assets/Project/Scripts/PlayerController.cs lines 1-120.
Search Assets/Project for "DamageDealer" and open the matching script.
Read Assets/Plan/ScriptIndex.md and suggest missing types.
Review script PlayerController.cs and apply corrections.
Fix the current console errors.
```

## License

This repository is public for collaboration and evaluation. Non-commercial use is allowed. Commercial use, redistribution, or creating a commercial derivative project (in whole or in part) is not allowed without explicit written permission from the maintainer.

This is not an OSI open-source license. See `LICENSE`.
