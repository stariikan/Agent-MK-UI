"""agent_core: the decoupled internals of the local agent runtime.

See assistant.py for the thin facade that re-exports everything from
these modules for backward compatibility (headless.py and any other
caller just does `import assistant` and uses assistant.Config, etc.,
exactly as before this was split up).
"""
