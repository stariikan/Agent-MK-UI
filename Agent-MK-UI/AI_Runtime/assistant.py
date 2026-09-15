#!/usr/bin/env python3
"""
assistant.py

A hardened local-agent runtime for Ollama / LM Studio / llama.cpp-style
OpenAI-compatible APIs.

    agent_core/config.py         Config dataclass
    agent_core/logging_setup.py  setup_logging()
    agent_core/database.py       Database (SQLite history/audit)
    agent_core/workspace.py      Workspace (safe file I/O)
    agent_core/model_client.py   ModelClient, http_post_json, ModelError
    agent_core/actions.py        SYSTEM_PROMPT, parse_agent_action, extract_json_object
    agent_core/context.py        trim_messages, token estimation, real context-length lookup
    agent_core/tools.py          Tool, ToolRegistry, build_tools, run_command_safely
    agent_core/agent.py          LocalAgent (the multi-step tool-use loop)
    agent_core/cli.py            argument parsing, make_config, interactive(), main()

This was a pure mechanical extraction -- no logic changed, only where it
lives. Everything below is re-exported under the same names so existing
callers (headless.py, this file's own __main__ block) don't need to
change at all: `import assistant; assistant.Config(...)`,
`assistant.LocalAgent(...)`, etc. all still work exactly as before.

Python 3.10+. No third-party dependencies required.
"""

from __future__ import annotations

from agent_core.config import Config
from agent_core.logging_setup import setup_logging
from agent_core.database import Database
from agent_core.workspace import WorkspaceError, Workspace
from agent_core.model_client import ModelError, http_post_json, ModelClient
from agent_core.actions import extract_json_object, parse_agent_action, SYSTEM_PROMPT
from agent_core.context import (
    trim_messages,
    estimate_tokens,
    context_usage_from_messages,
    get_model_context_length,
    effective_context_char_budget,
)
from agent_core.tools import Tool, ToolRegistry, run_command_safely, build_tools
from agent_core.agent import LocalAgent
from agent_core.cli import parse_args, default_endpoint, make_config, interactive, main

__all__ = [
    "Config",
    "setup_logging",
    "Database",
    "WorkspaceError",
    "Workspace",
    "ModelError",
    "http_post_json",
    "ModelClient",
    "extract_json_object",
    "parse_agent_action",
    "SYSTEM_PROMPT",
    "trim_messages",
    "estimate_tokens",
    "context_usage_from_messages",
    "get_model_context_length",
    "effective_context_char_budget",
    "Tool",
    "ToolRegistry",
    "run_command_safely",
    "build_tools",
    "LocalAgent",
    "parse_args",
    "default_endpoint",
    "make_config",
    "interactive",
    "main",
]


if __name__ == "__main__":
    raise SystemExit(main())
