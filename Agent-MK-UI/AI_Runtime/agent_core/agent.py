"""
LocalAgent: the multi-step tool-use loop tying config/db/workspace/model/tools together. Extracted from assistant.py.
"""

from __future__ import annotations

import argparse
import dataclasses
import datetime as dt
import difflib
import hashlib
import itertools
import json
import logging
import os
import re
import shutil
import sqlite3
import subprocess
import sys
import tempfile
import textwrap
import time
import traceback
import urllib.error
import urllib.request
import uuid
from collections import deque
from pathlib import Path
from typing import Any, Callable, Iterable, Optional

from agent_core.config import Config, AgentPolicy, agent_policy_for
from agent_core.database import Database
from agent_core.workspace import Workspace
from agent_core.model_client import ModelClient
from agent_core.tools import ToolRegistry
from agent_core.actions import SYSTEM_PROMPT, parse_agent_action
from agent_core.skills import skill_context
from agent_core.context import (
    trim_messages,
    context_usage_from_messages,
    get_model_context_length,
    effective_context_char_budget,
)

class LocalAgent:
    def __init__(
        self,
        cfg: Config,
        db: Database,
        workspace: Workspace,
        model: ModelClient,
        tools: ToolRegistry,
        logger: logging.Logger,
    ):
        self.cfg = cfg
        self.db = db
        self.workspace = workspace
        self.model = model
        self.tools = tools
        self.logger = logger
        self._policy: AgentPolicy = agent_policy_for(cfg.model, cfg.agent_profile)

    def _refresh_policy(self) -> AgentPolicy:
        self._policy = agent_policy_for(self.cfg.model, self.cfg.agent_profile)
        self.cfg.temperature = self._policy.temperature
        self.cfg.max_read_bytes_per_file = self._policy.max_read_bytes_per_file
        self.cfg.snapshot_max_files = self._policy.snapshot_max_files
        self.cfg.snapshot_chars_per_file = self._policy.snapshot_chars_per_file
        self.cfg.duplicate_tool_limit = self._policy.duplicate_tool_limit
        return self._policy

    def _effective_max_tool_steps(self) -> int:
        policy = self._refresh_policy()
        return max(1, self.cfg.max_tool_steps, policy.max_tool_steps)

    def _compact_tool_result(self, result: Any) -> str:
        raw = json.dumps(result, ensure_ascii=False, default=str)
        # Keep tool observations bounded so one verbose tool cannot evict the
        # actual task from context.
        max_chars = min(self._effective_max_chars() // 3, 30_000)
        if len(raw) <= max_chars:
            return raw
        return raw[:max_chars] + '\n... [tool result truncated by agent runtime]'

    @staticmethod
    def _tool_signature(name: str, args: dict[str, Any]) -> str:
        return name + ':' + json.dumps(args, ensure_ascii=False, sort_keys=True, default=str)

    @staticmethod
    def _website_contract_issues(files: dict[str, str], *, storefront: bool) -> list[str]:
        html_sources = "\n".join(
            content for path, content in files.items()
            if path.casefold().endswith((".html", ".htm"))
        )
        if not html_sources.strip():
            return ["an HTML entry page"]

        visible_text = re.sub(r"(?is)<(script|style)\b[^>]*>.*?</\1>", " ", html_sources)
        visible_text = re.sub(r"(?s)<!--.*?-->", " ", visible_text)
        visible_text = re.sub(r"<[^>]+>", " ", visible_text)
        visible_text = re.sub(r"\s+", " ", visible_text).strip()
        issues: list[str] = []
        if len(visible_text) < 180:
            issues.append("meaningful page content (the current page is still placeholder-sized)")
        if not re.search(r"(?i)<(header|nav|main|section|footer)\b", html_sources):
            issues.append("a clear page structure with navigation and content sections")
        combined = "\n".join(files.values())
        if not re.search(r"(?i)name\s*=\s*['\"]viewport['\"]", html_sources) and not re.search(r"(?i)@media\b", combined):
            issues.append("responsive layout for mobile screens")
        if not any(path.casefold().endswith("index.html") for path in files):
            issues.append("an index.html entry page that can be opened locally")

        if storefront:
            if not re.search(r"(?i)painting|canvas|oil|acrylic|product", combined):
                issues.append("painting or art products in the catalog")
            priced_items = re.findall(r"(?i)(?:[$€£]\s*\d|\b\d+\.\d{2}\b)", combined)
            if len(priced_items) < 4:
                issues.append("at least four sample products with prices")
            if not re.search(r"(?i)cart", combined) or not re.search(r"(?i)add\s*to\s*cart|addToCart|add_to_cart", combined):
                issues.append("a working add-to-cart interaction")
            if not re.search(r"(?i)quantity|qty", combined) or not re.search(r"(?i)subtotal|total", combined):
                issues.append("quantity controls and a calculated cart total")
            if not re.search(r"(?i)checkout", combined):
                issues.append("a checkout flow (a clearly labeled local demo is acceptable)")
            if not re.search(r"(?i)addEventListener|on(click|change|submit)", combined):
                issues.append("JavaScript event handling for the shop controls")
            if not re.search(r"(?is)<form\b|createElement\(\s*['\"]form['\"]", combined):
                issues.append("a real checkout/payment form, not only a thank-you message")
            if not re.search(r"(?is)(?:type\s*=\s*['\"]email['\"]|name\s*=\s*['\"]email['\"])", combined):
                issues.append("an email field in the checkout form")
            if not re.search(r"(?i)(?:name\s*=\s*['\"](?:address|shipping-address)['\"]|autocomplete\s*=\s*['\"]street-address['\"])", combined):
                issues.append("a delivery/address field in checkout")
            if not re.search(r"(?i)type\s*=\s*['\"]submit['\"]|place order|complete order|pay now", combined):
                issues.append("a clear order submission button")
            if not re.search(r"(?i)demo.{0,30}payment|payment.{0,30}demo|no (?:real )?(?:payment|card)|not charged", combined):
                issues.append("a clearly labeled demo payment notice that does not imply real money is charged")
        return issues

    @staticmethod
    def _phase_for_tool(name: str) -> str:
        if name in {"project_summary", "list_files", "search_files", "read_file"}:
            return "INSPECT"
        if name in {"write_file"}:
            return "IMPLEMENT"
        if name in {"run_command"}:
            return "VERIFY"
        return "WORK"

    def _effective_max_chars(self) -> int:
        """
        The real, currently-in-effect character budget for context
        trimming -- derived from the model's actual context window when
        that's knowable (see effective_context_char_budget), not just the
        fixed fallback constant. Both the real trim (_base_messages) and
        the reported usage (get_context_usage) call this exact same
        method, so what's displayed to the user is always the number
        actually governing behavior, never a different one.
        """
        model_context_length = get_model_context_length(self.cfg.endpoint, self.cfg.model)
        return effective_context_char_budget(self.cfg.max_context_chars, model_context_length)

    def _system_message(self, user_prompt: str) -> str:
        workspace_mode = (
            "an attached project"
            if getattr(self.cfg, "workspace_is_project", True)
            else "an isolated chat workspace (no project is attached)"
        )
        skill_text = skill_context(user_prompt)
        parts = [
            SYSTEM_PROMPT,
            "CURRENT WORKSPACE: "
            + workspace_mode
            + ". General questions do not require file inspection. File operations are limited to this workspace.",
        ]
        if skill_text:
            parts.append(skill_text)
        parts.append("AVAILABLE TOOLS: " + self.tools.describe_for_prompt())
        return "\n\n".join(parts)

    def _base_messages(self, user_prompt: str) -> list[dict[str, str]]:
        history = self.db.recent_messages(self.cfg.max_history_messages)

        # run() stores the current user message before building context. Remove
        # that saved copy here because it is appended explicitly below.
        if (
            history
            and history[-1].get("role") == "user"
            and history[-1].get("content") == user_prompt
        ):
            history = history[:-1]

        messages = [{"role": "system", "content": self._system_message(user_prompt)}]
        messages.extend(history)
        messages.append({"role": "user", "content": user_prompt})
        return trim_messages(messages, self._effective_max_chars())

    def get_context_usage(self) -> dict[str, Any]:
        """Report the context usage for the system and current saved history."""
        history = self.db.recent_messages(self.cfg.max_history_messages)
        last_user_prompt = next(
            (
                message.get("content", "")
                for message in reversed(history)
                if message.get("role") == "user"
            ),
            "",
        )
        messages = [
            {"role": "system", "content": self._system_message(last_user_prompt)}
        ] + history

        model_context_length = get_model_context_length(self.cfg.endpoint, self.cfg.model)
        effective_max_chars = effective_context_char_budget(
            self.cfg.max_context_chars, model_context_length
        )
        usage = context_usage_from_messages(messages, effective_max_chars)
        usage["max_tokens_model"] = model_context_length or (self.cfg.max_context_chars // 4)
        usage["max_tokens_is_estimate"] = model_context_length is None
        usage["total_stored_messages"] = self.db.total_message_count()
        usage["remembered_message_count"] = max(0, usage["kept_message_count"] - 1)
        usage["agent_profile"] = self._refresh_policy().name
        usage["max_output_tokens"] = self.cfg.max_tokens
        return usage

    @staticmethod
    def _tool_progress_message(tool_name: str, arguments: dict[str, Any]) -> str:
        raw_path = arguments.get("path")
        filename = str(raw_path).replace("\\", "/").rsplit("/", 1)[-1] if raw_path else ""
        if tool_name in {"project_summary", "list_files"}:
            return "Inspecting project structure…"
        if tool_name == "search_files":
            return "Searching project files…"
        if tool_name == "read_file":
            return f"Reading {filename}…" if filename else "Reading a file…"
        if tool_name == "write_file":
            return f"Updating {filename}…" if filename else "Updating a file…"
        if tool_name == "workspace_snapshot":
            return "Reviewing project files…"
        if tool_name == "run_command":
            return "Running a command…"
        if tool_name in {"list_skills", "read_skill"}:
            return "Loading task guidance…"
        return "Using a tool…"

    @staticmethod
    def _tool_result_progress(tool_name: str, result: Any) -> str:
        if not isinstance(result, dict) or not result.get("ok", True):
            return "A tool reported a problem; adjusting the next step…"
        if tool_name == "write_file":
            path = result.get("path", "the file")
            if result.get("written"):
                return f"Saved {path}."
            if result.get("dry_run"):
                return f"Previewed changes to {path}."
        if tool_name == "read_file":
            return f"Read {result.get('path', 'the file')}."
        if tool_name == "run_command":
            return "Command completed successfully." if result.get("returncode") == 0 else "Command reported a problem; adjusting the next step…"
        return "Received the tool result; continuing…"

    @staticmethod
    def _repeated_action_loop(history: deque[str], repeat_count: int, max_pattern_length: int = 20) -> int | None:
        items = list(history)
        max_length = min(max_pattern_length, len(items) // max(1, repeat_count))
        for length in range(1, max_length + 1):
            span = length * repeat_count
            tail = items[-span:]
            pattern = tail[:length]
            if all(tail[offset:offset + length] == pattern for offset in range(0, span, length)):
                return length
        return None

    def run(
        self,
        user_prompt: str,
        progress_callback: Callable[[str], None] | None = None,
        *,
        max_tool_steps_override: int | None = None,
        stop_reason: Callable[[], str | None] | None = None,
        persistent_context: str | None = None,
        unbounded: bool = False,
        loop_repeat_limit_override: int | None = None,
        duplicate_tool_limit_override: int | None = None,
        require_file_write_before_final: bool = False,
        require_site_storefront: bool = False,
    ) -> str:
        run_id = self.db.start_run(user_prompt)
        self.db.save_message("user", user_prompt)

        messages = self._base_messages(user_prompt)
        if persistent_context and messages:
            messages[0]["content"] += "\n\n" + persistent_context
            messages = trim_messages(messages, self._effective_max_chars())

        def report(message: str) -> None:
            if progress_callback is not None:
                try:
                    progress_callback(message)
                except Exception as callback_error:
                    self.logger.debug(f"Progress callback failed: {callback_error}")

        def stop_run(reason: str) -> str:
            report(reason)
            reply = f"The task stopped: {reason}"
            self.db.save_message("assistant", reply)
            self.db.finish_run(run_id, "cancelled", final_reply=reply)
            return reply

        try:
            report("Working on your request…")
            max_tool_steps = max(self._effective_max_tool_steps(), max_tool_steps_override or 0)
            duplicate_limit = duplicate_tool_limit_override if duplicate_tool_limit_override is not None else self.cfg.duplicate_tool_limit
            loop_repeat_limit = max(2, loop_repeat_limit_override or 10)
            recent_action_results: deque[str] = deque(maxlen=loop_repeat_limit * 20)
            duplicate_counts: dict[str, int] = {}
            stagnant_steps = 0
            last_phase = "UNDERSTAND"
            last_result_fingerprint: str | None = None
            invalid_tool_retries = 0
            file_write_count = 0
            plan_only_final_retries = 0
            site_quality_retries = 0
            task_files: dict[str, str] = {}
            file_write_attempts: dict[str, int] = {}
            if require_site_storefront:
                # On a continuation, validate the real saved site, not only files
                # written during this particular model run.
                for existing_path in self.workspace.list_files(limit=300):
                    normalized = existing_path.replace("\\", "/").strip("/").casefold()
                    if normalized.startswith((".agent_tasks/", ".agent_backups/")):
                        continue
                    if not normalized.endswith((".html", ".htm", ".css", ".js")):
                        continue
                    try:
                        task_files[normalized] = self.workspace.read_text(
                            existing_path, max_chars=30_000
                        )
                    except Exception:
                        continue
                if any(path.endswith(("index.html", "index.htm")) for path in task_files):
                    file_write_count = 1

            steps = itertools.count(1) if unbounded else range(1, max_tool_steps + 1)
            for step in steps:
                reason = stop_reason() if stop_reason is not None else None
                if reason:
                    return stop_run(reason)
                self.logger.info(
                    f"[agent step {step} policy={self._policy.name} phase={last_phase}]"
                )

                report("Waiting for the local model to choose the next action…")
                raw = self.model.chat(messages)
                reason = stop_reason() if stop_reason is not None else None
                if reason:
                    return stop_run(reason)
                action = parse_agent_action(raw, self.tools.tools.keys())

                if action["type"] == "final":
                    if require_file_write_before_final and file_write_count == 0:
                        plan_only_final_retries += 1
                        if plan_only_final_retries <= 3:
                            report("The model returned a plan without creating files; requesting actual implementation…")
                            messages.append({
                                "role": "user",
                                "content": (
                                    "This is an execution task. Your previous answer was only a plan and did not create any deliverable. "
                                    "Use write_file now to create the actual project files in the workspace. Do not repeat the plan. "
                                    "If blocked by missing essential information, ask one specific question instead of presenting another plan."
                                ),
                            })
                            messages = trim_messages(messages, self._effective_max_chars())
                            continue
                        reply = "I stopped because the model repeatedly returned plans without creating any project files. No deliverable files were written. Send a concrete implementation request or choose a model that can use tools reliably."
                        self.db.save_message("assistant", reply)
                        self.db.finish_run(run_id, "no_file_changes", final_reply=reply)
                        return reply
                    if require_site_storefront:
                        missing_site_parts = self._website_contract_issues(task_files, storefront=True)
                        if missing_site_parts:
                            site_quality_retries += 1
                            if site_quality_retries <= 1:
                                report("Storefront check found gaps: " + "; ".join(missing_site_parts) + ". Requesting one focused repair…")
                                messages.append({
                                    "role": "user",
                                    "content": (
                                        "Do not finish yet. The saved website is missing required parts: "
                                        + "; ".join(missing_site_parts)
                                        + ". Update only the files needed to fix these specific gaps. Keep existing working content; do not rewrite the whole site or replace it with a generic heading. "
                                        "Use sensible defaults and state only checks you actually ran. This is the final repair pass."
                                    ),
                                })
                                messages = trim_messages(messages, self._effective_max_chars())
                                continue
                            reply = (
                                "The agent wrote website files but they did not pass the basic storefront completeness check. "
                                "Still missing: " + "; ".join(missing_site_parts)
                                + ". The files remain in the workspace; the task was not reported complete."
                            )
                            self.db.save_message("assistant", reply)
                            self.db.finish_run(run_id, "incomplete_site", final_reply=reply)
                            return reply
                    report("Preparing response…")
                    reply = action["reply"].strip() or \
                        "I completed the task."

                    self.db.save_message("assistant", reply)
                    self.db.finish_run(
                        run_id,
                        "success",
                        final_reply=reply,
                    )
                    return reply

                if action["type"] == "invalid_tool":
                    report("Recovering from an invalid tool request…")
                    invalid_tool_retries += 1
                    if invalid_tool_retries > 1:
                        reply = "The model produced malformed tool-call JSON twice; I stopped safely rather than executing an ambiguous action."
                        self.db.save_message("assistant", reply)
                        self.db.finish_run(run_id, "invalid_tool", final_reply=reply)
                        return reply
                    messages.append({
                        "role": "user",
                        "content": action.get("error", "Malformed tool call. Retry with valid JSON only.")
                    })
                    messages = trim_messages(messages, self._effective_max_chars())
                    continue

                if action["type"] == "legacy_files":
                    reply = self._handle_legacy_files(
                        run_id,
                        action,
                    )
                    self.db.save_message("assistant", reply)
                    self.db.finish_run(
                        run_id,
                        "success",
                        final_reply=reply,
                    )
                    return reply

                if action["type"] == "tool":
                    tool_name = action["name"]
                    tool_args = action["arguments"]
                    signature = self._tool_signature(tool_name, tool_args)
                    duplicate_counts[signature] = duplicate_counts.get(signature, 0) + 1

                    if duplicate_counts[signature] > duplicate_limit:
                        result = {
                            "ok": False,
                            "error": (
                                "Repeated identical tool call blocked. Reassess the task, "
                                "choose a different action, or conclude if the work is complete."
                            ),
                        }
                        self.logger.warning(f"Blocked repeated tool call: {tool_name}")
                    else:
                        last_phase = self._phase_for_tool(tool_name)
                        report(self._tool_progress_message(tool_name, tool_args))
                        self.logger.info(f"  → tool: {tool_name} phase={last_phase}")
                        target_path = str(tool_args.get("path", "")).replace("\\", "/").strip("/").casefold()
                        write_count = file_write_attempts.get(target_path, 0)
                        if tool_name == "write_file" and not target_path.startswith(".agent_tasks/") and write_count >= 3:
                            result = {
                                "ok": False,
                                "error": (
                                    f"This task already replaced {target_path} three times. "
                                    "Do not rewrite the whole file again; inspect the current version, "
                                    "make progress on another missing requirement, or finish."
                                ),
                            }
                            self.logger.warning(f"Blocked repeated whole-file rewrite: {target_path}")
                        else:
                            try:
                                result = self.tools.call(tool_name, tool_args)
                                if result.get("ok") and tool_name == "write_file" and not target_path.startswith(".agent_tasks/"):
                                    file_write_attempts[target_path] = write_count + 1
                            except Exception as e:
                                result = {
                                    "ok": False,
                                    "error": str(e),
                                    "traceback": traceback.format_exc(limit=3),
                                }
                    written_path = str(tool_args.get("path", "")).replace("\\", "/").strip("/").casefold()
                    if result.get("ok") and not written_path.startswith(".agent_tasks/"):
                        if tool_name == "write_file" and isinstance(tool_args.get("content"), str):
                            task_files[written_path] = tool_args["content"]
                            file_write_count += 1
                        elif tool_name == "read_file" and isinstance(result.get("content"), str):
                            task_files[written_path] = result["content"]

                    result_text = self._compact_tool_result(result)
                    result_fingerprint = hashlib.sha256(result_text.encode("utf-8", errors="replace")).hexdigest()
                    exact_result = json.dumps(result, ensure_ascii=False, sort_keys=True, default=str)
                    exact_result_fingerprint = hashlib.sha256(exact_result.encode("utf-8", errors="replace")).hexdigest()
                    # progress.md is app-managed and changes after each status update,
                    # so hashing its full contents made metadata loops look unique.
                    checkpoint_path = str(tool_args.get("path", "")).replace("\\", "/").strip("/").casefold()
                    if tool_name in {"read_file", "write_file"} and checkpoint_path.startswith(".agent_tasks/"):
                        task_folder = checkpoint_path[len(".agent_tasks/"):].split("/", 1)[0]
                        checkpoint_signature = f"{tool_name}:.agent_tasks/{task_folder}"
                        checkpoint_result = "application-managed checkpoint operation"
                        exact_result_fingerprint = hashlib.sha256(checkpoint_result.encode("utf-8")).hexdigest()
                    else:
                        checkpoint_signature = signature
                    cycle_fingerprint = hashlib.sha256((checkpoint_signature + ":" + exact_result_fingerprint).encode("utf-8", errors="replace")).hexdigest()
                    recent_action_results.append(cycle_fingerprint)
                    repeated_loop_length = self._repeated_action_loop(recent_action_results, loop_repeat_limit)
                    if repeated_loop_length is not None:
                        reason = f"The same {repeated_loop_length}-step tool/action-result loop repeated {loop_repeat_limit} times in a row. I stopped to prevent an infinite loop."
                        report(reason)
                        reply = reason + " Checkpoint files are in the task workspace."
                        self.db.save_message("assistant", reply)
                        self.db.finish_run(run_id, "loop_detected", final_reply=reply)
                        return reply
                    if result_fingerprint == last_result_fingerprint:
                        stagnant_steps += 1
                    else:
                        stagnant_steps = 0
                    last_result_fingerprint = result_fingerprint

                    if stagnant_steps >= 3:
                        result = {
                            "ok": False,
                            "error": (
                                "The last several tool results were identical. Stop repeating "
                                "actions and reassess the task from the evidence already collected."
                            ),
                            "previous_result": result,
                        }
                        result_text = self._compact_tool_result(result)
                        stagnant_steps = 0

                    report(self._tool_result_progress(tool_name, result))
                    self.db.save_tool_event(
                        run_id,
                        tool_name,
                        tool_args,
                        result,
                    )

                    messages.append(
                        {
                            "role": "assistant",
                            "content": json.dumps(
                                action,
                                ensure_ascii=False,
                            ),
                        }
                    )

                    messages.append(
                        {
                            "role": "user",
                            "content": (
                                f"AGENT STATE: phase={last_phase}.\n"
                                "TOOL RESULT:\n"
                                + result_text
                                + "\n\nAfter a successful write, verify it. If repeated evidence stops changing, reassess rather than repeating a tool call."
                            ),
                        }
                    )

                    messages = trim_messages(
                        messages,
                        self._effective_max_chars(),
                    )
                    continue

            report("Stopped because the tool-step limit was reached.")
            reply = (
                f"I reached the maximum number of tool steps ({max_tool_steps}) before finishing. "
                "The task may need another run or a different approach."
            )
            self.db.save_message("assistant", reply)
            self.db.finish_run(
                run_id,
                "step_limit",
                final_reply=reply,
            )
            return reply

        except KeyboardInterrupt:
            self.db.finish_run(
                run_id,
                "cancelled",
                error="KeyboardInterrupt",
            )
            raise

        except Exception as e:
            cancellation_reason = stop_reason() if stop_reason is not None else None
            if cancellation_reason:
                return stop_run(cancellation_reason)

            error = f"{type(e).__name__}: {e}"
            self.logger.error(error)
            self.logger.debug(traceback.format_exc())
            is_timeout = "timed out" in str(e).casefold() or "timeout" in str(e).casefold()

            if is_timeout:
                report("The local model timed out. Any files already written are preserved; the task can continue from them.")
            self.db.finish_run(
                run_id,
                "model_timeout" if is_timeout else "error",
                error=error,
            )
            reply = (
                "The local model timed out before it could finish this step. Any files already written are still in the workspace; the task was not marked complete. Send a short follow-up in this Agentic conversation to continue from the saved project."
                if is_timeout else f"Agent failed safely: {error}"
            )

            # This reply IS what the user sees in the chat, so it has to be
            # saved here too -- otherwise this store's message count (used
            # for the context-window "remembers the last N messages"
            # reporting) silently falls out of sync with what's actually
            # shown in the UI (chat_store.py records it either way), and
            # the model itself never "sees" this turn happened if the
            # conversation continues.
            self.db.save_message("assistant", reply)
            return reply

    def _handle_legacy_files(
        self,
        run_id: str,
        action: dict[str, Any],
    ) -> str:
        files = action.get("files", {})
        saved: list[str] = []
        errors: list[str] = []

        if len(files) > self.cfg.max_files_per_response:
            return (
                f"Refused legacy payload: {len(files)} files exceeds "
                f"limit {self.cfg.max_files_per_response}."
            )

        for filename, content in files.items():
            if not isinstance(filename, str):
                errors.append("Non-string filename ignored.")
                continue

            if not isinstance(content, str):
                errors.append(f"{filename}: content is not text")
                continue

            if len(content.strip()) <= 10:
                errors.append(f"{filename}: content too short")
                continue

            try:
                result = self.workspace.atomic_write(
                    filename,
                    content,
                )
                self.db.save_tool_event(
                    run_id,
                    "legacy_write_file",
                    {
                        "path": filename,
                        "content_length": len(content),
                    },
                    result,
                )

                if result.get("written"):
                    saved.append(filename)
                elif result.get("dry_run"):
                    saved.append(f"{filename} (dry-run)")

            except Exception as e:
                errors.append(f"{filename}: {e}")

        reply_parts = []

        model_reply = action.get("reply", "").strip()
        if model_reply:
            reply_parts.append(model_reply)

        if saved:
            reply_parts.append(
                "Successfully updated workspace files: "
                + ", ".join(saved)
            )

        if errors:
            reply_parts.append(
                "Some file operations were skipped/failed: "
                + "; ".join(errors)
            )

        return "\n\n".join(reply_parts) or "Task completed."


# ============================================================
# Tool construction
# ============================================================











