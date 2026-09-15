# Agent-MK
**Agent-MK is a local AI software-engineering assistant for Windows.**

It combines a native **WinUI 3 desktop interface** with a **Python-based agent runtime** and local LLM backends such as **Ollama**. The goal is not just to provide a chat window, but to give a local model access to a real project workspace where it can inspect files, search code, make changes, and validate its work.

<img width="1919" height="1028" alt="image" src="https://github.com/user-attachments/assets/6e901520-ac28-438b-90ea-762f413b1c06" />
---

## What Agent-MK Does

Agent-MK is designed around the idea that a coding model should work with a project instead of forcing the user to paste an entire repository into the chat.

A typical workflow looks like:

```text
User request
    ↓
Agent understands the task
    ↓
Inspect project / search relevant files
    ↓
Read only the files needed
    ↓
Modify workspace
    ↓
Validate changes where possible
    ↓
Return a concise result
```

The agent can operate inside an attached project folder, while chats without an attached project use an isolated per-chat workspace.

---

## Main Features

### Local-first AI

Agent-MK is built to work with locally hosted models.

The current runtime supports:

* Ollama
* LM Studio / OpenAI-compatible endpoints
* llama.cpp-style OpenAI-compatible endpoints
* OpenAI-compatible providers

Ollama is the default provider.

---

### Project-aware coding

A chat can be attached to a real project directory.

The agent can then use workspace tools such as:

* `list_files`
* `read_file`
* `write_file`
* `search_files`
* `workspace_snapshot`
* `run_command` (when explicitly enabled)

These tools allow the model to inspect the project instead of relying entirely on text pasted into the conversation.

---

### Safe workspace operations

Workspace file operations include path validation, atomic writes, and optional backups.

The runtime prevents file paths from escaping the configured workspace.

File writes are performed through temporary files followed by an atomic replacement, with backup support when enabled.

---

### Multi-step agent workflow

Agent-MK is not limited to one model response.

The local agent can perform multiple tool-driven steps:

```text
Model
  ↓
Tool
  ↓
Tool result
  ↓
Model
  ↓
Tool
  ↓
Final answer
```

### Context-aware conversations

Agent-MK tracks conversation usage and attempts to determine the actual context window of the selected Ollama model.

The UI can therefore report information such as:

```text
Context: ~12,400 / 32,768 tokens
```

The runtime also separates the input/context budget from the model's generation capacity so that generation has room to complete.

---

### Chat persistence

Chats and their messages are stored in SQLite.
The UI currently supports up to 30 chats.

---

### Native Windows UI

The desktop application is built with:

* C#
* .NET 8
* WinUI 3
* Windows App SDK

The project targets Windows x64 and uses the Windows App SDK with native/self-contained deployment settings.

The UI provides:

* multi-chat interface
* project attachment
* project tree
* code rendering
* syntax highlighting
* code copying/saving
* context usage display
* Ollama status information
* setup and repair workflow
* model management

---

## Architecture

The Python runtime is deliberately modular. `assistant.py` acts as a compatibility facade while the implementation lives inside `agent_core`.

The major responsibilities are:

| Component          | Responsibility                               |
| ------------------ | -------------------------------------------- |
| `agent.py`         | Multi-step agent loop                        |
| `actions.py`       | Model output parsing and system instructions |
| `context.py`       | Context trimming and model context detection |
| `model_client.py`  | Local model HTTP communication               |
| `tools.py`         | Tool definitions and execution               |
| `workspace.py`     | Safe file access and project operations      |
| `database.py`      | Agent history and audit data                 |
| `logging_setup.py` | Runtime logging                              |
| `chat_store.py`    | UI chat/session persistence                  |
| `headless.py`      | C# ↔ Python IPC bridge                       |

---


## Agent Profiles

Agent-MK is intended to support different levels of local-model capability.

The preferred direction is an automatic strategy where the runtime adapts its behavior to the selected model rather than forcing identical limits on every model.

Conceptually:

```text
Auto
 ├── smaller models
 │    └── tighter context/tool usage
 │
 └── larger models
      └── deeper project inspection / verification
```

This allows smaller local models to remain responsive while larger models can take advantage of their greater reasoning and context capacity.

---

## Tool Safety

Command execution is disabled by default.

When enabled, commands are restricted to an executable allowlist such as:

```text
python
python3
pytest
git
ruff
black
mypy
node
npm
npx
```

The workspace also ignores directories and file types that are typically irrelevant or potentially expensive to process, including `.git`, `node_modules`, build directories, binaries, archives, and compiled Python files.

---

## Setup

Agent-MK includes a first-run setup and repair system.

The setup flow checks:

* Python installation
* Python virtual environment
* Python dependencies
* Ollama installation
* Ollama availability
* downloaded models
* hardware information
* recommended model configuration
<img width="1410" height="731" alt="image" src="https://github.com/user-attachments/assets/1aaac5e5-fdb1-4da6-98de-1ffae0091228" />

## The application stores setup state separately from chat history so the environment can be repaired without losing the chat database.

### AI runtime

* Python 3.10+
* Ollama

The Python facade currently requires Python 3.10+ and uses only the Python standard library for the core runtime.

---

## Getting Started

### 1. Clone the repository

```bash
git clone https://github.com/Agent-MK-UI/Agent-MK-UI.git
cd Agent-MK-UI
```

### 2. Open the solution

Open the solution/project in Visual Studio with the required Windows development tooling installed.

### 3. Build and run

Use the `Agent-MK-UI` project as the startup project.

On first launch, Agent-MK performs an environment check and opens the setup/repair workflow when required.

---

## Working With a Project

After starting a chat:

1. Attach a project directory.
2. Use the project panel to inspect the project.
3. Describe the task in normal language.
4. Let the agent inspect only the files it needs.
5. Review the changes in the workspace.
6. Run validation/tests when appropriate.

A project does **not** need to be pasted into the chat.

This is one of the core design goals of Agent-MK.

---

## Agent-MK is being built around several principles:

### Local first

Keep the model and project data under the user's control whenever practical.

### Inspect before modifying

The agent should understand the existing code before changing it.

### Minimal changes

Prefer targeted fixes over destructive rewrites.

### Tool-driven development

Use the project workspace as the source of truth instead of asking the user to continuously paste code.

### Safe execution

Protect the workspace, restrict commands, maintain backups, and record agent activity.

### Model-aware behavior

Different local models have different capabilities. Agent-MK should adapt to the model rather than treating a small 7B coding model and a large model identically.
<img width="1911" height="726" alt="image" src="https://github.com/user-attachments/assets/fb3141de-c6f0-41d7-96d9-8685018ade83" />

---

## Current Limitations

Some areas are intentionally conservative or incomplete:

* Local model quality varies significantly by model and hardware.
* Large repositories still require intelligent context management.
* Tool-driven agents can occasionally repeat actions or require additional guidance.
* Command execution is deliberately restricted.
* Full project compilation/test coverage depends on the project being worked on and the tools installed on the machine.

---

## Why Agent-MK?

Most local AI coding setups are essentially:

```text
Chat UI + Model
```

Agent-MK is aiming for:

```text
                 ┌───────────────┐
                 │   WinUI 3 UI  │
                 └───────┬───────┘
                         │
                         ▼
                 ┌───────────────┐
                 │  IPC / Core   │
                 └───────┬───────┘
                         │
                         ▼
                 ┌───────────────┐
                 │ Python Agent  │
                 └───────┬───────┘
                         │
             ┌───────────┼───────────┐
             ▼           ▼           ▼
         Workspace     Tools       Context
             │           │           │
             └───────────┼───────────┘
                         ▼
                 ┌───────────────┐
                 │ Local LLM     │
                 │   Ollama      │
                 └───────────────┘
```

The goal is to make a local model behave more like a **software-engineering agent** than a simple chatbot.


## Bug reports, experiments, and ideas are welcome.
