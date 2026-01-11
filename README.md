# ProtoTip.AI

ProtoTip.AI is a Unity Editor agent that uses OpenAI to plan and scaffold a playable prototype. It creates project structure, scripts, prefabs, and scene skeletons under `Assets/Project`, and stores plan/feature request artifacts under `Assets/Plan`.

## Installation

Copy the `ProtoTipAI` folder into your project's `Assets` folder.

## Requirements

- Unity 2022.3+ (tested)
- OpenAI API key

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
