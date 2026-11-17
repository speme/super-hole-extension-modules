# SuperHole SolidWorks Extension Modules

This public repository contains only independently authored extension modules for the SuperHole SolidWorks add-in. It does **not** contain source files, UI assets, example models, caches, or build outputs from `RoboMechatronics/MyCustomTaskpane`.

The modules provide coordinate-based hole/thread creation, table-driven metric thread lookup hooks, same-part material-depth resolution, and the named-pipe MCP host/server. They are designed to integrate with a SolidWorks add-in that supplies the task-pane UI and calls the host lifecycle methods. This repository is not a complete standalone SolidWorks add-in.

The developer's local add-in has a 69-row M-thread tap-drill table extracted from the installed SOLIDWORKS Toolbox. That vendor database data is deliberately not included here. The public source therefore requires the consuming add-in to supply licensed metric rows or to pass verified `tap_drill_diameter_mm` and `major_diameter_mm` values for specifications not resolved by its own table.

## Components

- `src/AssemblyHoleAlignment.cs`: coordinate and selection driven hole, thread, recess, and slot operations.
- `src/MaterialDepthResolver.cs`: same-part multibody depth resolution for plate stacks and hollow sections.
- `src/McpPipeHost.cs`: current-user named pipe host and SolidWorks UI-thread dispatcher.
- `mcp-server/`: standalone stdio MCP client for the local pipe protocol.

## Build

Requires Windows, .NET Framework 4.8 targeting pack, and SolidWorks interop assemblies installed at the standard `ProgramW6432` path. Build `SuperHole.ExtensionModules.csproj` and `mcp-server/McpServer.csproj` for x64.

The consuming add-in must attach the active SolidWorks instance with `AddinRuntime.Attach(app)`, start `McpPipeHost.Start(taskPaneControl)` on its UI thread, and call `McpPipeHost.Stop()` plus `AddinRuntime.Detach()` when disconnecting.

## Upstream integration

The original task-pane add-in is available at [RoboMechatronics/MyCustomTaskpane](https://github.com/RoboMechatronics/MyCustomTaskpane). That repository does not declare a license. No files from that repository are included here, and this repository does not grant rights to its code or assets. Obtain the original source and its redistribution permission separately before combining or distributing a complete add-in.

## License

The original modules in this repository are distributed under GNU GPL version 3. See [`COPYING`](COPYING). The license applies only to the original files included here and does not relicense upstream software, SolidWorks SDK components, or Toolbox data.
