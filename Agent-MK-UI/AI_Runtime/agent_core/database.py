"""
SQLite conversation history / tool-call audit trail. Extracted from assistant.py.
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



class Database:
    def __init__(self, path: Path):
        self.path = path
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self.conn = sqlite3.connect(str(path))
        self.conn.row_factory = sqlite3.Row
        self._init_schema()

    def _init_schema(self) -> None:
        self.conn.executescript(
            """
            PRAGMA journal_mode=WAL;

            CREATE TABLE IF NOT EXISTS messages (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                role TEXT NOT NULL,
                content TEXT NOT NULL,
                created_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS runs (
                id TEXT PRIMARY KEY,
                started_at TEXT NOT NULL,
                finished_at TEXT,
                status TEXT,
                user_prompt TEXT,
                final_reply TEXT,
                error TEXT
            );

            CREATE TABLE IF NOT EXISTS tool_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                run_id TEXT NOT NULL,
                tool_name TEXT NOT NULL,
                args_json TEXT,
                result_json TEXT,
                created_at TEXT NOT NULL
            );
            """
        )
        self.conn.commit()

    def save_message(self, role: str, content: str) -> None:
        self.conn.execute(
            "INSERT INTO messages(role, content, created_at) VALUES (?, ?, ?)",
            (role, content, dt.datetime.now(dt.timezone.utc).isoformat()),
        )
        self.conn.commit()

    def recent_messages(self, limit: int) -> list[dict[str, str]]:
        rows = self.conn.execute(
            """
            SELECT role, content
            FROM messages
            ORDER BY id DESC
            LIMIT ?
            """,
            (limit,),
        ).fetchall()
        rows = list(reversed(rows))
        return [{"role": r["role"], "content": r["content"]} for r in rows]

    def total_message_count(self) -> int:
        """Total user+assistant turns ever stored, regardless of max_history_messages -- used to report the true conversation length for the context-usage indicator."""
        row = self.conn.execute("SELECT COUNT(*) AS n FROM messages").fetchone()
        return int(row["n"])

    def start_run(self, prompt: str) -> str:
        run_id = str(uuid.uuid4())
        self.conn.execute(
            """
            INSERT INTO runs(id, started_at, status, user_prompt)
            VALUES (?, ?, ?, ?)
            """,
            (
                run_id,
                dt.datetime.now(dt.timezone.utc).isoformat(),
                "running",
                prompt,
            ),
        )
        self.conn.commit()
        return run_id

    def finish_run(
        self,
        run_id: str,
        status: str,
        final_reply: str = "",
        error: str = "",
    ) -> None:
        self.conn.execute(
            """
            UPDATE runs
            SET finished_at=?, status=?, final_reply=?, error=?
            WHERE id=?
            """,
            (
                dt.datetime.now(dt.timezone.utc).isoformat(),
                status,
                final_reply,
                error,
                run_id,
            ),
        )
        self.conn.commit()

    def save_tool_event(
        self,
        run_id: str,
        tool_name: str,
        args: dict[str, Any],
        result: Any,
    ) -> None:
        self.conn.execute(
            """
            INSERT INTO tool_events(
                run_id, tool_name, args_json, result_json, created_at
            )
            VALUES (?, ?, ?, ?, ?)
            """,
            (
                run_id,
                tool_name,
                json.dumps(args, ensure_ascii=False),
                json.dumps(result, ensure_ascii=False, default=str),
                dt.datetime.now(dt.timezone.utc).isoformat(),
            ),
        )
        self.conn.commit()


# ============================================================
# Workspace safety
# ============================================================

