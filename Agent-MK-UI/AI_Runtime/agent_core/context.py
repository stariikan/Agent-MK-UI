"""
Context-window bookkeeping: trimming, token estimation, and real per-model context length lookup via Ollama. Extracted from assistant.py.
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

from agent_core.model_client import ModelError, http_post_json

def trim_messages(
    messages: list[dict[str, str]],
    max_chars: int,
) -> list[dict[str, str]]:
    """Keep the system prompt and newest useful messages within the budget.

    Oversized individual messages are truncated rather than allowed to consume
    the entire budget. The most recent user message is always retained, but a
    compact tail is used when it is too large.
    """
    if not messages or max_chars <= 0:
        return []

    system = messages[0] if messages[0].get("role") == "system" else None
    rest = messages[1:] if system else messages[:]
    budget = max(1, max_chars - (len(system["content"]) if system else 0))

    # Always retain the current/newest message. Prefer the tail when the
    # request itself is larger than the remaining input budget.
    newest = rest[-1:] if rest else []
    older = rest[:-1] if rest else []
    newest_text = newest[0]["content"] if newest else ""
    if len(newest_text) > budget:
        tail_budget = max(256, budget)
        newest = [dict(newest[0], content=newest_text[-tail_budget:])]
        return ([system] if system else []) + newest

    kept = deque(newest)
    used = len(newest[0]["content"]) if newest else 0

    for msg in reversed(older):
        size = len(msg["content"])
        if used + size > budget:
            break
        kept.appendleft(msg)
        used += size

    result = list(kept)
    if system:
        result.insert(0, system)
    return result


def estimate_tokens(text: str) -> int:
    """
    Rough ~4-chars-per-token heuristic. Real tokenization varies by model
    and isn't worth a tokenizer dependency just for a "how full is the
    context window" indicator -- this is an approximation, not exact
    accounting, and is labeled as such in the UI.
    """
    return max(1, len(text) // 4)


def context_usage_from_messages(
    messages: list[dict[str, str]],
    max_chars: int,
) -> dict[str, Any]:
    """
    Reports how much of the character-budget context window (see
    Config.max_context_chars) the given message list occupies, and
    whether trim_messages() would actually have to drop anything to fit
    it -- i.e. the moment older history starts getting pushed out.
    """
    total_chars = sum(len(m["content"]) for m in messages)
    trimmed = trim_messages(messages, max_chars)

    return {
        "message_count": len(messages),
        "kept_message_count": len(trimmed),
        "used_chars": total_chars,
        "used_tokens_estimate": estimate_tokens(
            "".join(m["content"] for m in messages)
        ),
        "max_chars": max_chars,
        "was_trimmed": len(trimmed) < len(messages),
    }


_model_context_length_cache: dict[tuple[str, str], Optional[int]] = {}


def get_model_context_length(
    endpoint: str,
    model: str,
    timeout: int = 5,
) -> Optional[int]:
    """
    Best-effort lookup of a model's real context window (in tokens) via
    Ollama's /api/show endpoint, cached per model tag for the life of
    this process. Returns None if unreachable, the model isn't pulled
    yet, or the endpoint isn't an Ollama-shaped one -- callers should
    fall back to a sane default rather than guessing wrong.
    """
    cache_key = (endpoint, model)
    if cache_key in _model_context_length_cache:
        return _model_context_length_cache[cache_key]

    context_length: Optional[int] = None

    if "/api/chat" in endpoint:
        show_url = endpoint.replace("/api/chat", "/api/show")
        try:
            result = http_post_json(show_url, {"name": model}, timeout)
            model_info = result.get("model_info")
            if isinstance(model_info, dict):
                for key, value in model_info.items():
                    if key.endswith(".context_length") and isinstance(value, int):
                        context_length = value
                        break
        except ModelError:
            pass

    _model_context_length_cache[cache_key] = context_length
    return context_length


# Fraction of the model's real context window reserved for INPUT (history +
# system prompt + tool descriptions). The rest is left for the model's own
# reply. This is deliberately conservative because tokenization and provider
# overhead vary. The generation ceiling remains Config.max_tokens (8K by default).
INPUT_CONTEXT_FRACTION = 0.75


def effective_context_char_budget(
    fallback_max_chars: int,
    model_context_length: Optional[int],
) -> int:
    """
    The character budget trim_messages() actually uses to decide when to
    start dropping older messages.

    Previously this was ALWAYS the fixed Config.max_context_chars
    constant (120,000 chars, an arbitrary guess picked independently of
    any real model) -- completely disconnected from the real per-model
    context length looked up for the UI. That meant the number shown
    ("~12,400 / 32,768 tokens") and the number actually governing when
    history gets trimmed were two unrelated figures, which is exactly
    the confusion this was reported for ("it shows 32k tokens and I
    don't know from what point the chat starts rewriting context").

    Now the real model context length is the authoritative source
    whenever it's available (i.e. Ollama is reachable and reports one);
    the fixed constant is only a fallback for when that lookup fails.
    This guarantees the displayed max and the actual trim threshold are
    always the same number.
    """
    if model_context_length is None or model_context_length <= 0:
        return fallback_max_chars

    usable_tokens = int(model_context_length * INPUT_CONTEXT_FRACTION)
    return usable_tokens * 4  # same ~4-chars-per-token heuristic as estimate_tokens


# ============================================================
# Agent
# ============================================================



