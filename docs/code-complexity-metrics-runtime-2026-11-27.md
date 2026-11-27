# Vega complexity audit (Roslyn syntax metrics)

Source files: **505** · physical lines: **66 443** · callable bodies: **4 759** · syntax errors: **0**

Decision score = 1 + if/loops/catch/case/conditional/switch arm/logical and-or/coalesce/pattern and-or. Max depth counts nested control constructs. Nested callables are measured separately. Span lines include formatting/comments. These are reproducible screening metrics, not a semantic proof of defect risk.

## By project

| Project | Bodies | Max decision score | Score >= 15 | Score >= 30 | Span >= 100 |
|---|---:|---:|---:|---:|---:|
| Vega.Terraria | 1072 | 60 | 51 | 19 | 9 |
| Vega.Core | 1243 | 61 | 41 | 4 | 2 |
| Vega.Modules.Administration | 161 | 35 | 18 | 1 | 0 |
| Vega.Sandboxes | 255 | 43 | 16 | 1 | 1 |
| Vega.Operations | 124 | 34 | 12 | 2 | 1 |
| Vega.Regions | 89 | 37 | 11 | 2 | 1 |
| Vega.Runtime | 121 | 25 | 8 | 0 | 0 |
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
| `SchematicValidator.Validate` | `src/TerraRuntime.Schematics/SchematicValidation.cs:21` | 36 | 4 | 119 |
| `RegionBuildAuthorizationPolicy.TryGetInteractionArea` | `src/Vega.Regions/RegionBuildAuthorizationPolicy.cs:126` | 35 | 3 | 188 |
| `NativeOutboundQueue.WriteLoopAsync` | `src/Vega.Terraria/Networking/NativeOutboundQueue.cs:280` | 35 | 5 | 113 |
| `SceneDisplayProjection.Reconcile` | `src/Vega.Terraria/Scenes/SceneDisplayProjection.cs:41` | 35 | 5 | 67 |

## Deepest control nesting

| Method | File:line | Decision score | Depth | Lines |
|---|---|---:|---:|---:|
| `SandboxBackendLaunchOptions.Parse` | `src/Vega.Sandboxes/SandboxBackendRuntime.cs:25` | 24 | 7 | 74 |
| `SandboxSscSessionCoordinator.RunAsync` | `src/Vega.Sandboxes/SandboxSscSessionCoordinator.cs:119` | 28 | 6 | 90 |
| `SandboxControlService.MonitorAsync` | `src/Vega.Sandboxes/SandboxControlService.cs:575` | 21 | 6 | 90 |
| `NativeOutboundQueue.TryEnqueue` | `src/Vega.Terraria/Networking/NativeOutboundQueue.cs:136` | 16 | 6 | 53 |
| `BanCommand.ExecuteBanToolsAsync` | `src/Vega.Core/Commands/Administration/BanCommand.cs:145` | 61 | 5 | 75 |
| `NativeGameplayEventHooks.SamplePlayerStates` | `src/Vega.Terraria/Hooking/NativeGameplayEventHooks.PlayerState.cs:21` | 45 | 5 | 57 |
| `NativeSceneNetworkHooks.OnSendData` | `src/Vega.Terraria/Hooking/NativeSceneNetworkHooks.cs:82` | 40 | 5 | 83 |
| `TerrariaSceneRenderSink.TryGetSignRecipient` | `src/Vega.Terraria/Scenes/TerrariaSceneRenderSink.Signs.cs:41` | 37 | 5 | 31 |
| `NativeOutboundQueue.WriteLoopAsync` | `src/Vega.Terraria/Networking/NativeOutboundQueue.cs:280` | 35 | 5 | 113 |
| `SceneDisplayProjection.Reconcile` | `src/Vega.Terraria/Scenes/SceneDisplayProjection.cs:41` | 35 | 5 | 67 |
| `ChatPipeline.Process` | `src/Vega.Core/Messaging/ChatPipeline.cs:28` | 28 | 5 | 90 |
| `TerrariaPlayerInventoryService.HasGuiFootprint` | `src/Vega.Terraria/Players/TerrariaPlayerInventoryService.VirtualChestGui.cs:84` | 23 | 5 | 19 |

## Longest callable bodies

| Method | File:line | Decision score | Depth | Lines |
|---|---|---:|---:|---:|
| `TerminalUiHost.Run` | `src/Vega.TerminalUI/TerminalUiHost.cs:97` | 5 | 1 | 394 |
| `TerrariaServerHost.Run` | `src/Vega.Terraria/TerrariaServerHost.cs:30` | 21 | 2 | 239 |
| `RegionBuildAuthorizationPolicy.TryGetInteractionArea` | `src/Vega.Regions/RegionBuildAuthorizationPolicy.cs:126` | 35 | 3 | 188 |
| `VegaWorldImageCompiler.Build` | `src/Vega.Terraria/Worlds/VegaWorldImageFormat.cs:352` | 12 | 3 | 157 |
| `TerrariaWorldImageService.OnLoadWorld` | `src/Vega.Terraria/Worlds/TerrariaWorldImageService.cs:111` | 23 | 4 | 145 |
| `VegaWorldImageReader.Validate` | `src/Vega.Terraria/Worlds/VegaWorldImageFormat.cs:78` | 33 | 2 | 143 |
| `VegaWorldImageCompiler.ReadTileRun` | `src/Vega.Terraria/Worlds/VegaWorldImageFormat.cs:658` | 33 | 4 | 132 |
| `SandboxControlService.CreateAsync` | `src/Vega.Sandboxes/SandboxControlService.cs:73` | 43 | 4 | 127 |
| `OperationsSnapshotProvider.Capture` | `src/Vega.Operations/ReadModels/OperationsSnapshotProvider.cs:41` | 22 | 2 | 124 |
| `DeploymentBackupService.CreateAsync` | `src/Vega.Backups/DeploymentBackupService.cs:60` | 13 | 2 | 122 |
| `SoftRestartCoordinator.RestartAsync` | `src/Vega.Core/Modules/SoftRestartCoordinator.cs:74` | 21 | 3 | 122 |
| `SchematicBinary.Deserialize` | `src/TerraRuntime.Schematics/SchematicBinary.cs:105` | 27 | 3 | 121 |

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
| `LoopbackRestService.Start` | `src/Vega.Operations/Actions/LoopbackRestService.cs:41` | 18 | 2 | 30 |
| `LoopbackRestService.ServeAsync` | `src/Vega.Operations/Actions/LoopbackRestService.cs:72` | 12 | 3 | 24 |
| `LoopbackRestService.HandleRequestAsync` | `src/Vega.Operations/Actions/LoopbackRestService.cs:97` | 25 | 2 | 55 |
| `LoopbackRestService.ReadSnapshotPayload` | `src/Vega.Operations/Actions/LoopbackRestService.cs:153` | 8 | 2 | 21 |
| `LoopbackRestService.lambda` | `src/Vega.Operations/Actions/LoopbackRestService.cs:161` | 1 | 0 | 1 |
| `LoopbackRestService.lambda` | `src/Vega.Operations/Actions/LoopbackRestService.cs:163` | 1 | 0 | 1 |
| `LoopbackRestService.lambda` | `src/Vega.Operations/Actions/LoopbackRestService.cs:164` | 1 | 0 | 2 |
| `LoopbackRestService.lambda` | `src/Vega.Operations/Actions/LoopbackRestService.cs:168` | 1 | 0 | 1 |
| `LoopbackRestService.HandleAdministrationRouteAsync` | `src/Vega.Operations/Actions/LoopbackRestService.cs:175` | 9 | 3 | 22 |
| `LoopbackRestService.HandleWorldSaveAsync` | `src/Vega.Operations/Actions/LoopbackRestService.cs:198` | 7 | 2 | 17 |
| `LoopbackRestService.ReadWorldAsync` | `src/Vega.Operations/Actions/LoopbackRestService.cs:216` | 6 | 2 | 14 |
| `LoopbackRestService.WorldReady` | `src/Vega.Operations/Actions/LoopbackRestService.cs:231` | 3 | 1 | 2 |
| `LoopbackRestService.ApplyWorldPatchAsync` | `src/Vega.Operations/Actions/LoopbackRestService.cs:234` | 17 | 2 | 36 |
| `LoopbackRestService.ApplyPlayerActionAsync` | `src/Vega.Operations/Actions/LoopbackRestService.cs:271` | 24 | 3 | 44 |
| `LoopbackRestService.ApplyAdministrationAsync` | `src/Vega.Operations/Actions/LoopbackRestService.cs:316` | 9 | 2 | 20 |
| `LoopbackRestService.ApplyPluginOperationAsync` | `src/Vega.Operations/Actions/LoopbackRestService.cs:337` | 29 | 3 | 63 |
| `LoopbackRestService.lambda` | `src/Vega.Operations/Actions/LoopbackRestService.cs:340` | 5 | 1 | 2 |
| `LoopbackRestService.lambda` | `src/Vega.Operations/Actions/LoopbackRestService.cs:341` | 4 | 1 | 1 |
| `LoopbackRestService.lambda` | `src/Vega.Operations/Actions/LoopbackRestService.cs:394` | 1 | 0 | 1 |
| `LoopbackRestService.UnambiguousJson` | `src/Vega.Operations/Actions/LoopbackRestService.cs:401` | 8 | 4 | 12 |
| `LoopbackRestService.ReadMutationBodyAsync` | `src/Vega.Operations/Actions/LoopbackRestService.cs:414` | 5 | 2 | 10 |
| `LoopbackRestService.ParsePlayerAction` | `src/Vega.Operations/Actions/LoopbackRestService.cs:427` | 24 | 3 | 25 |
| `LoopbackRestService.ParseWorldPatch` | `src/Vega.Operations/Actions/LoopbackRestService.cs:453` | 33 | 3 | 34 |
| `LoopbackRestService.WorldView` | `src/Vega.Operations/Actions/LoopbackRestService.cs:488` | 1 | 0 | 6 |
| `LoopbackRestService.WriteJsonAsync` | `src/Vega.Operations/Actions/LoopbackRestService.cs:495` | 1 | 0 | 7 |
| `LoopbackRestService.AuditMaintenance` | `src/Vega.Operations/Actions/LoopbackRestService.cs:503` | 2 | 1 | 2 |
| `LoopbackRestService.ReadMetrics` | `src/Vega.Operations/Actions/LoopbackRestService.cs:506` | 1 | 0 | 12 |
| `LoopbackRestService.ParseFingerprint` | `src/Vega.Operations/Actions/LoopbackRestService.cs:519` | 3 | 1 | 6 |
| `LoopbackRestService.Authenticate` | `src/Vega.Operations/Actions/LoopbackRestService.cs:526` | 11 | 2 | 15 |
| `LoopbackRestService.ExecuteAsync` | `src/Vega.Operations/Actions/LoopbackRestService.cs:542` | 14 | 2 | 20 |
| `LoopbackRestService.Dispose` | `src/Vega.Operations/Actions/LoopbackRestService.cs:563` | 4 | 2 | 14 |
| `LoopbackRestService.CanMaintain` | `src/Vega.Operations/Actions/LoopbackRestService.cs:578` | 2 | 1 | 1 |
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
| `RuntimeHost.Run` | `src/Vega.Runtime/RuntimeHost.cs:34` | 13 | 1 | 27 |
| `RuntimeHost.ReadLaunch` | `src/Vega.Runtime/RuntimeHost.cs:62` | 7 | 2 | 52 |
| `RuntimeHost.PrepareWorld` | `src/Vega.Runtime/RuntimeHost.cs:115` | 7 | 3 | 35 |
| `RuntimeHost.CreateFoundation` | `src/Vega.Runtime/RuntimeHost.cs:151` | 6 | 1 | 66 |
| `RuntimeHost.lambda` | `src/Vega.Runtime/RuntimeHost.cs:178` | 3 | 2 | 6 |
| `RuntimeHost.RunNative` | `src/Vega.Runtime/RuntimeHost.cs:218` | 7 | 3 | 42 |
| `RuntimeHost.lambda` | `src/Vega.Runtime/RuntimeHost.cs:254` | 1 | 0 | 1 |
| `RuntimeHost.CreateIdentity` | `src/Vega.Runtime/RuntimeHost.Gameplay.cs:29` | 3 | 2 | 67 |
| `RuntimeHost.lambda` | `src/Vega.Runtime/RuntimeHost.Gameplay.cs:71` | 1 | 0 | 1 |
| `RuntimeHost.lambda` | `src/Vega.Runtime/RuntimeHost.Gameplay.cs:90` | 3 | 2 | 2 |
| `RuntimeHost.lambda` | `src/Vega.Runtime/RuntimeHost.Gameplay.cs:91` | 1 | 0 | 1 |
| `RuntimeHost.CreateCharacters` | `src/Vega.Runtime/RuntimeHost.Gameplay.cs:97` | 14 | 2 | 62 |
| `RuntimeHost.CreatePacketPolicies` | `src/Vega.Runtime/RuntimeHost.Gameplay.cs:160` | 1 | 0 | 65 |
| `RuntimeHost.lambda` | `src/Vega.Runtime/RuntimeHost.Gameplay.cs:171` | 1 | 0 | 1 |
| `RuntimeHost.lambda` | `src/Vega.Runtime/RuntimeHost.Gameplay.cs:172` | 1 | 0 | 1 |
| `RuntimeHost.lambda` | `src/Vega.Runtime/RuntimeHost.Gameplay.cs:180` | 1 | 0 | 1 |
| `RuntimeHost.lambda` | `src/Vega.Runtime/RuntimeHost.Gameplay.cs:203` | 1 | 0 | 1 |
| `RuntimeHost.lambda` | `src/Vega.Runtime/RuntimeHost.Gameplay.cs:204` | 1 | 0 | 1 |
| `RuntimeHost.lambda` | `src/Vega.Runtime/RuntimeHost.Gameplay.cs:205` | 1 | 0 | 1 |
| `RuntimeHost.lambda` | `src/Vega.Runtime/RuntimeHost.Gameplay.cs:207` | 1 | 0 | 1 |
| `RuntimeHost.lambda` | `src/Vega.Runtime/RuntimeHost.Gameplay.cs:208` | 1 | 0 | 1 |
| `RuntimeHost.lambda` | `src/Vega.Runtime/RuntimeHost.Gameplay.cs:209` | 1 | 0 | 1 |
| `RuntimeHost.lambda` | `src/Vega.Runtime/RuntimeHost.Gameplay.cs:210` | 1 | 0 | 1 |
| `RuntimeHost.lambda` | `src/Vega.Runtime/RuntimeHost.Gameplay.cs:220` | 2 | 1 | 2 |
| `RuntimeHost.RegisterSandboxCommands` | `src/Vega.Runtime/RuntimeHost.Gameplay.cs:226` | 2 | 1 | 19 |
| `RuntimeHost.RegisterAdministration` | `src/Vega.Runtime/RuntimeHost.Gameplay.cs:246` | 6 | 2 | 44 |
| `RuntimeHost.lambda` | `src/Vega.Runtime/RuntimeHost.Gameplay.cs:263` | 1 | 0 | 1 |
| `RuntimeHost.lambda` | `src/Vega.Runtime/RuntimeHost.Gameplay.cs:274` | 1 | 0 | 1 |
| `RuntimeHost.lambda` | `src/Vega.Runtime/RuntimeHost.Gameplay.cs:276` | 1 | 0 | 1 |
| `RuntimeHost.lambda` | `src/Vega.Runtime/RuntimeHost.Gameplay.cs:278` | 1 | 0 | 1 |
| `RuntimeHost.CreateOperations` | `src/Vega.Runtime/RuntimeHost.Operations.cs:28` | 12 | 3 | 52 |
| `RuntimeHost.lambda` | `src/Vega.Runtime/RuntimeHost.Operations.cs:67` | 1 | 0 | 1 |
| `RuntimeHost.StartPlugins` | `src/Vega.Runtime/RuntimeHost.Operations.cs:81` | 12 | 2 | 80 |
| `RuntimeHost.lambda` | `src/Vega.Runtime/RuntimeHost.Operations.cs:113` | 1 | 0 | 6 |
| `RuntimeHost.lambda` | `src/Vega.Runtime/RuntimeHost.Operations.cs:149` | 2 | 1 | 3 |
| `RuntimeHost.StartDashboard` | `src/Vega.Runtime/RuntimeHost.Operations.cs:162` | 8 | 1 | 90 |
| `RuntimeHost.lambda` | `src/Vega.Runtime/RuntimeHost.Operations.cs:214` | 1 | 0 | 1 |
| `RuntimeHost.CreateAdmission` | `src/Vega.Runtime/RuntimeHost.World.cs:38` | 14 | 2 | 54 |
| `RuntimeHost.CreateWorld` | `src/Vega.Runtime/RuntimeHost.World.cs:93` | 5 | 2 | 86 |
| `RuntimeHost.OnSceneSessionOpened` | `src/Vega.Runtime/RuntimeHost.World.cs:108` | 1 | 0 | 5 |
| `RuntimeHost.OnSceneSessionClosed` | `src/Vega.Runtime/RuntimeHost.World.cs:113` | 1 | 0 | 5 |
| `RuntimeHost.StartModules` | `src/Vega.Runtime/RuntimeHost.World.cs:180` | 2 | 1 | 83 |
| `RuntimeHost.CreateRestartableGraph` | `src/Vega.Runtime/RuntimeHost.World.cs:189` | 9 | 2 | 57 |
| `RuntimeHost.lambda` | `src/Vega.Runtime/RuntimeHost.World.cs:212` | 2 | 1 | 6 |
| `RuntimeHost.Enabled` | `src/Vega.Runtime/RuntimeHost.World.cs:220` | 1 | 0 | 1 |
| `RuntimeHost.lambda` | `src/Vega.Runtime/RuntimeHost.World.cs:233` | 1 | 0 | 1 |
| `RuntimeHost.lambda` | `src/Vega.Runtime/RuntimeHost.World.cs:238` | 1 | 0 | 1 |
| `RuntimeHost.lambda` | `src/Vega.Runtime/RuntimeHost.World.cs:239` | 5 | 1 | 6 |
| `TerrariaWorldEnvironmentService.GetSpawnPosition` | `src/Vega.Terraria/Worlds/TerrariaWorldEnvironmentService.cs:12` | 4 | 2 | 5 |
| `TerrariaWorldEnvironmentService.CaptureAsync` | `src/Vega.Terraria/Worlds/TerrariaWorldEnvironmentService.cs:18` | 1 | 0 | 7 |
| `TerrariaWorldEnvironmentService.lambda` | `src/Vega.Terraria/Worlds/TerrariaWorldEnvironmentService.cs:23` | 2 | 1 | 1 |
| `TerrariaWorldEnvironmentService.PatchAsync` | `src/Vega.Terraria/Worlds/TerrariaWorldEnvironmentService.cs:26` | 1 | 0 | 16 |
| `TerrariaWorldEnvironmentService.lambda` | `src/Vega.Terraria/Worlds/TerrariaWorldEnvironmentService.cs:33` | 1 | 0 | 1 |
| `TerrariaWorldEnvironmentService.Patch` | `src/Vega.Terraria/Worlds/TerrariaWorldEnvironmentService.cs:43` | 8 | 2 | 32 |
| `TerrariaWorldEnvironmentService.ApplyHardmode` | `src/Vega.Terraria/Worlds/TerrariaWorldEnvironmentService.cs:76` | 4 | 1 | 8 |
| `TerrariaWorldEnvironmentService.ClearAnglerPlayer` | `src/Vega.Terraria/Worlds/TerrariaWorldEnvironmentService.cs:85` | 11 | 4 | 13 |
| `TerrariaWorldEnvironmentService.lambda` | `src/Vega.Terraria/Worlds/TerrariaWorldEnvironmentService.cs:90` | 1 | 0 | 1 |
| `TerrariaWorldEnvironmentService.ValidateRegularPatch` | `src/Vega.Terraria/Worlds/TerrariaWorldEnvironmentService.cs:99` | 30 | 2 | 35 |
| `TerrariaWorldEnvironmentService.ApplyClockAndWeather` | `src/Vega.Terraria/Worlds/TerrariaWorldEnvironmentService.cs:135` | 12 | 2 | 20 |
| `TerrariaWorldEnvironmentService.ApplyLocationsAndRules` | `src/Vega.Terraria/Worlds/TerrariaWorldEnvironmentService.cs:156` | 6 | 1 | 16 |
| `TerrariaWorldEnvironmentService.ApplySeasonAndAngler` | `src/Vega.Terraria/Worlds/TerrariaWorldEnvironmentService.cs:173` | 7 | 4 | 22 |
| `TerrariaWorldEnvironmentService.Capture` | `src/Vega.Terraria/Worlds/TerrariaWorldEnvironmentService.cs:196` | 1 | 0 | 32 |
| `TerrariaWorldEnvironmentService.IsNativeEventActive` | `src/Vega.Terraria/Worlds/TerrariaWorldEnvironmentService.cs:229` | 10 | 2 | 12 |
| `TerrariaWorldEnvironmentService.InvasionType` | `src/Vega.Terraria/Worlds/TerrariaWorldEnvironmentService.cs:242` | 6 | 2 | 8 |
| `TerrariaWorldEnvironmentService.ApplyNativeEvent` | `src/Vega.Terraria/Worlds/TerrariaWorldEnvironmentService.cs:251` | 34 | 4 | 65 |
| `TerrariaWorldEnvironmentService.InsideWorld` | `src/Vega.Terraria/Worlds/TerrariaWorldEnvironmentService.cs:317` | 4 | 1 | 2 |
| `TerrariaWorldEnvironmentService.Invalid` | `src/Vega.Terraria/Worlds/TerrariaWorldEnvironmentService.cs:320` | 1 | 0 | 2 |
| `TerrariaWorldEnvironmentService.DispatchAsync` | `src/Vega.Terraria/Worlds/TerrariaWorldEnvironmentService.cs:323` | 5 | 1 | 42 |
| `TerrariaWorldEnvironmentService.lambda` | `src/Vega.Terraria/Worlds/TerrariaWorldEnvironmentService.cs:348` | 3 | 1 | 11 |
| `TerrariaWorldNativeActions.SwitchEvilAsync` | `src/Vega.Terraria/Worlds/TerrariaWorldNativeActions.cs:12` | 1 | 0 | 11 |
| `TerrariaWorldNativeActions.lambda` | `src/Vega.Terraria/Worlds/TerrariaWorldNativeActions.cs:13` | 4 | 2 | 10 |
| `TerrariaWorldNativeActions.SettleAsync` | `src/Vega.Terraria/Worlds/TerrariaWorldNativeActions.cs:24` | 1 | 0 | 9 |
| `TerrariaWorldNativeActions.lambda` | `src/Vega.Terraria/Worlds/TerrariaWorldNativeActions.cs:25` | 3 | 1 | 8 |
| `TerrariaWorldNativeActions.GrowAsync` | `src/Vega.Terraria/Worlds/TerrariaWorldNativeActions.cs:34` | 1 | 0 | 2 |
| `TerrariaWorldNativeActions.lambda` | `src/Vega.Terraria/Worlds/TerrariaWorldNativeActions.cs:35` | 1 | 0 | 1 |
| `TerrariaWorldNativeActions.Grow` | `src/Vega.Terraria/Worlds/TerrariaWorldNativeActions.cs:37` | 18 | 3 | 28 |
| `TerrariaWorldNativeActions.GrowCactus` | `src/Vega.Terraria/Worlds/TerrariaWorldNativeActions.cs:66` | 6 | 2 | 11 |
| `TerrariaWorldNativeActions.GrowHerb` | `src/Vega.Terraria/Worlds/TerrariaWorldNativeActions.cs:78` | 9 | 2 | 14 |
| `TerrariaWorldNativeActions.RequiredGround` | `src/Vega.Terraria/Worlds/TerrariaWorldNativeActions.cs:93` | 22 | 2 | 17 |
| `TerrariaWorldNativeActions.GrowTree` | `src/Vega.Terraria/Worlds/TerrariaWorldNativeActions.cs:111` | 16 | 2 | 16 |
| `TerrariaWorldNativeActions.RunAsync` | `src/Vega.Terraria/Worlds/TerrariaWorldNativeActions.cs:128` | 6 | 1 | 22 |
| `TerrariaWorldNativeActions.Apply` | `src/Vega.Terraria/Worlds/TerrariaWorldNativeActions.cs:134` | 5 | 2 | 7 |
| `TerrariaWorldNativeActions.lambda` | `src/Vega.Terraria/Worlds/TerrariaWorldNativeActions.cs:143` | 2 | 1 | 5 |
| `TerrariaWorldNativeActions.Invalid` | `src/Vega.Terraria/Worlds/TerrariaWorldNativeActions.cs:151` | 1 | 0 | 1 |
