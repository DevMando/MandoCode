# CLI components

Components are grouped by the feature they render:

| Folder | Responsibility |
| --- | --- |
| Shell | Application coordination, main chat shell, home screen, and help |
| Workspaces | Workspace tabs, picker, agent pane headers, and grid layout |
| Conversation | Transcript entries, messages, previews, and context meter |
| Input | Prompt input, autocomplete suggestions, model picker, and approval selection |
| Project | File explorer, Git changes, and directory browser |
| Sessions | Saved-agent history and context snapshots |
| Settings | Agent configuration, integrations, and MCP tools |
| Onboarding | Setup wizard and model downloads |
| Activity | Agent/command status, operation previews, music, and plan-step editing |

Each Razor component explicitly declares `@namespace MandoCode.Components`.
This matches the feature-folder convention used by Services: folders organize
source without changing public type names or component references. Keep all
`App` partial files together in Shell so their shared lifecycle state stays visible.
Shared Razor imports remain in `src/MandoCode/_Imports.razor`.
