#!/usr/bin/env python3
"""
headless.py

Long-running IPC bridge between the C# UI (Orchestra.Core.Services.PythonEngine)
and the real agent runtime in assistant.py, plus chat/project persistence via
chat_store.py. Reads one JSON request per line on stdin, writes one JSON
response per line on stdout.

Every request has an "action" field routing it to a handler below. All keys
are snake_case both directions, matching the [JsonPropertyName] attributes
on Orchestra.Core.Models.AgentRequest / AgentResponse.

Supported actions:
  chat               -- {chat_id, prompt, model_name?} -> {response_text, chat_id}
  list_chats         -- {} -> {chats: [...]}
  create_chat        -- {title, project_path?, model_name} -> {chat: {...}}
  delete_chat        -- {chat_id} -> {chat_id}
  get_messages       -- {chat_id} -> {messages: [...]}
  list_project_files -- {project_path} -> {files: [...]}
"""

import sys
import os
import json
import traceback
import shutil
from dataclasses import asdict
from pathlib import Path

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import assistant  # noqa: E402
from chat_store import ChatStore, ChatLimitError  # noqa: E402


_store = ChatStore()

# One LocalAgent per chat id, created lazily. A chat's effective workspace
# is its attached project folder, or an isolated per-chat scratch folder if
# it has no project -- either way it's stable for the chat's lifetime, so
# caching by chat_id (not by path) is correct even if a chat's project
# were ever changed later.
_agents: dict[int, "assistant.LocalAgent"] = {}

_DEFAULT_WORKSPACE_ROOT = Path.home() / "Documents" / "Agent-MK" / "Workspace"

# Directories we never want to walk into when listing a project tree --
# huge, irrelevant, or binary-heavy by convention.
_IGNORED_DIR_NAMES = {
    ".git", ".venv", "venv", "__pycache__", "node_modules",
    "bin", "obj", ".local_agent", ".idea", ".vs",
}
_MAX_TREE_ENTRIES = 3000


def get_workspace_for_chat(chat) -> Path:
    if chat.project_path:
        root = Path(chat.project_path)
    else:
        root = _DEFAULT_WORKSPACE_ROOT / f"chat_{chat.id}"
    root.mkdir(parents=True, exist_ok=True)
    return root


def get_agent(chat, model_name: str, agent_profile: str = "auto") -> "assistant.LocalAgent":
    agent = _agents.get(chat.id)

    if agent is None:
        root = get_workspace_for_chat(chat).resolve()
        
        # Isolate the LLM's memory database by chat_id.
        # It must NEVER be stored inside the project workspace folder, 
        # or multiple chats attached to the same project will become a hive-mind.
        memory_dir = _DEFAULT_WORKSPACE_ROOT / "ChatMemories" / f"chat_{chat.id}"
        memory_dir.mkdir(parents=True, exist_ok=True)

        cfg = assistant.Config(
            project_root=root,
            db_path=memory_dir / "history.sqlite3",
            audit_log_path=memory_dir / "audit.log",
            provider="ollama",
            model=model_name,
            endpoint=assistant.default_endpoint("ollama"),
            agent_profile=agent_profile,
        )

        logger = assistant.setup_logging(cfg.audit_log_path)
        db = assistant.Database(cfg.db_path)
        workspace = assistant.Workspace(cfg, logger)
        model_client = assistant.ModelClient(cfg, logger)
        tools = assistant.build_tools(cfg, workspace)

        agent = assistant.LocalAgent(
            cfg=cfg, db=db, workspace=workspace, model=model_client,
            tools=tools, logger=logger,
        )
        _agents[chat.id] = agent
    else:
        agent.cfg.model = model_name
        agent.cfg.agent_profile = agent_profile

    return agent


def list_project_tree(project_path: str) -> list[dict]:
    root = Path(project_path)
    if not root.is_dir():
        raise ValueError(f"Not a directory: {project_path}")

    entries: list[dict] = []

    def walk(current: Path):
        if len(entries) >= _MAX_TREE_ENTRIES:
            return
        try:
            children = sorted(
                current.iterdir(), key=lambda p: (p.is_file(), p.name.lower())
            )
        except PermissionError:
            return

        for child in children:
            if len(entries) >= _MAX_TREE_ENTRIES:
                return
            if child.name in _IGNORED_DIR_NAMES:
                continue

            rel = str(child.relative_to(root)).replace(os.sep, "/")
            entries.append({"path": rel, "is_dir": child.is_dir()})

            if child.is_dir():
                walk(child)

    walk(root)
    return entries


# ------------------------------------------------------------
# Action handlers -- each takes the parsed request dict and returns the
# response payload dict (without "action"/"is_error"/"error_message",
# which main() fills in around whatever these return).
# ------------------------------------------------------------

def handle_chat(req: dict) -> dict:
    chat_id = req.get("chat_id")
    prompt = req.get("prompt", "")
    model_override = req.get("model_name")

    if chat_id is None:
        raise ValueError("chat_id is required")

    chat = _store.get_chat(int(chat_id))
    if chat is None:
        raise ValueError(f"No such chat: {chat_id}")

    model_name = model_override or chat.model
    agent_profile = str(req.get("agent_profile") or "auto")
    if model_override and model_override != chat.model:
        _store.set_chat_model(chat.id, model_override)

    agent = get_agent(chat, model_name, agent_profile)

    _store.add_message(chat.id, "user", prompt)
    reply = agent.run(prompt)
    _store.add_message(chat.id, "assistant", reply)

    return {"chat_id": chat.id, "response_text": reply}


def handle_list_chats(_req: dict) -> dict:
    chats = [asdict(c) for c in _store.list_chats()]
    return {"chats": chats}


def handle_create_chat(req: dict) -> dict:
    title = req.get("title") or "New chat"
    project_path = req.get("project_path")
    model_name = req.get("model_name") or "qwen2.5-coder:7b"

    try:
        chat = _store.create_chat(title, project_path, model_name)
    except ChatLimitError as e:
        raise ValueError(str(e)) from e

    return {"chat": asdict(chat)}


def handle_delete_chat(req: dict) -> dict:
    chat_id = req.get("chat_id")
    if chat_id is None:
        raise ValueError("chat_id is required")

    chat_id = int(chat_id)
    _store.delete_chat(chat_id)
    _agents.pop(chat_id, None)

    # Destroy the SQLite memory bubble on the hard drive!
    # If we don't do this, a new chat that reuses this ID will inherit the deleted chat's memories.
    memory_dir = _DEFAULT_WORKSPACE_ROOT / "ChatMemories" / f"chat_{chat_id}"
    if memory_dir.exists():
        shutil.rmtree(memory_dir, ignore_errors=True)

    return {"chat_id": chat_id}


def handle_set_chat_project(req: dict) -> dict:
    chat_id = req.get("chat_id")
    if chat_id is None:
        raise ValueError("chat_id is required")

    chat_id = int(chat_id)
    project_path = req.get("project_path")
    _store.set_project_path(chat_id, project_path)

    # Force the next message on this chat to build a fresh LocalAgent
    # against the new workspace root instead of reusing the old one.
    _agents.pop(chat_id, None)

    chat = _store.get_chat(chat_id)
    if chat is None:
        raise ValueError(f"No such chat: {chat_id}")

    return {"chat": asdict(chat)}


def handle_rename_chat(req: dict) -> dict:
    chat_id = req.get("chat_id")
    title = req.get("title")
    if chat_id is None:
        raise ValueError("chat_id is required")
    if not title:
        raise ValueError("title is required")

    chat_id = int(chat_id)
    _store.rename_chat(chat_id, title)

    chat = _store.get_chat(chat_id)
    if chat is None:
        raise ValueError(f"No such chat: {chat_id}")

    return {"chat": asdict(chat)}


def handle_get_messages(req: dict) -> dict:
    chat_id = req.get("chat_id")
    if chat_id is None:
        raise ValueError("chat_id is required")

    return {"messages": _store.get_messages(int(chat_id))}


def handle_get_context_usage(req: dict) -> dict:
    chat_id = req.get("chat_id")
    if chat_id is None:
        raise ValueError("chat_id is required")

    chat = _store.get_chat(int(chat_id))
    if chat is None:
        raise ValueError(f"No such chat: {chat_id}")

    agent = get_agent(chat, chat.model, str(req.get("agent_profile") or "auto"))
    return {"context_usage": agent.get_context_usage()}


def handle_list_project_files(req: dict) -> dict:
    project_path = req.get("project_path")
    if not project_path:
        raise ValueError("project_path is required")

    return {"files": list_project_tree(project_path)}


_HANDLERS = {
    "chat": handle_chat,
    "list_chats": handle_list_chats,
    "create_chat": handle_create_chat,
    "delete_chat": handle_delete_chat,
    "set_chat_project": handle_set_chat_project,
    "rename_chat": handle_rename_chat,
    "get_messages": handle_get_messages,
    "get_context_usage": handle_get_context_usage,
    "list_project_files": handle_list_project_files,
}


def main() -> None:
    if sys.platform == "win32":
        sys.stdout.reconfigure(encoding="utf-8")
        sys.stdin.reconfigure(encoding="utf-8")

    while True:
        raw_input = sys.stdin.readline()
        if not raw_input:
            break

        action = "unknown"
        try:
            request = json.loads(raw_input)
            action = request.get("action", "chat")

            handler = _HANDLERS.get(action)
            if handler is None:
                raise ValueError(f"Unknown action: {action}")

            payload = handler(request)
            response_payload = {
                "action": action,
                "is_error": False,
                "error_message": None,
                **payload,
            }

        except Exception as e:
            error_trace = traceback.format_exc()
            sys.stderr.write(f"Fatal error handling action '{action}':\n{error_trace}\n")
            sys.stderr.flush()

            response_payload = {
                "action": action,
                "response_text": "",
                "is_error": True,
                "error_message": str(e),
            }

        sys.stdout.write(json.dumps(response_payload) + "\n")
        sys.stdout.flush()


if __name__ == "__main__":
    main()


