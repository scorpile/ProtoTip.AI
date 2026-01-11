# ProtoTip.AI

ProtoTip.AI is a Unity Editor agent that uses OpenAI to plan and scaffold a playable prototype. It creates project structure, scripts, prefabs, and scene skeletons under `Assets/Project`, and stores plan/feature request artifacts under `Assets/Plan`.

## Installation

Just copy the ProtoTipAI folder and all of its content to the Assets folder of your project.

## Requirements

- Unity 2022.3+ (tested)
- OpenAI API key

## Quick Start

Open menu Proto -> Setup:
-Add OpenAI API Token and select model (i'm testing 5.2)
-Add Project goal
-Refresh Summary
-Save and close
Open menu Proto -> Chat and align it with Inspector tab:
-Set the Plan Prompt
-Press Generate Plan and wait for the agent to end asking the plan to the AI. It will create a Plan folder and capture some MD files with inventory of different items for the Project.
-Press Create Feature Requests to generate all the feature requests of the plan.
-Press Apply Plan and wait it to finish.
-Should end without Script errors, but you can press Fix Step with 1 or more fix step iterations
-Wait for the end of the implementation
You should end with a Project folder that includes project folder structure, Scripts, Prefabs and Scenes skeletons, but some feature requests maybe need to be applied to be finished, so you can open menu Proto -> Plan Tracking and use drop down filters to find blocked feature requests that need retry or re-do. Using the different Plan Stages buttons on the Proto Chat tab should try only on pending (todo/blocked) feature requests.

I've been developing this agent, and you will have a playable prototype at the end, but with basic shapes, nothing fancy. It all depends on prompts, and you need to review variables and hydrate them on scenes and/or prefabs.

This Agent have a lot of stuff to be implemented, and i realised i cannot continue this Project alone, and Unity team started to add the AI directly to Unity so i'm not sure what's the future for this. I do feel i was more advanced than Unity's team ;) but this is far from being even a prototype.

If you want to collaborate, i will be really happy to hear from you.

## License

This repository is public for collaboration and evaluation. Non-commercial use is allowed. Commercial use, redistribution, or creating a commercial derivative project (in whole or in part) is not allowed without explicit written permission from the maintainer.

This is not an OSI open-source license. See `LICENSE`.
