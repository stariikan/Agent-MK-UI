"""
LocalAgent: the multi-step tool-use loop tying config/db/workspace/model/tools together. Extracted from assistant.py.
"""

from __future__ import annotations

import argparse
import dataclasses
import datetime as dt
import difflib
import hashlib
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

    def _base_messages(self, user_prompt: str) -> list[dict[str, str]]:
        tool_text = self.tools.describe_for_prompt()

        system = SYSTEM_PROMPT + "\n\nAVAILABLE TOOLS:\n" + tool_text

        history = self.db.recent_messages(
            self.cfg.max_history_messages
        )

        messages = [{"role": "system", "content": system}]
        messages.extend(history)
        messages.append({"role": "user", "content": user_prompt})

        return trim_messages(
            messages,
            self._effective_max_chars(),
        )

    def get_context_usage(self) -> dict[str, Any]:
        """
        Reports how full the context window is *right now*, without
        sending anything to the model -- usable any time (e.g. when the
        UI switches to a chat), not just right after a message. Combines
        the character-budget accounting (context_usage_from_messages)
        with a best-effort real model context length from Ollama, so the
        UI can show "~12,400 / 32,768 tokens" instead of just a percentage
        of an internal, model-agnostic character cap.
        """
        tool_text = self.tools.describe_for_prompt()
        system = SYSTEM_PROMPT + "\n\nAVAILABLE TOOLS:\n" + tool_text

        history = self.db.recent_messages(self.cfg.max_history_messages)
        messages = [{"role": "system", "content": system}] + history

        model_context_length = get_model_context_length(self.cfg.endpoint, self.cfg.model)
        effective_max_chars = effective_context_char_budget(self.cfg.max_context_chars, model_context_length)

        usage = context_usage_from_messages(messages, effective_max_chars)

        usage["max_tokens_model"] = model_context_length or (self.cfg.max_context_chars // 4)
        usage["max_tokens_is_estimate"] = model_context_length is None
        usage["total_stored_messages"] = self.db.total_message_count()
        usage["remembered_message_count"] = max(0, usage["kept_message_count"] - 1)  # -1 for the system message
        usage["agent_profile"] = self._refresh_policy().name
        usage["max_output_tokens"] = self.cfg.max_tokens

        return usage

    def run(self, user_prompt: str) -> str:
        run_id = self.db.start_run(user_prompt)
        self.db.save_message("user", user_prompt)

        messages = self._base_messages(user_prompt)

        try:
            max_tool_steps = self._effective_max_tool_steps()
            duplicate_counts: dict[str, int] = {}
            stagnant_steps = 0
            last_phase = "UNDERSTAND"
            last_result_fingerprint: str | None = None
            invalid_tool_retries = 0

            for step in range(1, max_tool_steps + 1):
                self.logger.info(
                    f"[agent step {step}/{max_tool_steps} policy={self._policy.name} phase={last_phase}]"
                )

                raw = self.model.chat(messages)
                action = parse_agent_action(raw)

                if action["type"] == "final":
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

                    if duplicate_counts[signature] > self.cfg.duplicate_tool_limit:
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
                        self.logger.info(f"  → tool: {tool_name} phase={last_phase}")

                        try:
                            result = self.tools.call(
                                tool_name,
                                tool_args,
                            )
                        except Exception as e:
                            result = {
                                "ok": False,
                                "error": str(e),
                                "traceback": traceback.format_exc(limit=3),
                            }

                    result_text = self._compact_tool_result(result)
                    result_fingerprint = hashlib.sha256(result_text.encode("utf-8", errors="replace")).hexdigest()
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
            error = f"{type(e).__name__}: {e}"
            self.logger.error(error)
            self.logger.debug(traceback.format_exc())

            self.db.finish_run(
                run_id,
                "error",
                error=error,
            )
            reply = f"Agent failed safely: {error}"

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



