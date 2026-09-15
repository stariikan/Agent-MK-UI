"""
Command-line entry point: argument parsing, config construction, interactive REPL, main(). Extracted from assistant.py.
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
from agent_core.logging_setup import setup_logging
from agent_core.database import Database
from agent_core.workspace import Workspace
from agent_core.model_client import ModelClient
from agent_core.tools import build_tools
from agent_core.agent import LocalAgent

def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Hardened local coding agent runtime"
    )

    parser.add_argument(
        "prompt",
        nargs="*",
        help="Task for the agent. If omitted, interactive mode starts.",
    )

    parser.add_argument(
        "--root",
        default=".",
        help="Workspace/project root.",
    )

    parser.add_argument(
        "--provider",
        choices=["ollama", "lmstudio", "llamacpp", "openai"],
        default="ollama",
    )

    parser.add_argument(
        "--model",
        default="qwen2.5-coder:14b",
    )

    parser.add_argument(
        "--endpoint",
        default=None,
    )

    parser.add_argument(
        "--temperature",
        type=float,
        default=0.2,
    )

    parser.add_argument(
        "--max-tool-steps",
        type=int,
        default=0,
    )

    parser.add_argument(
        "--agent-profile",
        choices=["auto", "fast", "deep"],
        default="auto",
        help="Agent workflow profile. Auto adapts to model size.",
    )

    parser.add_argument(
        "--dry-run",
        action="store_true",
    )

    parser.add_argument(
        "--allow-commands",
        action="store_true",
    )

    return parser.parse_args()


def default_endpoint(provider: str) -> str:
    if provider == "ollama":
        return "http://127.0.0.1:11434/api/chat"

    # LM Studio and llama.cpp commonly expose OpenAI-compatible APIs.
    return "http://127.0.0.1:1234/v1/chat/completions"


def make_config(args: argparse.Namespace) -> Config:
    root = Path(args.root).resolve()
    agent_dir = root / ".local_agent"

    return Config(
        project_root=root,
        db_path=agent_dir / "history.sqlite3",
        audit_log_path=agent_dir / "audit.log",
        provider=args.provider,
        model=args.model,
        endpoint=args.endpoint or default_endpoint(args.provider),
        temperature=args.temperature,
        max_tool_steps=args.max_tool_steps,
        agent_profile=args.agent_profile,
        dry_run=args.dry_run,
        allow_commands=args.allow_commands,
    )


def interactive(agent: LocalAgent) -> None:
    print(
        textwrap.dedent(
            """
            Local Agent interactive mode.

            Commands:
              /exit       quit
              /quit       quit
              /help       show this help
            """
        ).strip()
    )

    while True:
        try:
            prompt = input("\nYou > ").strip()
        except (EOFError, KeyboardInterrupt):
            print()
            return

        if not prompt:
            continue

        if prompt in {"/exit", "/quit"}:
            return

        if prompt == "/help":
            print("Enter a task, or /exit to quit.")
            continue

        reply = agent.run(prompt)
        print(f"\nAgent > {reply}")


def main() -> int:
    args = parse_args()
    cfg = make_config(args)

    logger = setup_logging(cfg.audit_log_path)
    db = Database(cfg.db_path)
    workspace = Workspace(cfg, logger)
    model = ModelClient(cfg, logger)
    tools = build_tools(cfg, workspace)

    agent = LocalAgent(
        cfg=cfg,
        db=db,
        workspace=workspace,
        model=model,
        tools=tools,
        logger=logger,
    )

    if args.prompt:
        prompt = " ".join(args.prompt).strip()
        reply = agent.run(prompt)
        print(reply)
        return 0

    interactive(agent)
    return 0



