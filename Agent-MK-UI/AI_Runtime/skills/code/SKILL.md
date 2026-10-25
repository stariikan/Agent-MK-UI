---
name: code
description: Implement focused code changes while following existing project conventions.
keywords: code coding script standalone pasted snippet implement feature refactor add modify function bug fix build
---
For a standalone script or code pasted into chat, treat that content as sufficient context for analysis or edits; do not require an attached project unless the request depends on other files. For an attached project, inspect its instructions and nearby implementation first. Trace the relevant code path, then make the smallest complete change that fits available conventions. Preserve unrelated edits and public APIs. Prefer patching focused sections over replacing whole files. Check callers and error paths. After edits, re-read changed files and run an appropriate focused validation command when available. Report the exact files changed and what validation actually ran.