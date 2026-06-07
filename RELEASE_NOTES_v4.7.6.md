# RhinoAIBridge v4.7.6

This release is packaged for one-click Windows installation. Users do not need
the .NET SDK: `INSTALL.bat` deploys the pre-built Rhino 8 plugin from
`dist/plugin`, installs the Python MCP dependencies with `uv`, and configures
detected AI clients.

## Highlights

- Adds Safe, Standard, and Developer access modes. Run `AIBridge` in Rhino to
  choose or change the active mode.
- Adds direct MCP image returns for viewport capture, thumbnails, and PDF
  previews, plus JSON variants for metadata.
- Adds McNeel-compatible aliases: `get_viewport_image`, `list_objects`,
  `set_selection`, and `run_python`.
- Adds inspection captures, surface primitives, SVG section/silhouette feedback,
  structured retry hints, and automatic checkpoints before risky operations.
- Adds local token authentication for the loopback bridge.
- Removes the Rhino plugin-loader dependency on `System.Threading.Channels`.
- Ships the full .NET 8 runtime payload required by Rhino.
- Configures Claude Desktop, OpenAI Codex, and Gemini Antigravity.
- Hardens checkpoint and export paths, connection limits, idle timeouts, and
  installer failure handling.
- Fixes transform GUID tracking, layer full-path collisions, planar curve
  offsets, design-memory writes, undo reporting, material downloads, and
  several smaller correctness issues.

## Install

1. Close Rhino completely.
2. Download and extract the repository ZIP.
3. Double-click `INSTALL.bat`.
4. Open Rhino 8 and install `RhinoAIBridge.rhp` through `PlugInManager` if this
   is the first installation.
5. Run `AIBridge`, select an access mode, then restart your AI client.

## Verification

- Rhino plugin: `dotnet build --configuration Release` passes with 0 errors.
- Python MCP server: `uv run python -m py_compile src\rhino_architect\server.py`
  passes.
- Python dependencies: `uv lock` refreshed for NumPy and OpenCV.
- Installer payload: `dist/plugin` refreshed from the successful Release build.
- Live bridge smoke test still recommended after installing this package in Rhino
  8, especially for the new visual and surface tools.
