# Vega complexity audit (Roslyn syntax metrics)

Source files: **500** · physical lines: **66 065** · callable bodies: **4 727** · syntax errors: **0**

Decision score = 1 + if/loops/catch/case/conditional/switch arm/logical and-or/coalesce/pattern and-or. Max depth counts nested control constructs. Nested callables are measured separately. Span lines include formatting/comments. These are reproducible screening metrics, not a semantic proof of defect risk.

## By project

| Project | Bodies | Max decision score | Score >= 15 | Score >= 30 | Span >= 100 |
|---|---:|---:|---:|---:|---:|
| Vega.Terraria | 1062 | 71 | 49 | 20 | 10 |
| Vega.Core | 1243 | 61 | 41 | 4 | 2 |
| Vega.Modules.Administration | 161 | 35 | 18 | 1 | 0 |
| Vega.Sandboxes | 255 | 43 | 16 | 1 | 1 |
| Vega.Operations | 119 | 62 | 12 | 3 | 2 |
| Vega.Regions | 89 | 37 | 11 | 2 | 1 |
| Vega.Runtime | 104 | 95 | 9 | 1 | 2 |
| Vega.Persistence | 300 | 20 | 6 | 0 | 1 |
| Vega.Cluster | 122 | 37 | 4 | 2 | 0 |
| Vega.Contracts | 229 | 19 | 4 | 0 | 0 |
| Vega.Modules.Scenes | 107 | 52 | 3 | 1 | 0 |
| Vega.TerminalUI | 188 | 27 | 3 | 0 | 1 |
| TerraRuntime.Schematics | 74 | 36 | 2 | 1 | 2 |
| Vega.Modules.Navigation | 75 | 53 | 2 | 2 | 0 |
| Vega.Modules.Restrictions | 20 | 24 | 2 | 0 | 0 |
| Vega.PluginSdk | 467 | 17 | 2 | 0 | 0 |
| Vega.Modules.Chests | 20 | 23 | 1 | 0 | 0 |
| Vega.Modules.Wipe | 8 | 15 | 1 | 0 | 0 |
| Vega.Backups | 22 | 13 | 0 | 0 | 1 |
| Vega.Modules.AutoWorldCleaner | 24 | 14 | 0 | 0 | 0 |
| Vega.Modules.Mentions | 5 | 8 | 0 | 0 | 0 |
| Vega.Modules.Pvp | 33 | 13 | 0 | 0 | 0 |

## Direct project dependencies (source ProjectReference graph)

| Project | References | Direct dependents |
|---|---:|---:|
| Vega.Runtime | 19 | 0 |
| Vega.Core | 2 | 1 |
| Vega.Modules.Scenes | 2 | 1 |
| Vega.Sandboxes | 2 | 1 |
| Vega.Terraria | 2 | 1 |
| Vega.Backups | 1 | 1 |
| Vega.Cluster | 1 | 1 |
| Vega.Modules.Administration | 1 | 1 |
| Vega.Modules.AutoWorldCleaner | 1 | 1 |
| Vega.Modules.Chests | 1 | 1 |
| Vega.Modules.Mentions | 1 | 1 |
| Vega.Modules.Navigation | 1 | 1 |
| Vega.Modules.Pvp | 1 | 1 |
| Vega.Modules.Restrictions | 1 | 1 |
| Vega.Modules.Wipe | 1 | 1 |
| Vega.Operations | 1 | 1 |
| Vega.Persistence | 1 | 1 |
| Vega.PluginSdk | 1 | 3 |
| Vega.Regions | 1 | 1 |
| Vega.TerminalUI | 1 | 1 |
| TerraRuntime.Schematics | 0 | 2 |
| Vega.Contracts | 0 | 19 |

No cycles in the source ProjectReference graph.

## Highest decision scores

| Method | File:line | Decision score | Depth | Lines |
|---|---|---:|---:|---:|
| `Start` | `src/Vega.Runtime/Program.cs:117` | 95 | 3 | 806 |
| `TerrariaWorldEnvironmentService.Patch` | `src/Vega.Terraria/Worlds/TerrariaWorldEnvironmentService.cs:43` | 71 | 5 | 124 |
| `TerrariaWorldNativeActions.Grow` | `src/Vega.Terraria/Worlds/TerrariaWorldNativeActions.cs:37` | 67 | 3 | 80 |
| `LoopbackRestService.ServeAsync` | `src/Vega.Operations/Actions/LoopbackRestService.cs:72` | 62 | 5 | 137 |
| `BanCommand.ExecuteBanToolsAsync` | `src/Vega.Core/Commands/Administration/BanCommand.cs:145` | 61 | 5 | 75 |
| `PacketPipelineClassification.Get` | `src/Vega.Terraria/Networking/PacketPipelineClassification.cs:37` | 60 | 2 | 90 |
| `TerrariaSchematicObjects.Prepare` | `src/Vega.Terraria/Worlds/TerrariaSchematicObjects.cs:24` | 59 | 4 | 104 |
| `NavigationModule.WarpAsync` | `src/Vega.Modules.Navigation/NavigationModule.cs:165` | 53 | 4 | 81 |
| `FakeProviderCommand.Execute` | `src/Vega.Modules.Scenes/FakeProviderCommand.cs:95` | 52 | 4 | 52 |
| `TerrariaPlayerInventoryService.TransferVirtualChestCore` | `src/Vega.Terraria/Players/TerrariaPlayerInventoryService.VirtualChests.cs:13` | 49 | 2 | 67 |
| `TerrariaPlayerBoundary.lambda` | `src/Vega.Terraria/Players/TerrariaPlayerBoundary.Administration.cs:51` | 48 | 3 | 55 |
| `MuteCommand.ExecuteAsync` | `src/Vega.Core/Commands/Administration/MuteCommand.cs:29` | 46 | 4 | 81 |
| `NativeGameplayEventHooks.SamplePlayerStates` | `src/Vega.Terraria/Hooking/NativeGameplayEventHooks.PlayerState.cs:21` | 45 | 5 | 57 |
| `SandboxControlService.CreateAsync` | `src/Vega.Sandboxes/SandboxControlService.cs:73` | 43 | 4 | 127 |
| `NativeSceneNetworkHooks.OnSendData` | `src/Vega.Terraria/Hooking/NativeSceneNetworkHooks.cs:82` | 40 | 5 | 83 |
| `HomeCommand.ExecuteAsync` | `src/Vega.Modules.Navigation/HomeCommand.cs:25` | 40 | 3 | 61 |
| `RegionObservations.ObserveTick` | `src/Vega.Regions/RegionObservations.cs:57` | 37 | 4 | 74 |
| `TerrariaSchematicPlacement.Place` | `src/Vega.Terraria/Worlds/TerrariaSchematicPlacement.cs:16` | 37 | 4 | 74 |
| `AuthenticatedClusterTransport.HandleAsync` | `src/Vega.Cluster/Transport/AuthenticatedClusterTransport.cs:204` | 37 | 3 | 64 |
| `TerrariaSceneRenderSink.TryGetSignRecipient` | `src/Vega.Terraria/Scenes/TerrariaSceneRenderSink.Signs.cs:41` | 37 | 5 | 31 |

## Deepest control nesting

| Method | File:line | Decision score | Depth | Lines |
|---|---|---:|---:|---:|
| `SandboxBackendLaunchOptions.Parse` | `src/Vega.Sandboxes/SandboxBackendRuntime.cs:25` | 24 | 7 | 74 |
| `SandboxSscSessionCoordinator.RunAsync` | `src/Vega.Sandboxes/SandboxSscSessionCoordinator.cs:119` | 28 | 6 | 90 |
| `SandboxControlService.MonitorAsync` | `src/Vega.Sandboxes/SandboxControlService.cs:575` | 21 | 6 | 90 |
| `NativeOutboundQueue.TryEnqueue` | `src/Vega.Terraria/Networking/NativeOutboundQueue.cs:136` | 16 | 6 | 53 |
| `TerrariaWorldEnvironmentService.Patch` | `src/Vega.Terraria/Worlds/TerrariaWorldEnvironmentService.cs:43` | 71 | 5 | 124 |
| `LoopbackRestService.ServeAsync` | `src/Vega.Operations/Actions/LoopbackRestService.cs:72` | 62 | 5 | 137 |
| `BanCommand.ExecuteBanToolsAsync` | `src/Vega.Core/Commands/Administration/BanCommand.cs:145` | 61 | 5 | 75 |
| `NativeGameplayEventHooks.SamplePlayerStates` | `src/Vega.Terraria/Hooking/NativeGameplayEventHooks.PlayerState.cs:21` | 45 | 5 | 57 |
| `NativeSceneNetworkHooks.OnSendData` | `src/Vega.Terraria/Hooking/NativeSceneNetworkHooks.cs:82` | 40 | 5 | 83 |
| `TerrariaSceneRenderSink.TryGetSignRecipient` | `src/Vega.Terraria/Scenes/TerrariaSceneRenderSink.Signs.cs:41` | 37 | 5 | 31 |
| `NativeOutboundQueue.WriteLoopAsync` | `src/Vega.Terraria/Networking/NativeOutboundQueue.cs:280` | 35 | 5 | 113 |
| `SceneDisplayProjection.Reconcile` | `src/Vega.Terraria/Scenes/SceneDisplayProjection.cs:41` | 35 | 5 | 67 |

## Longest callable bodies

| Method | File:line | Decision score | Depth | Lines |
|---|---|---:|---:|---:|
| `<top-level entrypoint>` | `src/Vega.Runtime/Program.cs:64` | 2 | 1 | 859 |
| `Start` | `src/Vega.Runtime/Program.cs:117` | 95 | 3 | 806 |
| `TerminalUiHost.Run` | `src/Vega.TerminalUI/TerminalUiHost.cs:97` | 5 | 1 | 394 |
| `TerrariaServerHost.Run` | `src/Vega.Terraria/TerrariaServerHost.cs:30` | 21 | 2 | 239 |
| `RegionBuildAuthorizationPolicy.TryGetInteractionArea` | `src/Vega.Regions/RegionBuildAuthorizationPolicy.cs:126` | 35 | 3 | 188 |
| `VegaWorldImageCompiler.Build` | `src/Vega.Terraria/Worlds/VegaWorldImageFormat.cs:352` | 12 | 3 | 157 |
| `TerrariaWorldImageService.OnLoadWorld` | `src/Vega.Terraria/Worlds/TerrariaWorldImageService.cs:111` | 23 | 4 | 145 |
| `VegaWorldImageReader.Validate` | `src/Vega.Terraria/Worlds/VegaWorldImageFormat.cs:78` | 33 | 2 | 143 |
| `LoopbackRestService.ServeAsync` | `src/Vega.Operations/Actions/LoopbackRestService.cs:72` | 62 | 5 | 137 |
| `VegaWorldImageCompiler.ReadTileRun` | `src/Vega.Terraria/Worlds/VegaWorldImageFormat.cs:658` | 33 | 4 | 132 |
| `SandboxControlService.CreateAsync` | `src/Vega.Sandboxes/SandboxControlService.cs:73` | 43 | 4 | 127 |
| `OperationsSnapshotProvider.Capture` | `src/Vega.Operations/ReadModels/OperationsSnapshotProvider.cs:41` | 22 | 2 | 124 |

## Refactored entry points

| Method | File:line | Decision score | Depth | Lines |
|---|---|---:|---:|---:|
| `GroupCommand.ExecuteAsync` | `src/Vega.Core/Commands/Administration/GroupCommand.cs:15` | 25 | 2 | 37 |
| `GroupCommand.SetPresentationAsync` | `src/Vega.Core/Commands/Administration/GroupCommand.cs:53` | 11 | 4 | 18 |
| `GroupCommand.CreateAsync` | `src/Vega.Core/Commands/Administration/GroupCommand.cs:72` | 4 | 2 | 11 |
| `GroupCommand.DeleteAsync` | `src/Vega.Core/Commands/Administration/GroupCommand.cs:84` | 4 | 2 | 15 |
| `GroupCommand.SetParentAsync` | `src/Vega.Core/Commands/Administration/GroupCommand.cs:100` | 7 | 2 | 19 |
| `GroupCommand.SetPermissionAsync` | `src/Vega.Core/Commands/Administration/GroupCommand.cs:120` | 8 | 2 | 27 |
| `GroupCommand.List` | `src/Vega.Core/Commands/Administration/GroupCommand.cs:148` | 1 | 0 | 12 |
| `GroupCommand.lambda` | `src/Vega.Core/Commands/Administration/GroupCommand.cs:151` | 2 | 1 | 7 |
| `GroupCommand.Show` | `src/Vega.Core/Commands/Administration/GroupCommand.cs:161` | 8 | 2 | 20 |
| `GroupCommand.TryParseColor` | `src/Vega.Core/Commands/Administration/GroupCommand.cs:182` | 7 | 2 | 15 |
| `GroupCommand.CanDelegate` | `src/Vega.Core/Commands/Administration/GroupCommand.cs:198` | 6 | 2 | 23 |
| `GroupCommand.lambda` | `src/Vega.Core/Commands/Administration/GroupCommand.cs:206` | 1 | 0 | 1 |
| `GroupCommand.lambda` | `src/Vega.Core/Commands/Administration/GroupCommand.cs:211` | 1 | 0 | 1 |
| `GroupCommand.Mutation` | `src/Vega.Core/Commands/Administration/GroupCommand.cs:222` | 4 | 2 | 12 |
| `GroupCommand.NotFound` | `src/Vega.Core/Commands/Administration/GroupCommand.cs:235` | 1 | 0 | 3 |
| `GroupCommand.DelegationDenied` | `src/Vega.Core/Commands/Administration/GroupCommand.cs:239` | 1 | 0 | 4 |
| `GroupCommand.Usage` | `src/Vega.Core/Commands/Administration/GroupCommand.cs:244` | 1 | 0 | 5 |
| `WorldEventCommand.ExecuteAsync` | `src/Vega.Modules.Administration/WorldCommands.cs:116` | 14 | 2 | 15 |
| `WorldEventCommand.Parse` | `src/Vega.Modules.Administration/WorldCommands.cs:132` | 29 | 3 | 20 |
| `WorldEventCommand.IsEnabled` | `src/Vega.Modules.Administration/WorldCommands.cs:153` | 6 | 2 | 5 |
| `WorldEventCommand.BuildPatch` | `src/Vega.Modules.Administration/WorldCommands.cs:159` | 16 | 4 | 10 |
| `WorldEventCommand.Usage` | `src/Vega.Modules.Administration/WorldCommands.cs:170` | 1 | 0 | 2 |
| `RestAdministration.WriteAsync` | `src/Vega.Operations/Actions/RestAdministration.cs:73` | 15 | 2 | 23 |
| `RestAdministration.WriteAccountsAsync` | `src/Vega.Operations/Actions/RestAdministration.cs:100` | 6 | 2 | 22 |
| `RestAdministration.WriteGroupsAsync` | `src/Vega.Operations/Actions/RestAdministration.cs:123` | 9 | 3 | 21 |
| `RestAdministration.WriteRegionsAsync` | `src/Vega.Operations/Actions/RestAdministration.cs:145` | 11 | 3 | 30 |
| `RestAdministration.WriteModerationAsync` | `src/Vega.Operations/Actions/RestAdministration.cs:176` | 24 | 3 | 30 |
| `RestAdministration.WriteSandboxesAsync` | `src/Vega.Operations/Actions/RestAdministration.cs:207` | 34 | 3 | 45 |
| `OfflineMigrationCommands.TryRun` | `src/Vega.Runtime/OfflineMigrationCommands.cs:15` | 8 | 1 | 11 |
| `OfflineMigrationCommands.ImportHomes` | `src/Vega.Runtime/OfflineMigrationCommands.cs:27` | 10 | 2 | 26 |
| `OfflineMigrationCommands.ImportWhitelist` | `src/Vega.Runtime/OfflineMigrationCommands.cs:54` | 17 | 3 | 32 |
| `OfflineMigrationCommands.ImportRegions` | `src/Vega.Runtime/OfflineMigrationCommands.cs:87` | 6 | 2 | 24 |
| `OfflineMigrationCommands.ImportSsc` | `src/Vega.Runtime/OfflineMigrationCommands.cs:112` | 10 | 2 | 26 |
| `OfflineMigrationCommands.ImportAccounts` | `src/Vega.Runtime/OfflineMigrationCommands.cs:139` | 5 | 2 | 23 |
| `OfflineMigrationCommands.ImportBans` | `src/Vega.Runtime/OfflineMigrationCommands.cs:163` | 5 | 2 | 24 |
| `OfflineMigrationCommands.ImportWarps` | `src/Vega.Runtime/OfflineMigrationCommands.cs:188` | 9 | 2 | 32 |
