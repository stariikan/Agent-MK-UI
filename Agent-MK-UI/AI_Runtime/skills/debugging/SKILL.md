---
name: debugging
description: Find a defect from the observed behavior, logs, and source.
keywords: debug debugging bug error exception crash failing broken traceback logs reproduce
priority: 15
---
State the symptom and expected behavior in concrete terms. Use the supplied code, logs, and sample input/output first. Trace the failing path to a cause before proposing a fix; don't replace diagnosis with a generic checklist or speculative rewrite. If evidence is missing, ask for the exact error, source section, or sample that blocks diagnosis. Add requested instrumentation at useful checkpoints, preserve the current output channel, and distinguish a suggested fix from one actually run.