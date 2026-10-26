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
    mode: str
    agentic_setup_confirmed: bool
    agentic_task_goal: Optional[str]
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
                mode TEXT NOT NULL DEFAULT 'chat',
                agentic_setup_confirmed INTEGER NOT NULL DEFAULT 0,
                agentic_task_goal TEXT,
                active_agentic_task_id TEXT,
                active_agentic_pid INTEGER,
                active_agentic_started_at TEXT,
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

            CREATE TABLE IF NOT EXISTS agentic_task_state (
                chat_id INTEGER PRIMARY KEY REFERENCES chats(id) ON DELETE CASCADE,
                checkpoint_id TEXT NOT NULL
            );
            """
        )
        columns = {row["name"] for row in self._conn.execute("PRAGMA table_info(chats)")}
        if "mode" not in columns:
            self._conn.execute("ALTER TABLE chats ADD COLUMN mode TEXT NOT NULL DEFAULT 'chat'")
            # Older versions allowed mode to change per message. Preserve the likely
            # intent for conversations that already contain agentic task runs.
            self._conn.execute(
                "UPDATE chats SET mode = 'agentic' WHERE id IN ("
                "SELECT DISTINCT chat_id FROM messages WHERE content LIKE '[Agentic task]%')"
            )
        if "agentic_setup_confirmed" not in columns:
            self._conn.execute(
                "ALTER TABLE chats ADD COLUMN agentic_setup_confirmed INTEGER NOT NULL DEFAULT 0"
            )
            self._conn.execute(
                "UPDATE chats SET agentic_setup_confirmed = 1 WHERE id IN ("
                "SELECT DISTINCT chat_id FROM messages WHERE content LIKE '[Agentic task]%')"
            )
        if "agentic_task_goal" not in columns:
            self._conn.execute("ALTER TABLE chats ADD COLUMN agentic_task_goal TEXT")
            # Keep the first actual agentic objective attached to the chat so later
            # follow-ups and timeout recovery do not depend on a small model recalling it.
            rows = self._conn.execute(
                "SELECT chat_id, content FROM messages "
                "WHERE content LIKE '[Agentic task]%' ORDER BY id ASC"
            ).fetchall()
            seen: set[int] = set()
            for message in rows:
                chat_id = int(message["chat_id"])
                if chat_id in seen:
                    continue
                content = str(message["content"])
                original_goal = content.split("\n", 1)[1].strip() if "\n" in content else ""
                if original_goal:
                    self._conn.execute(
                        "UPDATE chats SET mode = 'agentic', agentic_setup_confirmed = 1, agentic_task_goal = ? WHERE id = ?",
                        (original_goal, chat_id),
                    )
                    seen.add(chat_id)
        # SQLite serializes this lease claim across separate app processes.
        for name, declaration in (
            ("active_agentic_task_id", "TEXT"),
            ("active_agentic_pid", "INTEGER"),
            ("active_agentic_started_at", "TEXT"),
        ):
            if name not in columns:
                self._conn.execute(f"ALTER TABLE chats ADD COLUMN {name} {declaration}")
        # Older builds stored a display-only tag in visible chat messages. Strip
        # that tag in place so existing history remains readable after upgrade.
        legacy_rows = self._conn.execute(
            "SELECT id, content FROM messages WHERE content LIKE '[Agentic task]%'"
        ).fetchall()
        for legacy in legacy_rows:
            content = str(legacy["content"])
            if content.startswith("[Agentic task]\n"):
                self._conn.execute(
                    "UPDATE messages SET content = ? WHERE id = ?",
                    (content.split("\n", 1)[1], legacy["id"]),
                )
        self._conn.commit()

    @staticmethod
    def _pid_is_running(pid: Optional[int]) -> bool:
        if not pid or pid <= 0:
            return False
        try:
            os.kill(pid, 0)
            return True
        except PermissionError:
            return True
        except (ProcessLookupError, OSError):
            return False

    def try_start_agentic_task(self, chat_id: int, task_id: str, pid: int) -> bool:
        """Atomically claim this chat's Agentic run across app processes."""
        self._conn.execute("BEGIN IMMEDIATE")
        try:
            row = self._conn.execute(
                "SELECT active_agentic_task_id, active_agentic_pid FROM chats WHERE id = ?",
                (chat_id,),
            ).fetchone()
            if row is None:
                raise ValueError(f"No such chat: {chat_id}")
            if row["active_agentic_task_id"] and self._pid_is_running(row["active_agentic_pid"]):
                self._conn.rollback()
                return False
            self._conn.execute(
                "UPDATE chats SET active_agentic_task_id = ?, active_agentic_pid = ?, "
                "active_agentic_started_at = ?, updated_at = ? WHERE id = ?",
                (task_id, pid, _now(), _now(), chat_id),
            )
            self._conn.commit()
            return True
        except Exception:
            if self._conn.in_transaction:
                self._conn.rollback()
            raise

    def finish_agentic_task(self, chat_id: int, task_id: str) -> None:
        """Release a lease only when it is still owned by this task."""
        self._conn.execute(
            "UPDATE chats SET active_agentic_task_id = NULL, active_agentic_pid = NULL, "
            "active_agentic_started_at = NULL, updated_at = ? "
            "WHERE id = ? AND active_agentic_task_id = ?",
            (_now(), chat_id, task_id),
        )
        self._conn.commit()

    @staticmethod
    def _chat_from_row(row: sqlite3.Row) -> Chat:
        # The chats table also stores process-lock metadata; keep it internal.
        values = {key: row[key] for key in Chat.__dataclass_fields__ if key in row.keys()}
        values["agentic_setup_confirmed"] = bool(values.get("agentic_setup_confirmed", False))
        return Chat(**values)

    def count_chats(self) -> int:
        row = self._conn.execute("SELECT COUNT(*) AS n FROM chats").fetchone()
        return int(row["n"])

    def list_chats(self) -> List[Chat]:
        rows = self._conn.execute(
            "SELECT * FROM chats ORDER BY updated_at DESC"
        ).fetchall()
        return [self._chat_from_row(row) for row in rows]

    def get_chat(self, chat_id: int) -> Optional[Chat]:
        row = self._conn.execute(
            "SELECT * FROM chats WHERE id = ?", (chat_id,)
        ).fetchone()
        return self._chat_from_row(row) if row else None

    def create_chat(
        self, title: str, project_path: Optional[str], model: str, mode: str = "chat"
    ) -> Chat:
        mode = mode.strip().lower()
        if mode not in {"chat", "agentic"}:
            raise ValueError("mode must be 'chat' or 'agentic'")
        if self.count_chats() >= MAX_CHATS:
            raise ChatLimitError(
                f"Maximum of {MAX_CHATS} chats reached. Delete an existing chat first."
            )

        now = _now()
        cur = self._conn.execute(
            "INSERT INTO chats (title, project_path, model, mode, created_at, updated_at) "
            "VALUES (?, ?, ?, ?, ?, ?)",
            (title, project_path, model, mode, now, now),
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

    def get_agentic_checkpoint_id(self, chat_id: int) -> Optional[str]:
        row = self._conn.execute(
            "SELECT checkpoint_id FROM agentic_task_state WHERE chat_id = ?", (chat_id,)
        ).fetchone()
        return str(row["checkpoint_id"]) if row else None

    def set_agentic_checkpoint_id(self, chat_id: int, checkpoint_id: str) -> None:
        self._conn.execute(
            "INSERT INTO agentic_task_state (chat_id, checkpoint_id) VALUES (?, ?) "
            "ON CONFLICT(chat_id) DO UPDATE SET checkpoint_id = excluded.checkpoint_id",
            (chat_id, checkpoint_id),
        )
        self._conn.commit()
    def set_agentic_task_goal(self, chat_id: int, goal: str) -> None:
        self._conn.execute(
            "UPDATE chats SET agentic_task_goal = ?, updated_at = ? WHERE id = ?",
            (goal, _now(), chat_id),
        )
        self._conn.commit()

    def set_agentic_setup_confirmed(self, chat_id: int, confirmed: bool = True) -> None:
        self._conn.execute(
            "UPDATE chats SET agentic_setup_confirmed = ?, updated_at = ? WHERE id = ?",
            (1 if confirmed else 0, _now(), chat_id),
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








