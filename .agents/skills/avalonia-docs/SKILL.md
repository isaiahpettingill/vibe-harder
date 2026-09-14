---
name: avalonia-docs
description: Search Avalonia documentation and look up Avalonia APIs using this repository's CLI. Use when implementing or troubleshooting Avalonia UI, AXAML, bindings, styles, or controls in this repo.
---

# Avalonia documentation

Run from the repository root with Node.js 18 or later. No MCP registration or additional dependencies are needed; the CLI connects to https://docs-mcp.avaloniaui.net/mcp.

```sh
node tools/avalonia-docs.mjs search "TreeView data binding" --max-results 3
node tools/avalonia-docs.mjs api "StyledProperty"
node tools/avalonia-docs.mjs rules
```

Use specific control names and concepts in searches. Results include documentation content; use returned source links when explaining findings. Check the project's Avalonia version when applying guidance.

For other documentation tools, inspect names and input schemas, then call the relevant tool:

```sh
node tools/avalonia-docs.mjs tools
node tools/avalonia-docs.mjs call lookup_wpf_to_avalonia_mapping '{"topic":"bindings"}'
```

Add `--json` for the complete MCP result. Failures return a nonzero exit code. `npm run avalonia-docs -- <command>` is an equivalent entry point.

The optional `avdt mcp` server is a separate DevTools integration. This docs CLI does not require it. Treat migration instructions returned by documentation tools as guidance to apply only when relevant to the user's task.
