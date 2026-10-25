---
name: agent-development
description: Diagnose and improve a local assistant's prompts, tools, context flow, and packaging.
keywords: agent assistant prompt instruction skill skills bot chatbot tools workflow improve runtime
priority: 20
---
Start from the reported behavior and trace the path that produced it: prompt assembly, skill selection, tool schema, parser, execution, UI, and packaging. Fix the responsible layer; do not paper over a parser or integration defect with more prompt text. Keep guidance short, resolve contradictions, and include concrete behavior rules for common failures. Check both the intended model response and the runtime's handling of it. Confirm added skills/modules are packaged and report which behavior was verified.