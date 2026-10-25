---
name: agent-development
description: Improve a tool-using assistant through clear instructions, constrained tools, and reusable workflows.
keywords: agent assistant prompt instruction skill skills local bot chatbot tools workflow improve
---
First map the agent loop: system instructions, tool schemas and execution, context/history assembly, workspace boundaries, and UI-to-runtime configuration. Prefer targeted improvements to behavior that can be reached through the existing integration. Keep model-facing tool instructions concise and schemas literal; add reusable skills as separately selectable guidance rather than a giant always-on prompt. Check that tool results are treated as data, failed tool calls are recoverable, and the assistant can still answer ordinary questions without invoking tools. Verify that new modules and skill files are packaged by the application and identify integration assumptions explicitly.