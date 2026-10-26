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
  agentic_task       -- {chat_id, task_id, prompt} -> long-running task with checkpoints
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
import time
import re
import shutil
from dataclasses import asdict
from pathlib import Path

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import assistant  # noqa: E402
from chat_store import ChatStore, ChatLimitError  # noqa: E402
from agent_core.actions import extract_json_object  # noqa: E402


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
            workspace_is_project=bool(chat.project_path),
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

def emit_chat_progress(chat_id: int, message: str) -> None:
    """Write a progress frame on stdout; the C# IPC reader consumes it before the final response."""
    frame = {
        "action": "chat_progress",
        "chat_id": chat_id,
        "progress_text": message,
    }
    sys.stdout.write(json.dumps(frame, ensure_ascii=False) + "\n")
    sys.stdout.flush()


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
    reply = agent.run(
        prompt,
        progress_callback=lambda message: emit_chat_progress(chat.id, message),
    )
    _store.add_message(chat.id, "assistant", reply)

    return {"chat_id": chat.id, "response_text": reply}

def handle_prepare_agentic_task(req: dict) -> dict:
    chat_id = req.get("chat_id")
    prompt = str(req.get("prompt") or "").strip()
    if chat_id is None or not prompt:
        raise ValueError("chat_id and prompt are required for task review.")
    chat = _store.get_chat(int(chat_id))
    if chat is None:
        raise ValueError(f"No such chat: {chat_id}")

    agent = get_agent(chat, chat.model, str(req.get("agent_profile") or "auto"))
    history = agent.db.recent_messages(min(agent.cfg.max_history_messages, 6))
    context = [
        {"role": item.get("role", ""), "content": str(item.get("content", ""))[-1000:]}
        for item in history
    ]
    previous_limits = (agent.cfg.max_tokens, agent.cfg.request_timeout_sec, agent.cfg.retries)
    agent.cfg.max_tokens = min(agent.cfg.max_tokens, 384)
    agent.cfg.request_timeout_sec = max(agent.cfg.request_timeout_sec, 90)
    agent.cfg.retries = 1
    messages = [
        {
            "role": "system",
            "content": (
                "You are the task-understanding preflight for a local coding assistant. "
                "Interpret the newest user request using the recent conversation as context. "
                "If it means continue or resume, recover the actual earlier goal from context, regardless of language or wording. "
                "Do not execute tools, write code, or produce a plan. Do not invent requirements. Use sensible defaults for optional design choices. "
                "Do not ask about preferences, colors, optional features, or details that can safely use a default. Set requires_clarification=true only when essential information is missing and useful work cannot safely begin without it. "
                "State one concise, actionable goal in the user's language, list concrete deliverables, and return only JSON with understood_goal (string), deliverables (array of strings), requires_clarification (boolean), clarification_question (string)."
            ),
        },
        {
            "role": "user",
            "content": json.dumps(
                {"recent_conversation": context, "new_request": prompt},
                ensure_ascii=False,
            ),
        },
    ]
    try:
        raw = agent.model.chat(messages)
    finally:
        agent.cfg.max_tokens, agent.cfg.request_timeout_sec, agent.cfg.retries = previous_limits
    parsed = extract_json_object(raw)
    if not isinstance(parsed, dict):
        raise ValueError("The model could not produce a readable task interpretation. Please retry or edit your request.")
    goal = parsed.get("understood_goal")
    deliverables = parsed.get("deliverables", [])
    question = parsed.get("clarification_question", "")
    requires_clarification = parsed.get("requires_clarification", False) is True
    if not isinstance(goal, str) or not goal.strip():
        raise ValueError("The model returned an empty task interpretation. Please retry or edit your request.")
    if not isinstance(deliverables, list):
        deliverables = []
    result = {
        "understood_goal": goal.strip(),
        "deliverables": [str(item).strip() for item in deliverables if str(item).strip()][:8],
        "clarification_question": question.strip() if isinstance(question, str) else "",
        "requires_clarification": requires_clarification,
    }
    return {"chat_id": chat.id, "response_text": json.dumps(result, ensure_ascii=False)}


def _task_cancel_path(task_id: str) -> Path:
    if not re.fullmatch(r"[0-9a-fA-F]{32}", task_id):
        raise ValueError("Invalid agentic task id")
    local_app_data = os.environ.get("LOCALAPPDATA") or str(Path.home())
    folder = Path(local_app_data) / "AgentMK" / "task_control"
    folder.mkdir(parents=True, exist_ok=True)
    return folder / f"{task_id}.cancel"


def _is_continue_request(text: str) -> bool:
    return bool(re.fullmatch(r"\s*(?:ok(?:ay)?\s+)?(?:please\s+)?(?:continue|resume|keep going|go on|proceed)(?:\s+(?:(?:the|this|previous|original)\s+)?(?:task|work|project|site))?(?:\s+please)?[.! ]*\s*", text, re.IGNORECASE))


def _latest_prior_task_goal(workspace: Path) -> str | None:
    tasks_dir = workspace / ".agent_tasks"
    if not tasks_dir.is_dir():
        return None
    candidates = sorted(tasks_dir.glob("*/task.md"), key=lambda path: path.stat().st_mtime, reverse=True)
    for task_file in candidates:
        try:
            text = task_file.read_text(encoding="utf-8")
        except OSError:
            continue
        match = re.search(r"(?ms)^## (?:Primary )?Goal\s*\n(.*?)(?=^## |\Z)", text)
        candidate = match.group(1).strip() if match else ""
        if candidate and not _is_continue_request(candidate):
            return candidate
    return None


def _build_task_plan(goal: str) -> str:
    safe_goal = goal.strip() or "Complete the user's requested task."
    return (
        "# Execution plan\n\n"
        f"## Goal\n{safe_goal}\n\n"
        "## Plan status\nNeeds task-specific refinement by the agent.\n\n"
        "## Milestones\n"
        "- [ ] Inspect the selected workspace and read only files relevant to this task.\n"
        "- [ ] Turn the goal into concrete deliverables and observable acceptance checks; refine this checklist for the task.\n"
        f"- [ ] Implement the requested deliverable: {safe_goal}\n"
        "- [ ] Verify the changed files with the most relevant available checks and fix failures.\n"
        "- [ ] Report changed files, checks actually run, and any remaining limitations.\n\n"
        "## Working rules\n"
        "Keep these milestones task-specific. Check off a milestone only after completing it. Update this file at meaningful milestones; do not replace it with a one-line summary.\n"
    )


def _plan_has_actionable_steps(content: str) -> bool:
    items = re.findall(r"(?m)^\s*(?:[-*]\s+\[[ xX]\]\s+|\d+[.)]\s+)\S.+$", content or "")
    return len(items) >= 4

def _read_progress_activity(path: Path) -> list[tuple[str, str]]:
    """Load high-signal entries from both the old event log and new summary."""
    if not path.exists():
        return []
    try:
        text = path.read_text(encoding="utf-8")
    except OSError:
        return []
    entries: list[tuple[str, str]] = []
    for line in text.splitlines():
        match = re.match(r"(?:-\s*)?(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d UTC):\s*(.+)$", line.strip())
        if match:
            entries.append((match.group(1), match.group(2).strip()))
        elif line.startswith("- ") and " | " in line:
            stamp, message = line[2:].split(" | ", 1)
            entries.append((stamp.strip(), message.strip()))
    return entries[-100:]


def _render_progress(goal: str, status: str, completed: list[str], next_action: str,
                     blockers: list[str], activity: list[tuple[str, str]]) -> str:
    lines = ["# Task progress", "", "## Goal", goal.strip() or "Complete the requested task.", "", "## Status", status, "", "## Completed"]
    lines.extend(f"- {item}" for item in completed[-12:] if item)
    if not completed:
        lines.append("- No deliverables confirmed yet.")
    lines.extend(["", "## Next", next_action or "Continue implementation, then verify the deliverable.", "", "## Blockers"])
    lines.extend(f"- {item}" for item in blockers[-6:] if item)
    if not blockers:
        lines.append("- None reported.")
    lines.extend(["", "## Recent activity"])
    lines.extend(f"- {stamp} | {message}" for stamp, message in activity[-12:])
    if not activity:
        lines.append("- Task started; no implementation events yet.")
    lines.append("")
    return "\n".join(lines)


def _matching_task_checkpoint(workspace: Path, goal: str) -> str | None:
    tasks_dir = workspace / ".agent_tasks"
    if not tasks_dir.is_dir():
        return None
    task_files = list(tasks_dir.glob("*/task.md"))
    task_files.sort(
        key=lambda path: (path.parent / "progress.md").stat().st_mtime
        if (path.parent / "progress.md").exists() else path.stat().st_mtime,
        reverse=True,
    )
    for task_file in task_files:
        try:
            text = task_file.read_text(encoding="utf-8")
        except OSError:
            continue
        match = re.search(r"(?ms)^## (?:Primary )?Goal\s*\n(.*?)(?=^## |\Z)", text)
        candidate = match.group(1).strip() if match else ""
        if candidate == goal.strip():
            return task_file.parent.name
    return None

def _run_agentic_task(req: dict) -> dict:
    chat_id = req.get("chat_id")
    goal = str(req.get("prompt") or "").strip()
    task_id = str(req.get("task_id") or "")
    allow_commands = bool(req.get("allow_commands", False))
    requested_goal = goal

    if chat_id is None or not goal:
        raise ValueError("An agentic task needs a chat and a goal.")

    chat = _store.get_chat(int(chat_id))
    if chat is None:
        raise ValueError(f"No such chat: {chat_id}")

    control_task_id = task_id
    control_path = _task_cancel_path(control_task_id)
    _store.set_agentic_setup_confirmed(chat.id, True)
    chat.agentic_setup_confirmed = True
    agent = get_agent(chat, chat.model, str(req.get("agent_profile") or "auto"))
    workspace_root = get_workspace_for_chat(chat).resolve()
    primary_goal = chat.agentic_task_goal
    is_continuation = bool(primary_goal)
    resumed_goal = None
    if not primary_goal:
        resumed_goal = _latest_prior_task_goal(workspace_root) if _is_continue_request(requested_goal) else None
        primary_goal = resumed_goal or requested_goal
        _store.set_agentic_task_goal(chat.id, primary_goal)
        chat.agentic_task_goal = primary_goal
        is_continuation = resumed_goal is not None
    if _is_continue_request(requested_goal):
        goal = primary_goal
    elif requested_goal.strip() == primary_goal.strip():
        goal = primary_goal
    else:
        goal = f"PRIMARY TASK OBJECTIVE:\n{primary_goal}\n\nLATEST USER FOLLOW-UP:\n{requested_goal}"

    checkpoint_id = _store.get_agentic_checkpoint_id(chat.id)
    if not checkpoint_id or not re.fullmatch(r"[0-9a-fA-F]{32}", checkpoint_id):
        checkpoint_id = _matching_task_checkpoint(workspace_root, primary_goal) or control_task_id
        _store.set_agentic_checkpoint_id(chat.id, checkpoint_id)
    task_id = checkpoint_id
    task_dir = workspace_root / ".agent_tasks" / task_id
    task_dir.mkdir(parents=True, exist_ok=True)
    task_file = task_dir / "task.md"
    plan_file = task_dir / "plan.md"
    progress_file = task_dir / "progress.md"
    if not task_file.exists():
        task_file.write_text(
            f"# Task\n\n## Primary goal\n{primary_goal}\n\n## Current objective\n{goal}\n\n## User request for this run\n{requested_goal}\n\n## Completion\nComplete the requested deliverable in the workspace. A plan or description alone is not completion.\n\n"
            "## Safety guard\nStop if an identical tool-action/result cycle repeats 10 times in a row, or when the user requests stop.\n\n"
            + ("## Command permission\nThe user enabled allowlisted local commands for this task.\n" if allow_commands else "## Command permission\nLocal commands are disabled for this task.\n"),
            encoding="utf-8",
        )
        plan_file.write_text(_build_task_plan(primary_goal), encoding="utf-8")
        progress_file.write_text(
            "# Progress\n\nThis file is maintained automatically by the application. Do not read or write it during the task.\n",
            encoding="utf-8",
        )
    else:
        # Keep one durable task folder and preserve its plan/progress across follow-ups.
        with task_file.open("a", encoding="utf-8") as stream:
            stream.write(
                f"\n## Follow-up request ({time.strftime('%Y-%m-%d %H:%M:%S UTC', time.gmtime())})\n"
                f"{requested_goal}\n"
            )
        existing_plan = plan_file.read_text(encoding="utf-8") if plan_file.exists() else ""
        if not _plan_has_actionable_steps(existing_plan):
            plan_file.write_text(_build_task_plan(primary_goal), encoding="utf-8")
        if not progress_file.exists():
            progress_file.write_text("# Task progress\n", encoding="utf-8")

    progress_activity = [
        (stamp, message) for stamp, message in _read_progress_activity(progress_file)
        if not message.lower().startswith((
            "waiting for the local model", "reading ", "updating ", "reviewing project",
            "inspecting project", "searching project", "using a tool", "received the tool",
            "preparing response", "generating with the local model", "loading task guidance",
        ))
    ]
    completed_items: list[str] = []
    blockers: list[str] = []
    for _, old_message in progress_activity:
        if old_message.startswith("Saved "):
            saved_path = old_message[6:].rstrip(".")
            if saved_path.replace("\\", "/").startswith(f".agent_tasks/{task_id}/"):
                blockers.append("Previous project files were saved inside the internal checkpoint folder; recreate them in the workspace project folder.")
            elif saved_path not in completed_items:
                completed_items.append(saved_path)
    progress_state = {
        "status": "In progress",
        "next": "Continue implementation, then verify the deliverables.",
        "completed": completed_items,
        "blockers": blockers,
        "activity": progress_activity[-12:],
    }

    def save_progress() -> None:
        progress_file.write_text(
            _render_progress(primary_goal, progress_state["status"], progress_state["completed"],
                             progress_state["next"], progress_state["blockers"], progress_state["activity"]),
            encoding="utf-8",
        )

    save_progress()
    site_project_guidance = ""
    if re.search(r"\b(website|web site|site|frontend|front-end|html|css)\b", goal, re.IGNORECASE):
        site_project_guidance = (
            "For a website request, create a clearly named child folder under the selected workspace and keep the site files together. "
            "Do not make a placeholder page or a page containing only a generic heading. Build a coherent, useful first version with meaningful content, navigation, responsive styling, and working interactions. "
        )
        if re.search(r"\b(shop|store|cart|checkout|e-?commerce|sell|buy|product)\b", goal, re.IGNORECASE):
            site_project_guidance += (
                "For this storefront, implement a usable product catalog, add/remove and quantity controls, cart totals, and a clear demo checkout flow. "
                "Do not ask for payment credentials or preferences; without credentials, use a clearly labeled local demo checkout and never imply a real payment was taken. "
                "Choose sensible visual defaults and include several realistic sample products. "
            )
    task_prompt = (
        "AGENTIC TASK MODE. Execute the goal using the available workspace tools; a written plan is never a substitute for the requested deliverable. "
        "There is no time or total-step limit. Continue until the goal is complete, you need essential user input, the user stops you, or the repeated-loop guard fires. Use sensible defaults for optional choices; do not ask for approval or confirmation between steps. Ask only when a missing detail makes safe or useful progress impossible. "
        f"Use .agent_tasks/{task_id}/ only for task.md and plan.md; the application manages progress.md. This checkpoint directory is not the deliverable directory. Create project files in the selected workspace root or a named child folder such as storefront/, never beneath .agent_tasks/. Read task.md and plan.md once; do not read progress.md. Before changing project files, replace the baseline plan status with a task-specific plan of 4-8 concrete checkbox milestones and observable acceptance checks. A one-sentence summary is not a plan. " + ("This is a continuation: inspect the current deliverable, keep completed work, fix only concrete gaps, and do not rewrite files that already meet the goal. Refine the existing checklist with task-specific actions; do not create another plan. " if is_continuation else f"Refine the prefilled plan into 4-8 task-specific checkbox milestones, then start implementation immediately. ") + "Prioritize actual deliverables over checkpoint housekeeping. "
        + site_project_guidance
        + ("Continue the existing task from its saved files and make only necessary changes. " if is_continuation else "")
        + "Inspect relevant files briefly, then create or edit the actual deliverable, avoid overwriting unrelated files, and verify changes with available checks. "
        + ("Use the approved command tool only when needed for setup or verification. It is restricted to an executable allowlist and runs without a shell. " if allow_commands else "Command execution is disabled; create and inspect files using workspace tools and state any checks you could not run. ")
        + "Stop if an identical tool-action/result loop repeats 10 times consecutively. Do not disguise repeated retries by changing irrelevant arguments. "
        "Do not reveal private chain-of-thought. End with a concise summary of changes, checks actually run, and remaining work.\n\n"
        f"Task checkpoint files are in .agent_tasks/{task_id}/ (task.md, plan.md, progress.md).\n\n"
        f"GOAL:\n{goal}"
    )

    def report(message: str) -> None:
        if message == "Working on your request…":
            message = f"Agentic task running until complete or stopped. Loop guard: 10 identical cycles. Checkpoint: .agent_tasks/{task_id}/"
        emit_chat_progress(chat.id, message)
        lower = message.lower()
        if lower.startswith((
            "waiting for the local model", "reading ", "updating ", "reviewing project",
            "inspecting project", "searching project", "using a tool", "received the tool",
            "preparing response", "generating with the local model", "loading task guidance",
        )):
            return

        stamp = time.strftime("%Y-%m-%d %H:%M:%S UTC", time.gmtime())
        if message.startswith("Saved "):
            saved_path = message[6:].rstrip(".")
            normalized = saved_path.replace("\\", "/")
            if normalized.startswith(f".agent_tasks/{task_id}/"):
                blocker = "A deliverable was written inside the internal checkpoint directory. Recreate it in the workspace root or a named project folder."
                if blocker not in progress_state["blockers"]:
                    progress_state["blockers"].append(blocker)
                progress_state["status"] = "Needs correction"
                progress_state["next"] = "Move or recreate the deliverable outside .agent_tasks/, then verify it."
            elif saved_path not in progress_state["completed"]:
                progress_state["completed"].append(saved_path)
                progress_state["status"] = "In progress"
                progress_state["next"] = "Finish the remaining deliverables and run relevant checks."
        elif "timed out" in lower:
            progress_state["status"] = "Paused after timeout"
            progress_state["next"] = "Continue from the saved workspace files and verify the unfinished work."
            progress_state["blockers"].append(message)
        elif lower.startswith("stopped by the user") or lower.startswith("the task stopped"):
            progress_state["status"] = "Stopped by user"
            progress_state["next"] = "Resume this task to continue from its saved files."
        elif "found gaps:" in lower or "incomplete" in lower:
            progress_state["status"] = "Needs correction"
            progress_state["next"] = message
        elif "tool reported a problem" in lower or "reported a problem" in lower or "error" in lower:
            progress_state["status"] = "Needs attention"
            progress_state["blockers"].append(message)
        elif "task ended" in lower:
            progress_state["status"] = "Finishing"
            progress_state["next"] = "Review the final response and confirm the deliverables and checks."
        elif "plan without creating files" in lower:
            progress_state["status"] = "Implementation required"
            progress_state["next"] = "Create the actual requested project files; a plan is not the deliverable."

        progress_state["activity"].append((stamp, message))
        progress_state["activity"] = progress_state["activity"][-12:]
        progress_state["blockers"] = progress_state["blockers"][-6:]
        save_progress()
    def should_stop() -> str | None:
        if control_path.exists():
            return "Stopped by the user. The latest checkpoint is in progress.md."

        return None

    _store.add_message(chat.id, "user", requested_goal)
    previous_allow_commands = agent.cfg.allow_commands
    previous_command_timeout = agent.cfg.command_timeout_sec
    previous_request_timeout = agent.cfg.request_timeout_sec
    previous_retries = agent.cfg.retries
    previous_max_tokens = agent.cfg.max_tokens
    previous_model_progress = getattr(agent.model, "progress_callback", None)
    previous_model_cancel = getattr(agent.model, "cancel_requested", None)
    agent.model.cancel_requested = lambda: should_stop() is not None
    agent.model.progress_callback = lambda token_count: emit_chat_progress(
        chat.id, f"Generating with the local model… (~{token_count:,} tokens)"
    )
    agent.cfg.allow_commands = allow_commands
    agent.cfg.command_timeout_sec = 300 if allow_commands else previous_command_timeout
    # Local 14B models can need several minutes for structured code/tool output.
    # Allow one longer inference attempt while preserving the durable checkpoint.
    agent.cfg.request_timeout_sec = max(previous_request_timeout, 600)
    agent.cfg.retries = 1
    agent.cfg.max_tokens = min(previous_max_tokens, 4096)
    try:
        reply = agent.run(
            task_prompt,
            progress_callback=report,
            stop_reason=should_stop,
            unbounded=True,
            loop_repeat_limit_override=10,
            duplicate_tool_limit_override=10,
            require_file_write_before_final=bool(
                re.search(r"\b(create|build|make|write|implement|develop|generate)\b", goal, re.IGNORECASE)
                and re.search(r"\b(website|web site|site|project|script|file|application|app)\b", goal, re.IGNORECASE)
            ),
            require_site_storefront=bool(
                re.search(r"\b(website|web site|site|html)\b", goal, re.IGNORECASE)
                and re.search(r"\b(shop|store|cart|checkout|e-?commerce|sell|buy|product)\b", goal, re.IGNORECASE)
            ),
            persistent_context=(
                "ACTIVE AGENTIC TASK: keep working toward the chat's primary objective; treat the latest message as a follow-up unless it clearly replaces the goal. A plan alone is not completion. Inspect saved project files, preserve completed work, and fix only concrete gaps. "
                + f"Checkpoint folder: .agent_tasks/{task_id}/. progress.md is app-managed and must not be read."
            ),
        )
    finally:
        agent.cfg.allow_commands = previous_allow_commands
        agent.cfg.command_timeout_sec = previous_command_timeout
        agent.cfg.request_timeout_sec = previous_request_timeout
        agent.cfg.retries = previous_retries
        agent.cfg.max_tokens = previous_max_tokens
        agent.model.progress_callback = previous_model_progress
        agent.model.cancel_requested = previous_model_cancel

    report(f"Task ended. Checkpoint folder: .agent_tasks/{task_id}/")
    reply_status = reply.casefold()
    if "timed out" in reply_status or "timeout" in reply_status:
        progress_state["status"] = "Paused after timeout"
        progress_state["next"] = "Continue from the saved workspace files and verify the unfinished work."
    elif "stopped" in reply_status or "loop repeated" in reply_status:
        progress_state["status"] = "Stopped before completion"
        progress_state["next"] = "Resume after reviewing the blocker and the saved files."
    elif "incomplete" in reply_status or "could not" in reply_status or "missing" in reply_status:
        progress_state["status"] = "Incomplete"
        progress_state["next"] = "Review the remaining work in the final response, then continue this task."
    else:
        progress_state["status"] = "Finished — review final response"
        progress_state["next"] = "Confirm the listed deliverables and checks in the final response."
    save_progress()
    reply += f"\n\nTask checkpoint: .agent_tasks/{task_id}/ (task.md, plan.md, progress.md)"
    control_path.unlink(missing_ok=True)
    _store.add_message(chat.id, "assistant", reply)
    return {"chat_id": chat.id, "response_text": reply, "task_id": task_id}


def handle_agentic_task(req: dict) -> dict:
    chat_id = req.get("chat_id")
    task_id = str(req.get("task_id") or "").strip()
    if chat_id is None or not task_id:
        raise ValueError("An agentic task needs a chat and a task ID.")
    chat_id = int(chat_id)
    if not _store.try_start_agentic_task(chat_id, task_id, os.getpid()):
        raise ValueError(
            "An Agentic task is already running for this chat, possibly in another Agent-MK window. "
            "Wait for it to finish or stop that task before starting another."
        )
    try:
        return _run_agentic_task(req)
    finally:
        _store.finish_agentic_task(chat_id, task_id)

def handle_list_chats(_req: dict) -> dict:
    chats = [asdict(c) for c in _store.list_chats()]
    return {"chats": chats}


def handle_create_chat(req: dict) -> dict:
    title = req.get("title") or "New chat"
    project_path = req.get("project_path")
    model_name = req.get("model_name") or "qwen2.5-coder:7b"
    mode = str(req.get("mode") or "chat").strip().lower()

    try:
        chat = _store.create_chat(title, project_path, model_name, mode)
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
    "prepare_agentic_task": handle_prepare_agentic_task,
    "agentic_task": handle_agentic_task,
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

















