"""Small, dependency-free loader for bundled task skills."""
from __future__ import annotations

import re
from pathlib import Path

SKILLS_DIR = Path(__file__).resolve().parent.parent / "skills"


def _load() -> list[dict[str, str]]:
    result = []
    if not SKILLS_DIR.is_dir():
        return result
    for path in sorted(SKILLS_DIR.glob("*/SKILL.md")):
        try:
            content = path.read_text(encoding="utf-8")
        except (OSError, UnicodeError):
            continue
        header = content.split("---", 2)[1] if content.startswith("---") and content.count("---") >= 2 else ""
        fields = {}
        for line in header.splitlines():
            match = re.match(r"([a-z_]+):\s*(.*)", line.strip())
            if match:
                fields[match.group(1)] = match.group(2).strip().strip("\"")
        name = fields.get("name", path.parent.name)
        description = fields.get("description", "")
        keywords = fields.get("keywords", "")
        body = content.split("---", 2)[-1].strip() if header else content.strip()
        result.append({"name": name, "description": description, "keywords": keywords, "priority": fields.get("priority", "0"), "body": body, "path": path})
    return result


def list_skills() -> list[dict[str, str]]:
    return [{"name": s["name"], "description": s["description"]} for s in _load()]


def read_skill(name: str) -> str:
    for skill in _load():
        if name in {skill["name"], skill["path"].parent.name}:
            return skill["body"]
    raise KeyError(f"Unknown skill: {name}")


def skill_context(prompt: str, max_skills: int = 2) -> str:
    """Select task guides by whole-word keyword matches, avoiding substring noise."""
    prompt_terms = set(re.findall(r"[a-z0-9+#.-]+", (prompt or "").lower()))
    selected = []
    for skill in _load():
        terms = set(re.findall(r"[a-z0-9+#.-]+", skill["keywords"].lower()))
        terms.update(re.findall(r"[a-z0-9+#.-]+", skill["name"].lower().replace("_", " ")))
        score = sum(1 for term in terms if len(term) > 2 and term in prompt_terms)
        if score:
            try:
                priority = int(skill["priority"])
            except ValueError:
                priority = 0
            selected.append((score, priority, skill))
    selected.sort(key=lambda row: (-row[0], -row[1], row[2]["name"]))
    if not selected:
        return ""
    parts = ["RELEVANT SKILLS (workflow guidance; use only when helpful):"]
    for _score, _priority, skill in selected[:max_skills]:
        parts.append(f"\n### {skill['name']}\n{skill['body']}")
    return "\n".join(parts)