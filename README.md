# MandoCode

<!-- Badges -->
![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)
![.NET](https://img.shields.io/badge/.NET-8.0-blueviolet?logo=dotnet)
![Semantic Kernel](https://img.shields.io/badge/Semantic%20Kernel-Agent%20Orchestration-blue?logo=microsoft)
![Ollama](https://img.shields.io/badge/Ollama-Local%20LLM-black?logo=ollama)
![RazorConsole](https://img.shields.io/badge/RazorConsole-Interactive%20TUI-purple)
![Platform](https://img.shields.io/badge/Platform-Windows%20%7C%20Linux-lightgrey)
![Made with <3 by Mando](https://img.shields.io/badge/Made%20with%20%3C3%20by-Mando-red)

**MandoCode** is a fully local, token-free AI coding assistant powered by **Semantic Kernel**, **Ollama**, and **RazorConsole**.
It provides Claude-Code-style project awareness, code generation, task planning, interactive file references, and safe file editing — all running **offline** on your machine.

MandoCode understands **any file type** in your project, including C#, JavaScript, TypeScript, CSS, HTML, JSON, configuration files, and more.

---

## Features

- **Fully offline** — no API keys, no cloud, no tokens
- **Project-aware AI** — reads, writes, deletes, and searches your entire codebase
- **Diff approvals** — color-coded diffs for file writes and deletions with approve/deny/redirect controls
- **`@` file references** — type `@` to autocomplete and attach file content to any prompt
- **`/` command autocomplete** — slash commands with interactive dropdown navigation
- **Task planner** — automatically breaks complex requests into step-by-step plans
- **Streaming responses** — real-time AI output with animated spinners
- **Retry & deduplication** — resilient function execution with automatic recovery
- **Configuration wizard** — guided setup with model selection and connection testing
- **Animated startup banner** — gradient text with howling wolf animation

---

## Architecture

```
[ Terminal (MandoCode CLI with RazorConsole) ]
                    |
             [ .NET 8.0 CLI ]
                    |
           [ Semantic Kernel ]
              /           \
     [ Local LLM ]     [ Plugins ]
  (Ollama models)    (FileSystem, etc.)
```

### Project Structure

```
src/MandoCode/
  Components/        Razor UI components (App, Banner, HelpDisplay, ConfigMenu, Prompt)
  Services/          Core business logic
  Models/            Data models, config, system prompts
  Plugins/           Semantic Kernel plugins (FileSystem)
  Program.cs         Entry point and DI registration
```

---

## Getting Started

### Prerequisites

- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Ollama](https://ollama.ai) installed and running

### Installation

```bash
# Clone the repository
git clone https://github.com/DevMando/MandoCode.git
cd MandoCode

# Build the project
dotnet build src/MandoCode/MandoCode.csproj

# Run MandoCode in your project directory
dotnet run --project src/MandoCode/MandoCode.csproj -- /path/to/your/project
```

### First Run

On first run, MandoCode will use the default model (`minimax-m2.5:cloud`). You can configure it:

```bash
# Run the configuration wizard interactively
mandocode config init

# Or set individual values
mandocode config set model qwen2.5-coder:14b
mandocode config set endpoint http://localhost:11434
mandocode config set temperature 0.5
```

---

## Commands

Type `/` at the start of your input to see the autocomplete dropdown.

| Command | Description |
|---------|-------------|
| `/help` | Show available commands and usage examples |
| `/config` | Open the configuration menu (wizard or view current settings) |
| `/clear` | Clear conversation history |
| `/exit` | Exit MandoCode |
| `/quit` | Exit MandoCode |

Anything else you type is sent to the AI as a natural-language prompt.

---

## `@` File References

Type `@` anywhere in your input (after a space or at position 0) to trigger **file autocomplete**. A dropdown appears showing your project files, filtered as you type.

### How It Works

1. Type your prompt and hit `@` — a file dropdown appears
2. Type a partial name to filter (e.g., `Conf`) — matches narrow down
3. Use arrow keys to navigate, **Tab** or **Enter** to select
4. The selected path is inserted (e.g., `@src/MandoCode/Models/MandoCodeConfig.cs`)
5. Continue typing and press **Enter** to submit
6. MandoCode reads the referenced file(s) and injects the content as context for the AI

### Examples

```
explain @src/MandoCode/Services/AIService.cs to me
what does the ProcessFileReferences method do in @src/MandoCode/Components/App.razor
refactor @src/MandoCode/Models/LoadingMessages.cs to use fewer spinners
```

Multiple `@` references in one prompt are supported. Files over 10,000 characters are automatically truncated.

### Autocomplete Controls

| Key | Action |
|-----|--------|
| `@` | Open file dropdown |
| Type | Filter files by name |
| Up/Down | Navigate dropdown |
| Tab/Enter | Insert selected file path (does not submit) |
| Escape | Close dropdown, keep text |
| Backspace | Re-filter, or close if you delete past `@` |

---

## Diff Approvals

When the AI writes or deletes a file, MandoCode intercepts the operation and shows a color-coded diff before applying changes.

### What You See

- **Red lines** — content being removed
- **Light blue lines** — content being added
- **Dim lines** — unchanged context (3 lines around each change)
- Long unchanged sections are collapsed with a summary

### Approval Options

| Option | Behavior |
|--------|----------|
| **Approve** | Apply this change |
| **Approve - Don't ask again** | Auto-approve future changes to this file (per-file), or all files (global) |
| **Deny** | Reject the change, the AI is told it was denied |
| **Provide new instructions** | Redirect the AI with custom feedback |

For **new files**, the "don't ask again" option sets a **global bypass** — all future writes and deletes are auto-approved for the session. For **existing files**, the bypass is **per-file**.

Even when auto-approved, diffs are still rendered so you can follow along.

### Delete Approvals

File deletions show all existing content as red removals with a deletion warning. The same approval options apply.

### Configuration

Diff approvals are enabled by default. To toggle:

```bash
mandocode config set diffApprovals false
```

---

## Task Planner

MandoCode automatically detects complex requests and offers to break them into a step-by-step plan before execution.

### Triggers

The planner activates for requests like:
- `Create a REST API service with authentication and rate limiting for the user module` (12+ words with imperative verb and scope indicator)
- `Build an application that handles user registration and sends email confirmations`
- Numbered lists with 3+ items
- Requests over 400 characters

Simple questions, short prompts, and single-action operations (delete, remove, read, show, list, find, search, rename) bypass planning automatically.

### Workflow

1. **Detection** — heuristics identify complex requests
2. **Plan generation** — AI creates numbered steps
3. **User approval** — review the plan table, then choose: execute, skip planning, or cancel
4. **Step-by-step execution** — each step runs with progress tracking
5. **Error handling** — skip failed steps or cancel the entire plan

See [Task Planner Documentation](src/MandoCode/docs/TaskPlannerFeature.md) for full technical details.

---

## AI Capabilities (Plugins)

### FileSystemPlugin

The AI has controlled access to your project directory through these functions:

| Function | Description |
|----------|-------------|
| `list_all_project_files()` | Recursively lists all project files, excluding ignored directories |
| `list_files_match_glob_pattern(pattern)` | Lists files matching a glob pattern (`*.cs`, `src/**/*.ts`, `*.*`) |
| `read_file_contents(relativePath)` | Reads complete file content with line count |
| `write_file(relativePath, content)` | Writes/creates a file (creates directories as needed) |
| `delete_file(relativePath)` | Deletes a file (directories cannot be deleted) |
| `create_folder(relativePath)` | Creates a new directory |
| `search_text_in_files(pattern, searchText)` | Searches file contents for text, returns file paths and line numbers |
| `get_absolute_path(relativePath)` | Converts a relative path to its absolute filesystem path |

**Security:** All file operations are sandboxed to the project root. Path traversal outside the project directory is blocked.

**Ignored directories** (not scanned): `.git`, `node_modules`, `bin`, `obj`, `.vs`, `.vscode`, `packages`, `dist`, `build`, `__pycache__`, `.idea` — plus any custom directories from your config.

---

## Reliability Features

### Retry Policy

Transient errors (HTTP failures, timeouts, socket errors) are automatically retried with exponential backoff:

```
Attempt 1 -> fail -> wait 500ms
Attempt 2 -> fail -> wait 1000ms
Attempt 3 -> fail -> throw
```

### Function Deduplication

Prevents duplicate operations within configurable time windows:

| Operation | Window | Matching |
|-----------|--------|----------|
| Read operations | 2 seconds | Function name + arguments |
| Write operations | 5 seconds (configurable) | Function name + path + content hash (SHA256) |

### Fallback Function Parsing

Some local models output function calls as JSON text instead of proper tool calls. MandoCode automatically detects and parses these formats:

- Standard: `{"name": "func", "parameters": {...}}`
- OpenAI-style: `{"function_call": {"name": "func", "arguments": {...}}}`
- Tool calls: `{"tool_calls": [{"function": {"name": "func", "arguments": {...}}}]}`

### Event-Based Completion Tracking

Function executions are tracked with semaphore-based signaling, ensuring each task plan step fully completes before the next begins.

---

## Configuration

### Config File

Located at `~/.mandocode/config.json`

```json
{
  "ollamaEndpoint": "http://localhost:11434",
  "modelName": "minimax-m2.5:cloud",
  "modelPath": null,
  "temperature": 0.7,
  "maxTokens": 4096,
  "ignoreDirectories": [],
  "enableTaskPlanning": true,
  "enableFallbackFunctionParsing": true,
  "functionDeduplicationWindowSeconds": 5,
  "maxRetryAttempts": 2
}
```

### All Options

| Key | Default | Description |
|-----|---------|-------------|
| `ollamaEndpoint` | `http://localhost:11434` | Ollama server URL |
| `modelName` | `minimax-m2.5:cloud` | Model to use |
| `modelPath` | `null` | Optional path to a local GGUF model file |
| `temperature` | `0.7` | Response creativity (0.0 = focused, 1.0 = creative) |
| `maxTokens` | `4096` | Maximum response token length |
| `ignoreDirectories` | `[]` | Additional directories to exclude from file scanning |
| `enableDiffApprovals` | `true` | Show diffs and prompt for approval before file writes/deletes |
| `enableTaskPlanning` | `true` | Enable automatic task planning for complex requests |
| `enableFallbackFunctionParsing` | `true` | Parse function calls from text output |
| `functionDeduplicationWindowSeconds` | `5` | Time window to prevent duplicate function calls |
| `maxRetryAttempts` | `2` | Max retry attempts for transient errors |

### CLI Config Commands

```bash
mandocode config show              # Display current configuration
mandocode config init              # Create default configuration file
mandocode config set <key> <value> # Set a configuration value
mandocode config path              # Show configuration file location
mandocode config --help            # Show help
```

### Environment Variables

| Variable | Overrides |
|----------|-----------|
| `OLLAMA_ENDPOINT` | `ollamaEndpoint` in config |
| `OLLAMA_MODEL` | `modelName` in config |

---

## Recommended Models

Models with tool/function calling support work best:

- **minimax-m2.5:cloud** (default) — excellent tool support
- **qwen2.5-coder:14b** — strong coding model with function calling
- **qwen2.5-coder:7b** — lighter alternative
- **mistral** — general purpose with tool support
- **llama3.1** — Meta's model with function calling

MandoCode validates model compatibility on startup and warns if the selected model may not support function calling.

---

## UI Components

| Component | Description |
|-----------|-------------|
| **Banner** | Animated startup screen with gradient MANDOCODE text and howling wolf animation |
| **HelpDisplay** | Command reference panel shown on startup |
| **ConfigMenu** | Interactive configuration display |
| **Prompt** | Input prompt with autocomplete support |
| **App** | Main application shell — connection checking, command routing, AI interaction loop |

---

## Dependencies

| Package | Version | Purpose |
|---------|---------|---------|
| [Microsoft.SemanticKernel](https://github.com/microsoft/semantic-kernel) | 1.68.0 | LLM orchestration and plugin system |
| [Microsoft.SemanticKernel.Connectors.Ollama](https://github.com/microsoft/semantic-kernel) | 1.68.0-alpha | Ollama model integration |
| [RazorConsole.Core](https://github.com/RazorConsole/RazorConsole) | 0.2.2 | Rich terminal UI with Razor components |

---

## License

[MIT](LICENSE)
