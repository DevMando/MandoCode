# MandoCode

<!-- Badges -->
![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)
![.NET](https://img.shields.io/badge/.NET-8.0-blueviolet?logo=dotnet)
![Semantic Kernel](https://img.shields.io/badge/Semantic%20Kernel-Agent%20Orchestration-blue?logo=microsoft)
![Ollama](https://img.shields.io/badge/Ollama-Local%20LLM-black?logo=ollama)
![DeepSeek Coder V2](https://img.shields.io/badge/DeepSeek%20Coder%20V2-16B-orange)
![RazorConsole](https://img.shields.io/badge/RazorConsole-Interactive%20TUI-purple)
![Platform](https://img.shields.io/badge/Platform-Windows%20%7C%20Linux-lightgrey)
![Made with <3 by Mando](https://img.shields.io/badge/Made%20with%20%3C3%20by-Mando-red)




**MandoCode** is a fully local, token-free AI coding assistant powered by **Semantic Kernel**, **Ollama**, **DeepSeek Coder V2**, and **RazorConsole**.  
It provides Claude-Code–style project awareness, code refactoring, diff previews, interactive terminal UI, and safe file editing — all running **offline** on your machine.

MandoCode understands **any file type** in your project, including C#, JavaScript, TypeScript, CSS, HTML, JSON, configuration files, and more.

---

## 🚀 Project Goal

Build a local developer assistant that:

- Uses **DeepSeek Coder V2 (via Ollama)** as the LLM  
- Uses **Semantic Kernel** to orchestrate model calls and tools  
- Exposes **filesystem operations** (list files, read files, write files)  
- Provides a natural-language **CLI interface**  
- Uses **RazorConsole** for a rich, interactive terminal UI  
- Behaves similarly to **Claude Code**, but completely offline and free

---

## 🧠 High-Level Architecture

[ Terminal (MandoCode CLI with RazorConsole) ]
↓
[ .NET CLI Wrapper ]
↓
[ Semantic Kernel ]
↙ ↘
[ Local LLM ] [ Tools ]
(DeepSeek via Ollama) (Filesystem, Git,
Tests, etc.)

---

## 📁 Core Behaviors

MandoCode provides project-wide intelligence across **any file type**, including:

- `.cs`, `.js`, `.ts`, `.css`, `.scss`, `.html`, `.json`
- `.csproj`, `.config`, `.env`, `.md`
- Any file inside your repo

---

## 🔍 Multi-File Intelligence

The model can:

- Discover files using listing tools  
- Read any file via `fs.ReadFile()`  
- Modify multiple files per request  
- Produce separate diffs per file  
- Plan changes across multiple languages  

---

## 🔧 Semantic Kernel Plugins (Tools)

### **FileSystemPlugin**

Provides safe, controlled access to the project directory:

- `ListFiles(pattern)`  
  - Examples: `"*.cs"`, `"*.js"`, `"*.*"`  
- `ListAllProjectFiles()`  
  - Recursively returns all project files, excluding ignored dirs  
- `ReadFile(relativePath)`  
  - Returns file text  
- `WriteFile(relativePath, content)`  
  - Writes updated file content  

### **Planned Tools**

- **Git Integration**
  - `GitStatus`
  - `GitDiff`
  - `GitCommit(message)`
- **Test Runners**
  - `RunDotnetTests`
  - `RunNpmTests`
- **Search**
  - `FindInFiles(pattern, text)`

---

## 🧩 System Prompt Behavior

The model is instructed to:

- Use listing functions instead of guessing file names  
- Read files before editing  
- Propose changes with clear summaries  
- Output diffs for review  
- Keep edits minimal unless requested otherwise  
- Work across **multi-language** codebases  

This produces a Claude Code–style workflow using only local compute.

---

## 🖼️ Interactive CLI Powered by RazorConsole

MandoCode uses **RazorConsole** to provide a modern, interactive TUI (Text User Interface) inside the terminal.

This enables:

- Rich panels and layouts  
- Syntax-highlighted code previews  
- File tree explorers  
- Unified diff visualizations  
- Interactive Y/N confirmation prompts  
- Step-by-step guided workflows  

By using Razor components inside the console, MandoCode behaves more like a lightweight **local IDE assistant** than a traditional CLI tool.

**RazorConsole repo:**  
https://github.com/RazorConsole/RazorConsole

---




