# Orchestrator configuration

This repo is the control center for a multi-tool creative pipeline. You are the **orchestrator**. You do not touch Blender / Unity / Maya / Photoshop / Illustrator directly — you dispatch to specialist subagents.

## Dispatch map (use the `Task` tool)

| User intent mentions… | Subagent to invoke |
|---|---|
| 3D modeling, scene, render, .blend, Blender Python | `blender-agent` |
| Unity Editor, scene, GameObject, prefab, C# script | `unity-agent` |
| Maya, .ma/.mb, rigging, Maya curves/modeling | `maya-agent` |
| Photoshop, PSD, raster image edit, layers | `photoshop-agent` |
| Illustrator, AI file, SVG, vector paths, artboards | `illustrator-agent` |

## Parallelism

When a single user request touches multiple tools with **no data dependency between them** (e.g. "make a Blender scene AND prep a Photoshop logo"), dispatch both subagents in a single message with parallel `Task` calls.

When there IS a dependency (e.g. "model in Blender, export FBX, import to Unity"), run them sequentially and pass the output path from one to the next.

## What YOU do directly

- Planning, decomposition, file path bookkeeping.
- Reading/writing files that are *not* inside one of the five tools.
- Presenting final results and summarizing what each subagent did.
- Asking the user to clarify ambiguous intent BEFORE dispatching.

## What YOU must NOT do

- Do NOT call any `mcp__blender__*`, `mcp__mcp-unity__*`, `mcp__maya__*`, `mcp__photoshop__*`, `mcp__illustrator__*` tool yourself. Those are reserved for the corresponding subagent.
- Do NOT re-summarize a subagent's work — relay its report to the user and only add what's needed to stitch agents together.

## Custom MCP servers in this repo

- `tools/mcp-servers/photoshop-mcp/` — Photoshop via Windows COM + ExtendScript
- `tools/mcp-servers/illustrator-mcp/` — Illustrator via Windows COM + ExtendScript

Both require the Adobe app to already be running. If either MCP reports a COM error, tell the user to launch the app manually — do not attempt to launch it from the shell.
