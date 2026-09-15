"""
Agent runtime configuration (Config dataclass). Extracted from assistant.py.
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


@dataclasses.dataclass(frozen=True)
class AgentPolicy:
    name: str
    max_tool_steps: int
    max_read_bytes_per_file: int
    snapshot_max_files: int
    snapshot_chars_per_file: int
    duplicate_tool_limit: int
    temperature: float


def infer_model_size_b(model: str) -> float | None:
    """Best-effort parameter-size inference from common model tags."""
    import re as _re
    text = (model or "").lower()
    moe = _re.search(r"(\d+)x(\d+(?:\.\d+)?)b", text)
    if moe:
        return float(moe.group(1)) * float(moe.group(2))
    matches = _re.findall(r"(?<![a-z0-9])([0-9]+(?:\.[0-9]+)?)b\b", text)
    if not matches:
        return None
    return max(float(x) for x in matches)


def agent_policy_for(model: str, profile: str = "auto") -> AgentPolicy:
    """Return a conservative or deep tool policy without changing model APIs."""
    requested = (profile or "auto").strip().lower()
    size_b = infer_model_size_b(model)
    is_large = size_b is not None and size_b > 14

    if requested == "fast":
        return AgentPolicy("fast", 10, 350_000, 50, 6_000, 2, 0.2)
    if requested == "deep":
        return AgentPolicy("deep", 18, 1_000_000, 120, 10_000, 2, 0.15)
    if is_large:
        return AgentPolicy("auto-deep", 16, 800_000, 100, 9_000, 2, 0.15)
    return AgentPolicy("auto-standard", 12, 500_000, 70, 8_000, 2, 0.2)




@dataclasses.dataclass
class Config:
    project_root: Path
    db_path: Path
    audit_log_path: Path

    provider: str = "ollama"
    model: str = "qwen2.5-coder:14b"

    # Ollama default. For LM Studio use:
    # http://127.0.0.1:1234/v1/chat/completions
    endpoint: str = "http://127.0.0.1:11434/api/chat"

    temperature: float = 0.2
    max_tokens: int = 8192
    # auto = <=14B standard safeguards, >14B deeper verification; fast/deep override it.
    agent_profile: str = "auto"

    request_timeout_sec: int = 180
    command_timeout_sec: int = 30
    retries: int = 3

    # Hard cap on how many recent DB rows are even considered before
    # char-based trimming (see agent_core/context.py). This is a
    # performance/sanity backstop, NOT the primary truncation mechanism --
    # that's effective_context_char_budget(), which derives the real
    # limit from the model's actual context window when knowable. Raised
    # from the old default of 30 (which was small enough to silently cut
    # off history well before the char budget ever became relevant).
    max_history_messages: int = 200
    snapshot_max_files: int = 80
    snapshot_chars_per_file: int = 8_000
    duplicate_tool_limit: int = 2
    # Fallback only, used when the real per-model context length can't be
    # looked up (Ollama unreachable, non-Ollama provider, etc.) -- see
    # agent_core/context.py's effective_context_char_budget().
    max_context_chars: int = 120_000

    max_files_per_response: int = 20
    max_write_bytes_per_file: int = 2_000_000
    max_read_bytes_per_file: int = 500_000
    # 0 means "use the selected agent policy"; CLI can override with a positive value.
    max_tool_steps: int = 0

    dry_run: bool = False
    backups: bool = True
    allow_commands: bool = False

    # Only these command executables are allowed when command execution is enabled.
    command_allowlist: tuple[str, ...] = (
        "python",
        "python3",
        "pytest",
        "git",
        "ruff",
        "black",
        "mypy",
        "node",
        "npm",
        "npx",
    )

    ignored_dirs: tuple[str, ...] = (
        ".git",
        ".idea",
        ".vscode",
        "__pycache__",
        "node_modules",
        ".venv",
        "venv",
        "dist",
        "build",
        ".agent_backups",
    )

    ignored_extensions: tuple[str, ...] = (
        ".png", ".jpg", ".jpeg", ".gif", ".webp",
        ".pdf", ".zip", ".gz", ".tar", ".7z",
        ".exe", ".dll", ".so", ".dylib",
        ".pyc", ".pyo",
    )


# ============================================================
# Logging
# ============================================================



