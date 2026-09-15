"""
Audit-log + console logging setup. Extracted from assistant.py.
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



def setup_logging(audit_log_path: Path) -> logging.Logger:
    audit_log_path.parent.mkdir(parents=True, exist_ok=True)

    logger = logging.getLogger("local_agent")
    logger.setLevel(logging.INFO)
    logger.handlers.clear()

    console = logging.StreamHandler(sys.stderr)
    console.setFormatter(logging.Formatter("%(message)s"))
    logger.addHandler(console)

    file_handler = logging.FileHandler(audit_log_path, encoding="utf-8")
    file_handler.setFormatter(
        logging.Formatter(
            "%(asctime)s %(levelname)s %(message)s",
            datefmt="%Y-%m-%dT%H:%M:%S",
        )
    )
    logger.addHandler(file_handler)
    return logger


# ============================================================
# SQLite memory
# ============================================================

