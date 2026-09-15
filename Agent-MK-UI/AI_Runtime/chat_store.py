"""
chat_store.py

SQLite-backed storage for chat sessions and their message history. This is
deliberately separate from assistant.py's per-workspace Database class
(which is the agent's own tool-call audit trail) -- this one is the
UI-facing concept of "a chat, optionally attached to a project folder."

One project per chat: a chat either has a project_path (a folder on disk
the agent can read/write within) or none, in which case it gets an
isolated per-chat scratch folder so chats without a project don't bleed
files into each other.

Enforces a maximum of 30 chats -- create_chat() raises ChatLimitError past
that, rather than silently deleting the user's oldest chat for them.
"""

from __future__ import annotations

import os
import sqlite3
from dataclasses import dataclass, asdict
from datetime import datetime, timezone
from pathlib import Path
from typing import List, Optional

MAX_CHATS = 30


class ChatLimitError(Exception):
    """Raised when create_chat() is called at the MAX_CHATS cap."""


def _now() -> str:
    return datetime.now(timezone.utc).isoformat()


def default_db_path() -> Path:
    """
    Mirrors Orchestra.Core.Services.SetupStateStore's location logic
    (%LOCALAPPDATA%\\AgentMK) so both sides agree on where app-level state
    lives without needing to pass the path over the IPC channel.
    """
    base = os.environ.get("LOCALAPPDATA") or str(Path.home())
    folder = Path(base) / "AgentMK"
    folder.mkdir(parents=True, exist_ok=True)
    return folder / "chats.sqlite3"


@dataclass
class Chat:
    id: int
    title: str
    project_path: Optional[str]
    model: str
    created_at: str
    updated_at: str


class ChatStore:
    def __init__(self, db_path: Optional[Path] = None):
        self.db_path = db_path or default_db_path()
        self._conn = sqlite3.connect(str(self.db_path), check_same_thread=False)
        self._conn.row_factory = sqlite3.Row
        self._conn.execute("PRAGMA foreign_keys = ON")
        self._init_schema()

    def _init_schema(self) -> None:
        self._conn.executescript(
            """
            CREATE TABLE IF NOT EXISTS chats (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                title TEXT NOT NULL,
                project_path TEXT,
                model TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS messages (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                chat_id INTEGER NOT NULL REFERENCES chats(id) ON DELETE CASCADE,
                role TEXT NOT NULL,
                content TEXT NOT NULL,
                created_at TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_messages_chat_id ON messages(chat_id);
            """
        )
        self._conn.commit()

    def count_chats(self) -> int:
        row = self._conn.execute("SELECT COUNT(*) AS n FROM chats").fetchone()
        return int(row["n"])

    def list_chats(self) -> List[Chat]:
        rows = self._conn.execute(
            "SELECT * FROM chats ORDER BY updated_at DESC"
        ).fetchall()
        return [Chat(**{k: row[k] for k in row.keys()}) for row in rows]

    def get_chat(self, chat_id: int) -> Optional[Chat]:
        row = self._conn.execute(
            "SELECT * FROM chats WHERE id = ?", (chat_id,)
        ).fetchone()
        return Chat(**{k: row[k] for k in row.keys()}) if row else None

    def create_chat(self, title: str, project_path: Optional[str], model: str) -> Chat:
        if self.count_chats() >= MAX_CHATS:
            raise ChatLimitError(
                f"Maximum of {MAX_CHATS} chats reached. Delete an existing chat first."
            )

        now = _now()
        cur = self._conn.execute(
            "INSERT INTO chats (title, project_path, model, created_at, updated_at) "
            "VALUES (?, ?, ?, ?, ?)",
            (title, project_path, model, now, now),
        )
        self._conn.commit()
        chat = self.get_chat(cur.lastrowid)
        assert chat is not None
        return chat

    def delete_chat(self, chat_id: int) -> bool:
        cur = self._conn.execute("DELETE FROM chats WHERE id = ?", (chat_id,))
        self._conn.commit()
        return cur.rowcount > 0

    def touch_chat(self, chat_id: int) -> None:
        self._conn.execute(
            "UPDATE chats SET updated_at = ? WHERE id = ?", (_now(), chat_id)
        )
        self._conn.commit()

    def set_chat_model(self, chat_id: int, model: str) -> None:
        self._conn.execute(
            "UPDATE chats SET model = ?, updated_at = ? WHERE id = ?",
            (model, _now(), chat_id),
        )
        self._conn.commit()

    def set_project_path(self, chat_id: int, project_path: Optional[str]) -> None:
        self._conn.execute(
            "UPDATE chats SET project_path = ?, updated_at = ? WHERE id = ?",
            (project_path, _now(), chat_id),
        )
        self._conn.commit()

    def rename_chat(self, chat_id: int, title: str) -> None:
        self._conn.execute(
            "UPDATE chats SET title = ?, updated_at = ? WHERE id = ?",
            (title, _now(), chat_id),
        )
        self._conn.commit()

    def add_message(self, chat_id: int, role: str, content: str) -> None:
        self._conn.execute(
            "INSERT INTO messages (chat_id, role, content, created_at) VALUES (?, ?, ?, ?)",
            (chat_id, role, content, _now()),
        )
        self._conn.commit()
        self.touch_chat(chat_id)

    def get_messages(self, chat_id: int) -> List[dict]:
        rows = self._conn.execute(
            "SELECT role, content, created_at FROM messages WHERE chat_id = ? ORDER BY id ASC",
            (chat_id,),
        ).fetchall()
        return [dict(row) for row in rows]

    def close(self) -> None:
        self._conn.close()
