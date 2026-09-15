"""
Safe workspace file I/O: reads/writes, path-traversal protection, atomic writes with backups. Extracted from assistant.py.
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

class WorkspaceError(RuntimeError):
    pass


class Workspace:
    def __init__(self, config: Config, logger: logging.Logger):
        self.cfg = config
        self.root = config.project_root.resolve()
        self.logger = logger
        self.root.mkdir(parents=True, exist_ok=True)

    def safe_path(self, relative_path: str) -> Path:
        if not relative_path or "\x00" in relative_path:
            raise WorkspaceError("Invalid path.")

        candidate = (self.root / relative_path).resolve()

        try:
            candidate.relative_to(self.root)
        except ValueError:
            raise WorkspaceError(
                f"Path escapes workspace: {relative_path!r}"
            )

        return candidate

    def is_ignored(self, path: Path) -> bool:
        try:
            rel = path.relative_to(self.root)
        except ValueError:
            return True

        if any(part in self.cfg.ignored_dirs for part in rel.parts):
            return True

        if path.suffix.lower() in self.cfg.ignored_extensions:
            return True

        return False

    def read_text(
        self,
        relative_path: str,
        *,
        start_line: int | None = None,
        end_line: int | None = None,
        max_chars: int | None = None,
    ) -> str:
        path = self.safe_path(relative_path)

        if self.is_ignored(path):
            raise WorkspaceError(f"Refusing ignored file: {relative_path}")
        if not path.exists():
            raise WorkspaceError(f"File does not exist: {relative_path}")
        if not path.is_file():
            raise WorkspaceError(f"Not a file: {relative_path}")

        size = path.stat().st_size
        if size > self.cfg.max_read_bytes_per_file and start_line is None:
            raise WorkspaceError(
                f"File too large to read whole ({size} bytes): {relative_path}. "
                "Use start_line/end_line or max_chars."
            )

        text = path.read_text(encoding="utf-8", errors="replace")
        if start_line is not None or end_line is not None:
            first = max(1, int(start_line or 1))
            last = max(first, int(end_line or first + 400))
            lines = text.splitlines()
            text = "\n".join(lines[first - 1:last])
        if max_chars is not None and len(text) > max_chars:
            text = text[:max_chars] + "\n...[truncated]"
        return text

    def project_summary(self, *, max_files: int = 120) -> dict[str, Any]:
        """Return compact project metadata without embedding full source."""
        files = self.list_files(limit=max_files)
        manifest_names = {
            "*.sln", "*.csproj", "package.json", "pyproject.toml",
            "requirements.txt", "Cargo.toml", "go.mod", "CMakeLists.txt",
            "*.uproject", "ProjectSettings/ProjectVersion.txt",
        }
        manifests = [f for f in files if any(
            (f.lower().endswith(x[1:].lower()) if x.startswith("*.") else f == x)
            for x in manifest_names
        )]
        entry_candidates = [f for f in files if Path(f).name.lower() in {
            "program.cs", "main.py", "main.cs", "app.xaml.cs", "index.ts",
            "index.tsx", "main.ts", "main.tsx", "app.tsx", "startup.cs",
        }]
        try:
            recent = sorted(
                ((p.stat().st_mtime, p.relative_to(self.root).as_posix())
                 for p in self.root.rglob("*") if p.is_file() and not self.is_ignored(p)),
                reverse=True,
            )[:20]
            recent_files = [rel for _, rel in recent]
        except OSError:
            recent_files = []
        return {
            "root": str(self.root),
            "file_count_seen": len(files),
            "truncated_listing": len(files) >= max_files,
            "manifests": sorted(manifests)[:30],
            "entry_candidates": sorted(entry_candidates)[:30],
            "recent_files": recent_files,
            "extensions": sorted({Path(f).suffix.lower() for f in files if Path(f).suffix})[:50],
        }

    def backup_file(self, path: Path) -> Optional[Path]:
        if not self.cfg.backups or not path.exists():
            return None

        backup_dir = self.root / ".agent_backups"
        backup_dir.mkdir(parents=True, exist_ok=True)

        stamp = dt.datetime.now().strftime("%Y%m%d_%H%M%S_%f")
        rel = path.relative_to(self.root)
        safe_name = "__".join(rel.parts)
        backup_path = backup_dir / f"{stamp}__{safe_name}"

        shutil.copy2(path, backup_path)
        return backup_path

    def atomic_write(self, relative_path: str, content: str) -> dict[str, Any]:
        if not isinstance(content, str):
            raise WorkspaceError("File content must be text.")

        payload = content.encode("utf-8")
        if len(payload) > self.cfg.max_write_bytes_per_file:
            raise WorkspaceError(
                f"Write exceeds max size: {len(payload)} bytes"
            )

        path = self.safe_path(relative_path)

        if self.is_ignored(path):
            raise WorkspaceError(f"Refusing ignored file: {relative_path}")

        path.parent.mkdir(parents=True, exist_ok=True)

        old_content = ""
        if path.exists() and path.is_file():
            try:
                old_content = path.read_text(
                    encoding="utf-8", errors="replace"
                )
            except Exception:
                old_content = ""

        diff = "\n".join(
            difflib.unified_diff(
                old_content.splitlines(),
                content.splitlines(),
                fromfile=f"a/{relative_path}",
                tofile=f"b/{relative_path}",
                lineterm="",
            )
        )

        if self.cfg.dry_run:
            return {
                "path": relative_path,
                "written": False,
                "dry_run": True,
                "diff": diff[:30_000],
            }

        backup = self.backup_file(path)

        fd, temp_name = tempfile.mkstemp(
            prefix=".agent_tmp_",
            dir=str(path.parent),
        )
        temp_path = Path(temp_name)

        try:
            with os.fdopen(fd, "w", encoding="utf-8", newline="") as f:
                f.write(content)
                f.flush()
                os.fsync(f.fileno())

            os.replace(temp_path, path)
        finally:
            if temp_path.exists():
                temp_path.unlink(missing_ok=True)

        return {
            "path": relative_path,
            "written": True,
            "bytes": len(payload),
            "backup": str(backup) if backup else None,
            "diff": diff[:30_000],
        }

    def list_files(self, limit: int = 500) -> list[str]:
        files: list[str] = []

        for path in self.root.rglob("*"):
            if len(files) >= limit:
                break

            if not path.is_file() or self.is_ignored(path):
                continue

            try:
                rel = path.relative_to(self.root).as_posix()
            except ValueError:
                continue

            files.append(rel)

        return sorted(files)

    def snapshot(
        self,
        *,
        max_files: int = 80,
        max_chars_per_file: int = 8_000,
    ) -> str:
        chunks = []

        for rel in self.list_files(limit=max_files):
            try:
                text = self.read_text(rel)
            except Exception:
                continue

            if len(text) > max_chars_per_file:
                text = text[:max_chars_per_file] + "\n...[truncated]"

            chunks.append(f"\n### FILE: {rel}\n{text}")

        return "\n".join(chunks)

    def search(
        self,
        query: str,
        *,
        max_results: int = 20,
    ) -> list[dict[str, Any]]:
        terms = [
            t.lower()
            for t in re.findall(r"[A-Za-z0-9_.$-]+", query)
            if len(t) >= 2
        ]

        if not terms:
            return []

        scored: list[tuple[int, str, str]] = []

        for rel in self.list_files(limit=1000):
            try:
                text = self.read_text(rel)
            except Exception:
                continue

            lower = text.lower()
            score = sum(lower.count(t) for t in terms)

            if score <= 0:
                continue

            first_idx = min(
                [lower.find(t) for t in terms if lower.find(t) >= 0]
                or [0]
            )
            start = max(0, first_idx - 400)
            end = min(len(text), first_idx + 1600)

            scored.append((score, rel, text[start:end]))

        scored.sort(reverse=True, key=lambda x: x[0])

        return [
            {"score": score, "path": rel, "snippet": snippet}
            for score, rel, snippet in scored[:max_results]
        ]


# ============================================================
# HTTP model adapters
# ============================================================



