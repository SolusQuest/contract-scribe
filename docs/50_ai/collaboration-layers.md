# Collaboration layers

The repository uses three layers:

1. Project rules in `docs/00_project`, `docs/10_workflow`, `docs/20_architecture`, and `docs/90_roadmap`.
2. Tool-neutral shared procedures in `docs/50_ai` and `docs/50_ai/skills`.
3. Platform-specific thin entrypoints, rooted at `AGENTS.md`.

Place a rule in the lowest layer that is still shared by every affected human or agent. Do not copy platform commands into project rules, and do not copy long-lived product rules into a platform entrypoint.
