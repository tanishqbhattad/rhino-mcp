# rhino-mcp evals

Scored modeling tasks that measure how well an AI agent drives Rhino through this MCP. Scoring is programmatic (object counts, detected levels, GFA, clash checks) against the **live scene** - no screenshots, no vibes.

## Why

Every SOTA model release, every tool-surface change, and every skill edit changes agent behavior. This harness turns "feels better" into numbers you can compare across runs (`results.jsonl`, tagged with `--label`).

## Usage

Rhino 8 + AIBridge must be running.

```
uv --directory server run python ../evals/run_evals.py list
uv --directory server run python ../evals/run_evals.py prompt massing_floors   # paste into your MCP client
# ... let the agent build it ...
uv --directory server run python ../evals/run_evals.py check massing_floors --label claude-fable-5
uv --directory server run python ../evals/run_evals.py reset                   # clear scene for the next task
```

## Task ordering

Tasks 01→04→08 build on the same scene (massing → core → schedule → drawings): run them as a sequence without reset. Tasks 03, 05, 06, 07, 09 each start from an empty scene (`reset` first).

## Adding tasks

Drop a JSON file in `tasks/`: `{id, title, prompt, assertions:[...]}`. Assertion types live in `run_evals.py` (`ASSERTIONS` dict) - add new ones there as needed. Keep assertion bounds tolerant to legitimate design variation (ranges, not exact counts, unless the task pins the number).
