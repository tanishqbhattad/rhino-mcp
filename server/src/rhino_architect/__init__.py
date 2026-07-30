"""Rhino AI Bridge - MCP server for AI-assisted architectural modelling in Rhino 8.

The version is read from installed package metadata (pyproject.toml is the single
source of truth) so it can never drift from the released package again.
"""

import importlib.metadata

try:
    __version__ = importlib.metadata.version("rhino-architect")
except importlib.metadata.PackageNotFoundError:  # running from a raw checkout
    __version__ = "0.0.0-dev"

__all__ = ["__version__"]
