"""
Tool registry and the concrete tools (list_files, read_file, write_file, search_files, workspace_snapshot, run_command). Extracted from assistant.py.
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

from agent_core.config import Config
from agent_core.workspace import Workspace

ToolFn = Callable[[dict[str, Any]], Any]


@dataclasses.dataclass
class Tool:
    name: str
    description: str
    schema: dict[str, Any]
    fn: ToolFn


class ToolRegistry:
    def __init__(self):
        self.tools: dict[str, Tool] = {}

    def register(self, tool: Tool) -> None:
        if tool.name in self.tools:
            raise ValueError(f"Duplicate tool: {tool.name}")
        self.tools[tool.name] = tool

    def describe_for_prompt(self) -> str:
        data = []
        for tool in self.tools.values():
            data.append(
                {
                    "name": tool.name,
                    "description": tool.description,
                    "arguments": tool.schema,
                }
            )
        return json.dumps(data, ensure_ascii=False, indent=2)

    def call(self, name: str, args: dict[str, Any]) -> Any:
        if name not in self.tools:
            raise KeyError(f"Unknown tool: {name}")
        return self.tools[name].fn(args)


# ============================================================
# Model output parsing
# ============================================================


def run_command_safely(
    cfg: Config,
    workspace: Workspace,
    args: dict[str, Any],
) -> dict[str, Any]:
    if not cfg.allow_commands:
        return {
            "ok": False,
            "error": "Command execution is disabled.",
        }

    command = args.get("command")
    if not isinstance(command, list) or not command:
        return {
            "ok": False,
            "error": "command must be a non-empty list of strings",
        }

    if not all(isinstance(x, str) for x in command):
        return {
            "ok": False,
            "error": "command elements must be strings",
        }

    exe = Path(command[0]).name

    if exe not in cfg.command_allowlist:
        return {
            "ok": False,
            "error": f"Executable not allowed: {exe}",
        }

    try:
        completed = subprocess.run(
            command,
            cwd=str(workspace.root),
            capture_output=True,
            text=True,
            timeout=cfg.command_timeout_sec,
            shell=False,
        )

        return {
            "ok": completed.returncode == 0,
            "returncode": completed.returncode,
            "stdout": completed.stdout[-20_000:],
            "stderr": completed.stderr[-20_000:],
        }

    except subprocess.TimeoutExpired as e:
        return {
            "ok": False,
            "error": f"Command timed out after "
                     f"{cfg.command_timeout_sec}s",
            "stdout": (e.stdout or "")[-10_000:]
                if isinstance(e.stdout, str) else "",
            "stderr": (e.stderr or "")[-10_000:]
                if isinstance(e.stderr, str) else "",
        }

    except Exception as e:
        return {
            "ok": False,
            "error": str(e),
        }


# ============================================================
# Context management
# ============================================================


def build_tools(
    cfg: Config,
    workspace: Workspace,
) -> ToolRegistry:
    tools = ToolRegistry()

    tools.register(
        Tool(
            name="list_files",
            description=(
                "List text/code files in the workspace. "
                "Use this before guessing project structure."
            ),
            schema={
                "limit": "optional integer, default 500"
            },
            fn=lambda args: {
                "ok": True,
                "files": workspace.list_files(
                    int(args.get("limit", 500))
                ),
            },
        )
    )

    def read_file(args: dict[str, Any]) -> dict[str, Any]:
        path = args.get("path")
        if not isinstance(path, str):
            return {"ok": False, "error": "path must be a string"}

        start_line = args.get("start_line")
        end_line = args.get("end_line")
        max_chars = args.get("max_chars")
        try:
            start_line = int(start_line) if start_line is not None else None
            end_line = int(end_line) if end_line is not None else None
            max_chars = int(max_chars) if max_chars is not None else None
        except (TypeError, ValueError):
            return {"ok": False, "error": "line/character bounds must be integers"}

        text = workspace.read_text(
            path, start_line=start_line, end_line=end_line, max_chars=max_chars
        )
        return {
            "ok": True,
            "path": path,
            "content": text,
            "sha256": hashlib.sha256(text.encode("utf-8")).hexdigest(),
            "partial": start_line is not None or end_line is not None or max_chars is not None,
        }

    tools.register(
        Tool(
            name="read_file",
            description="Read one UTF-8 text/code file.",
            schema={
                "path": "workspace-relative file path",
                "start_line": "optional 1-based first line",
                "end_line": "optional inclusive last line",
                "max_chars": "optional character cap",
            },
            fn=read_file,
        )
    )

    def write_file(args: dict[str, Any]) -> dict[str, Any]:
        path = args.get("path")
        content = args.get("content")

        if not isinstance(path, str):
            return {
                "ok": False,
                "error": "path must be a string",
            }

        if not isinstance(content, str):
            return {
                "ok": False,
                "error": "content must be a string",
            }

        result = workspace.atomic_write(
            path,
            content,
        )
        return {
            "ok": True,
            **result,
        }

    tools.register(
        Tool(
            name="write_file",
            description=(
                "Atomically create or replace one text/code file. "
                "Existing files are backed up when enabled."
            ),
            schema={
                "path": "workspace-relative file path",
                "content": "complete new file content",
            },
            fn=write_file,
        )
    )

    def search_files(args: dict[str, Any]) -> dict[str, Any]:
        query = args.get("query")

        if not isinstance(query, str):
            return {
                "ok": False,
                "error": "query must be a string",
            }

        return {
            "ok": True,
            "results": workspace.search(
                query,
                max_results=int(args.get("max_results", 20)),
            ),
        }

    tools.register(
        Tool(
            name="search_files",
            description=(
                "Search workspace text/code files for relevant terms."
            ),
            schema={
                "query": "search string",
                "max_results": "optional integer",
            },
            fn=search_files,
        )
    )

    tools.register(
        Tool(
            name="project_summary",
            description=(
                "Return compact project metadata: manifests, likely entry points, "
                "recent files, extensions, and bounded file count. Prefer this "
                "before broad source inspection on large projects."
            ),
            schema={"max_files": "optional integer"},
            fn=lambda args: {
                "ok": True,
                "summary": workspace.project_summary(
                    max_files=int(args.get("max_files", 120))
                ),
            },
        )
    )

    tools.register(
        Tool(
            name="workspace_snapshot",
            description=(
                "Return a bounded snapshot of many workspace files. "
                "Use when project context is small enough to inspect broadly."
            ),
            schema={},
            fn=lambda args: {
                "ok": True,
                "snapshot": workspace.snapshot(
                    max_files=int(args.get("max_files", cfg.snapshot_max_files)),
                    max_chars_per_file=int(args.get("max_chars_per_file", cfg.snapshot_chars_per_file)),
                ),
            },
        )
    )

    tools.register(
        Tool(
            name="run_command",
            description=(
                "Run a command from the configured executable allowlist. "
                "Disabled unless --allow-commands is supplied."
            ),
            schema={
                "command": (
                    "array of strings, e.g. "
                    '["python","-m","pytest","-q"]'
                )
            },
            fn=lambda args: run_command_safely(
                cfg,
                workspace,
                args,
            ),
        )
    )

    return tools


# ============================================================
# CLI
# ============================================================



