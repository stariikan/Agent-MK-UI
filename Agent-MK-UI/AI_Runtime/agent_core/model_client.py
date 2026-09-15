"""
HTTP model adapters (Ollama / OpenAI-compatible). Extracted from assistant.py.
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

class ModelError(RuntimeError):
    pass


def http_post_json(
    url: str,
    payload: dict[str, Any],
    timeout: int,
) -> dict[str, Any]:
    raw = json.dumps(payload).encode("utf-8")
    request = urllib.request.Request(
        url,
        data=raw,
        headers={"Content-Type": "application/json"},
        method="POST",
    )

    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            body = response.read().decode("utf-8")
    except urllib.error.HTTPError as e:
        detail = e.read().decode("utf-8", errors="replace")
        raise ModelError(f"HTTP {e.code}: {detail}") from e
    except Exception as e:
        raise ModelError(str(e)) from e

    try:
        return json.loads(body)
    except json.JSONDecodeError as e:
        raise ModelError(
            f"Model server returned invalid JSON: {body[:1000]}"
        ) from e


class ModelClient:
    def __init__(self, config: Config, logger: logging.Logger):
        self.cfg = config
        self.logger = logger

    def chat(self, messages: list[dict[str, str]]) -> str:
        last_error: Optional[Exception] = None

        for attempt in range(1, self.cfg.retries + 1):
            try:
                if self.cfg.provider == "ollama":
                    return self._ollama(messages)
                elif self.cfg.provider in {"openai", "lmstudio", "llamacpp"}:
                    return self._openai_compatible(messages)
                else:
                    raise ModelError(
                        f"Unknown provider: {self.cfg.provider}"
                    )
            except Exception as e:
                last_error = e
                self.logger.warning(
                    f"Model request failed "
                    f"({attempt}/{self.cfg.retries}): {e}"
                )
                if attempt < self.cfg.retries:
                    time.sleep(min(2 ** (attempt - 1), 8))

        raise ModelError(f"All model attempts failed: {last_error}")

    def _ollama(self, messages: list[dict[str, str]]) -> str:
        payload = {
            "model": self.cfg.model,
            "messages": messages,
            "stream": False,
            "options": {
                "temperature": self.cfg.temperature,
                "num_predict": self.cfg.max_tokens,
            },
        }

        result = http_post_json(
            self.cfg.endpoint,
            payload,
            self.cfg.request_timeout_sec,
        )

        try:
            return str(result["message"]["content"])
        except KeyError as e:
            raise ModelError(
                f"Unexpected Ollama response: {result}"
            ) from e

    def _openai_compatible(
        self,
        messages: list[dict[str, str]],
    ) -> str:
        payload = {
            "model": self.cfg.model,
            "messages": messages,
            "temperature": self.cfg.temperature,
            "max_tokens": self.cfg.max_tokens,
        }

        result = http_post_json(
            self.cfg.endpoint,
            payload,
            self.cfg.request_timeout_sec,
        )

        try:
            return str(result["choices"][0]["message"]["content"])
        except (KeyError, IndexError) as e:
            raise ModelError(
                f"Unexpected OpenAI-compatible response: {result}"
            ) from e