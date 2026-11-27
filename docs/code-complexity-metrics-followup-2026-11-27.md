# Vega complexity audit (Roslyn syntax metrics)

Source files: **505** · physical lines: **66 645** · callable bodies: **4 803** · syntax errors: **0**

Decision score = 1 + if/loops/catch/case/conditional/switch arm/logical and-or/coalesce/pattern and-or. Max depth counts nested control constructs. Nested callables are measured separately. Span lines include formatting/comments. These are reproducible screening metrics, not a semantic proof of defect risk.

## By project

| Project | Bodies | Max decision score | Score >= 15 | Score >= 30 | Span >= 100 |
|---|---:|---:|---:|---:|---:|
| Vega.Terraria | 1088 | 60 | 54 | 12 | 7 |
| Vega.Core | 1252 | 31 | 42 | 2 | 2 |
| Vega.Modules.Administration | 162 | 29 | 19 | 0 | 0 |
| Vega.Sandboxes | 258 | 28 | 17 | 0 | 0 |
| Vega.Operations | 125 | 33 | 12 | 1 | 1 |
| Vega.Regions | 91 | 35 | 11 | 1 | 1 |
| Vega.Runtime | 121 | 25 | 8 | 0 | 0 |
| Vega.Persistence | 300 | 20 | 6 | 0 | 1 |
| Vega.Cluster | 122 | 37 | 4 | 2 | 0 |
| Vega.Contracts | 229 | 19 | 4 | 0 | 0 |
| Vega.Modules.Scenes | 109 | 27 | 4 | 0 | 0 |
| Vega.Modules.Navigation | 80 | 28 | 3 | 0 | 0 |
| Vega.TerminalUI | 188 | 27 | 3 | 0 | 1 |
| Vega.Modules.Restrictions | 20 | 24 | 2 | 0 | 0 |
| Vega.PluginSdk | 467 | 17 | 2 | 0 | 0 |
| TerraRuntime.Schematics | 79 | 27 | 1 | 0 | 1 |
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
| `PacketPipelineClassification.Get` | `src/Vega.Terraria/Networking/PacketPipelineClassification.cs:37` | 60 | 2 | 90 |
| `NativeSceneNetworkHooks.OnSendData` | `src/Vega.Terraria/Hooking/NativeSceneNetworkHooks.cs:82` | 40 | 5 | 83 |
| `AuthenticatedClusterTransport.HandleAsync` | `src/Vega.Cluster/Transport/AuthenticatedClusterTransport.cs:204` | 37 | 3 | 64 |
| `TerrariaSceneRenderSink.TryGetSignRecipient` | `src/Vega.Terraria/Scenes/TerrariaSceneRenderSink.Signs.cs:41` | 37 | 5 | 31 |
| `RegionBuildAuthorizationPolicy.TryGetInteractionArea` | `src/Vega.Regions/RegionBuildAuthorizationPolicy.cs:126` | 35 | 3 | 188 |
| `NativeOutboundQueue.WriteLoopAsync` | `src/Vega.Terraria/Networking/NativeOutboundQueue.cs:280` | 35 | 5 | 113 |
| `SceneDisplayProjection.Reconcile` | `src/Vega.Terraria/Scenes/SceneDisplayProjection.cs:41` | 35 | 5 | 67 |
| `TerrariaWorldEnvironmentService.ApplyNativeEvent` | `src/Vega.Terraria/Worlds/TerrariaWorldEnvironmentService.cs:251` | 34 | 4 | 65 |
| `VegaWorldImageCompiler.ReadTileRun` | `src/Vega.Terraria/Worlds/VegaWorldImageFormat.cs:675` | 33 | 4 | 132 |
| `AuthenticatedClusterTransport.AuthenticatedClusterTransport(ctor)` | `src/Vega.Cluster/Transport/AuthenticatedClusterTransport.cs:50` | 33 | 4 | 53 |
| `LoopbackRestService.ParseWorldPatch` | `src/Vega.Operations/Actions/LoopbackRestService.cs:453` | 33 | 3 | 34 |
| `TerrariaNpcActorService.Create` | `src/Vega.Terraria/Worlds/TerrariaNpcActorService.cs:44` | 32 | 2 | 45 |
| `TerrariaNpcActorService.Exchange` | `src/Vega.Terraria/Worlds/TerrariaNpcActorService.cs:378` | 31 | 3 | 60 |
| `SceneRuntime.FreezeVirtualObjects` | `src/Vega.Core/Scenes/SceneRuntime.VirtualChests.cs:18` | 31 | 3 | 56 |
| `WorldMutationSanityPolicy.Process` | `src/Vega.Core/Networking/WorldMutationSanityPolicy.cs:24` | 30 | 2 | 79 |
| `TerrariaPlayerInventoryService.AcceptManagedDisplayTransfer` | `src/Vega.Terraria/Players/TerrariaPlayerInventoryService.ManagedDisplays.cs:19` | 30 | 2 | 40 |
| `TerrariaWorldEnvironmentService.ValidateRegularPatch` | `src/Vega.Terraria/Worlds/TerrariaWorldEnvironmentService.cs:99` | 30 | 2 | 35 |
| `TerrariaWorldNpcService.Butcher` | `src/Vega.Terraria/Worlds/TerrariaWorldNpcService.cs:225` | 30 | 3 | 34 |
| `BanCommand.ExecuteAsync` | `src/Vega.Core/Commands/Administration/BanCommand.cs:29` | 29 | 3 | 115 |
| `LoopbackRestService.ApplyPluginOperationAsync` | `src/Vega.Operations/Actions/LoopbackRestService.cs:337` | 29 | 3 | 63 |

## Deepest control nesting

| Method | File:line | Decision score | Depth | Lines |
|---|---|---:|---:|---:|
| `SandboxBackendLaunchOptions.Parse` | `src/Vega.Sandboxes/SandboxBackendRuntime.cs:25` | 24 | 7 | 74 |
| `SandboxSscSessionCoordinator.RunAsync` | `src/Vega.Sandboxes/SandboxSscSessionCoordinator.cs:119` | 28 | 6 | 90 |
| `SandboxControlService.MonitorAsync` | `src/Vega.Sandboxes/SandboxControlService.cs:588` | 21 | 6 | 90 |
| `NativeOutboundQueue.TryEnqueue` | `src/Vega.Terraria/Networking/NativeOutboundQueue.cs:136` | 16 | 6 | 53 |
| `NativeSceneNetworkHooks.OnSendData` | `src/Vega.Terraria/Hooking/NativeSceneNetworkHooks.cs:82` | 40 | 5 | 83 |
| `TerrariaSceneRenderSink.TryGetSignRecipient` | `src/Vega.Terraria/Scenes/TerrariaSceneRenderSink.Signs.cs:41` | 37 | 5 | 31 |
| `NativeOutboundQueue.WriteLoopAsync` | `src/Vega.Terraria/Networking/NativeOutboundQueue.cs:280` | 35 | 5 | 113 |
| `SceneDisplayProjection.Reconcile` | `src/Vega.Terraria/Scenes/SceneDisplayProjection.cs:41` | 35 | 5 | 67 |
| `ChatPipeline.Process` | `src/Vega.Core/Messaging/ChatPipeline.cs:28` | 28 | 5 | 90 |
| `TerrariaPlayerInventoryService.HasGuiFootprint` | `src/Vega.Terraria/Players/TerrariaPlayerInventoryService.VirtualChestGui.cs:84` | 23 | 5 | 19 |
| `SceneRuntime.Update` | `src/Vega.Core/Scenes/SceneRuntime.cs:297` | 18 | 5 | 68 |
| `SceneRuntime.HasOverlayInRegion` | `src/Vega.Core/Scenes/SceneRuntime.cs:24` | 16 | 5 | 25 |

## Longest callable bodies

| Method | File:line | Decision score | Depth | Lines |
|---|---|---:|---:|---:|
| `TerminalUiHost.Run` | `src/Vega.TerminalUI/TerminalUiHost.cs:97` | 5 | 1 | 394 |
| `TerrariaServerHost.Run` | `src/Vega.Terraria/TerrariaServerHost.cs:30` | 21 | 2 | 239 |
| `RegionBuildAuthorizationPolicy.TryGetInteractionArea` | `src/Vega.Regions/RegionBuildAuthorizationPolicy.cs:126` | 35 | 3 | 188 |
| `VegaWorldImageCompiler.Build` | `src/Vega.Terraria/Worlds/VegaWorldImageFormat.cs:369` | 12 | 3 | 157 |
| `TerrariaWorldImageService.OnLoadWorld` | `src/Vega.Terraria/Worlds/TerrariaWorldImageService.cs:111` | 23 | 4 | 145 |
| `VegaWorldImageCompiler.ReadTileRun` | `src/Vega.Terraria/Worlds/VegaWorldImageFormat.cs:675` | 33 | 4 | 132 |
| `OperationsSnapshotProvider.Capture` | `src/Vega.Operations/ReadModels/OperationsSnapshotProvider.cs:41` | 22 | 2 | 124 |
| `DeploymentBackupService.CreateAsync` | `src/Vega.Backups/DeploymentBackupService.cs:60` | 13 | 2 | 122 |
| `SoftRestartCoordinator.RestartAsync` | `src/Vega.Core/Modules/SoftRestartCoordinator.cs:74` | 21 | 3 | 122 |
| `SchematicBinary.Deserialize` | `src/TerraRuntime.Schematics/SchematicBinary.cs:105` | 27 | 3 | 121 |
| `BanCommand.ExecuteAsync` | `src/Vega.Core/Commands/Administration/BanCommand.cs:29` | 29 | 3 | 115 |
| `NativeOutboundQueue.WriteLoopAsync` | `src/Vega.Terraria/Networking/NativeOutboundQueue.cs:280` | 35 | 5 | 113 |

## Refactored entry points

| Method | File:line | Decision score | Depth | Lines |
|---|---|---:|---:|---:|
| `SchematicValidator.Validate` | `src/TerraRuntime.Schematics/SchematicValidation.cs:21` | 1 | 0 | 11 |
| `SchematicValidator.ValidateTiles` | `src/TerraRuntime.Schematics/SchematicValidation.cs:33` | 5 | 2 | 14 |
| `SchematicValidator.ValidateContainers` | `src/TerraRuntime.Schematics/SchematicValidation.cs:48` | 3 | 1 | 20 |
| `SchematicValidator.ValidateTileEntities` | `src/TerraRuntime.Schematics/SchematicValidation.cs:69` | 10 | 3 | 36 |
| `SchematicValidator.ValidateActorsAndItems` | `src/TerraRuntime.Schematics/SchematicValidation.cs:106` | 11 | 3 | 24 |
| `SchematicValidator.ValidateMarkersAndMetadata` | `src/TerraRuntime.Schematics/SchematicValidation.cs:131` | 11 | 4 | 30 |
| `SchematicValidator.ValidateChestCapacity` | `src/TerraRuntime.Schematics/SchematicValidation.cs:162` | 3 | 2 | 5 |
| `SchematicValidator.ValidateHeader` | `src/TerraRuntime.Schematics/SchematicValidation.cs:168` | 9 | 2 | 15 |
| `SchematicValidator.ValidateCount` | `src/TerraRuntime.Schematics/SchematicValidation.cs:184` | 2 | 1 | 5 |
| `SchematicValidator.ValidateTilePosition` | `src/TerraRuntime.Schematics/SchematicValidation.cs:190` | 3 | 2 | 5 |
| `SchematicValidator.ValidatePixelPosition` | `src/TerraRuntime.Schematics/SchematicValidation.cs:196` | 7 | 2 | 5 |
| `SchematicValidator.ValidateItems` | `src/TerraRuntime.Schematics/SchematicValidation.cs:202` | 2 | 1 | 7 |
| `SchematicValidator.ValidateItem` | `src/TerraRuntime.Schematics/SchematicValidation.cs:210` | 7 | 3 | 12 |
| `SchematicValidator.ValidateString` | `src/TerraRuntime.Schematics/SchematicValidation.cs:223` | 5 | 2 | 19 |
| `BanCommand.ExecuteAsync` | `src/Vega.Core/Commands/Administration/BanCommand.cs:29` | 29 | 3 | 115 |
| `BanCommand.lambda` | `src/Vega.Core/Commands/Administration/BanCommand.cs:93` | 1 | 0 | 1 |
| `BanCommand.ExecuteBanToolsAsync` | `src/Vega.Core/Commands/Administration/BanCommand.cs:145` | 13 | 2 | 15 |
| `BanCommand.ListRanges` | `src/Vega.Core/Commands/Administration/BanCommand.cs:161` | 7 | 2 | 8 |
| `BanCommand.lambda` | `src/Vega.Core/Commands/Administration/BanCommand.cs:166` | 1 | 0 | 1 |
| `BanCommand.lambda` | `src/Vega.Core/Commands/Administration/BanCommand.cs:167` | 1 | 0 | 1 |
| `BanCommand.MutateRangeAsync` | `src/Vega.Core/Commands/Administration/BanCommand.cs:170` | 12 | 3 | 18 |
| `BanCommand.lambda` | `src/Vega.Core/Commands/Administration/BanCommand.cs:179` | 1 | 0 | 1 |
| `BanCommand.ExecutePlayerBanToolsAsync` | `src/Vega.Core/Commands/Administration/BanCommand.cs:189` | 17 | 4 | 23 |
| `BanCommand.lambda` | `src/Vega.Core/Commands/Administration/BanCommand.cs:193` | 1 | 0 | 1 |
| `BanCommand.ResolveBanToolSubjectsAsync` | `src/Vega.Core/Commands/Administration/BanCommand.cs:213` | 15 | 4 | 21 |
| `BanCommand.DescribeBanToolSubjects` | `src/Vega.Core/Commands/Administration/BanCommand.cs:235` | 2 | 1 | 5 |
| `BanCommand.lambda` | `src/Vega.Core/Commands/Administration/BanCommand.cs:237` | 1 | 0 | 1 |
| `BanCommand.lambda` | `src/Vega.Core/Commands/Administration/BanCommand.cs:237` | 2 | 1 | 1 |
| `BanCommand.lambda` | `src/Vega.Core/Commands/Administration/BanCommand.cs:238` | 1 | 0 | 1 |
| `BanCommand.BanToolsUsage` | `src/Vega.Core/Commands/Administration/BanCommand.cs:241` | 1 | 0 | 1 |
| `BanCommand.AddTypedAsync` | `src/Vega.Core/Commands/Administration/BanCommand.cs:243` | 9 | 2 | 56 |
| `BanCommand.lambda` | `src/Vega.Core/Commands/Administration/BanCommand.cs:282` | 1 | 0 | 1 |
| `BanCommand.List` | `src/Vega.Core/Commands/Administration/BanCommand.cs:300` | 2 | 1 | 24 |
| `BanCommand.lambda` | `src/Vega.Core/Commands/Administration/BanCommand.cs:303` | 2 | 1 | 1 |
| `BanCommand.lambda` | `src/Vega.Core/Commands/Administration/BanCommand.cs:304` | 1 | 0 | 1 |
| `BanCommand.lambda` | `src/Vega.Core/Commands/Administration/BanCommand.cs:314` | 1 | 0 | 8 |
| `BanCommand.Show` | `src/Vega.Core/Commands/Administration/BanCommand.cs:325` | 4 | 2 | 23 |
| `BanCommand.lambda` | `src/Vega.Core/Commands/Administration/BanCommand.cs:333` | 1 | 0 | 1 |
| `BanCommand.RevokeAsync` | `src/Vega.Core/Commands/Administration/BanCommand.cs:349` | 5 | 2 | 29 |
| `BanCommand.lambda` | `src/Vega.Core/Commands/Administration/BanCommand.cs:359` | 1 | 0 | 1 |
| `BanCommand.TryParseKind` | `src/Vega.Core/Commands/Administration/BanCommand.cs:379` | 9 | 2 | 13 |
| `BanCommand.TryParseDuration` | `src/Vega.Core/Commands/Administration/BanCommand.cs:393` | 6 | 2 | 13 |
| `BanCommand.FormatSubject` | `src/Vega.Core/Commands/Administration/BanCommand.cs:407` | 5 | 2 | 8 |
| `BanCommand.FormatExpiry` | `src/Vega.Core/Commands/Administration/BanCommand.cs:416` | 2 | 1 | 2 |
| `BanCommand.FindPlayers` | `src/Vega.Core/Commands/Administration/BanCommand.cs:419` | 4 | 2 | 20 |
| `BanCommand.lambda` | `src/Vega.Core/Commands/Administration/BanCommand.cs:422` | 1 | 0 | 1 |
| `BanCommand.lambda` | `src/Vega.Core/Commands/Administration/BanCommand.cs:426` | 1 | 0 | 1 |
| `BanCommand.lambda` | `src/Vega.Core/Commands/Administration/BanCommand.cs:431` | 1 | 0 | 1 |
| `BanCommand.lambda` | `src/Vega.Core/Commands/Administration/BanCommand.cs:436` | 1 | 0 | 1 |
| `BanCommand.TryGetIpAddress` | `src/Vega.Core/Commands/Administration/BanCommand.cs:440` | 4 | 1 | 22 |
| `BanCommand.InvalidUsage` | `src/Vega.Core/Commands/Administration/BanCommand.cs:463` | 1 | 0 | 5 |
| `BanCommand.Failure` | `src/Vega.Core/Commands/Administration/BanCommand.cs:469` | 1 | 0 | 7 |
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
| `MuteCommand.ExecuteAsync` | `src/Vega.Core/Commands/Administration/MuteCommand.cs:29` | 10 | 2 | 23 |
| `MuteCommand.DeleteByPlayerAsync` | `src/Vega.Core/Commands/Administration/MuteCommand.cs:53` | 13 | 2 | 24 |
| `MuteCommand.lambda` | `src/Vega.Core/Commands/Administration/MuteCommand.cs:65` | 1 | 0 | 1 |
| `MuteCommand.lambda` | `src/Vega.Core/Commands/Administration/MuteCommand.cs:67` | 5 | 1 | 2 |
| `MuteCommand.AddCompatibilityAsync` | `src/Vega.Core/Commands/Administration/MuteCommand.cs:78` | 17 | 2 | 24 |
| `MuteCommand.ObservedIdentifiers` | `src/Vega.Core/Commands/Administration/MuteCommand.cs:103` | 5 | 3 | 10 |
| `MuteCommand.AddOnlineAsync` | `src/Vega.Core/Commands/Administration/MuteCommand.cs:114` | 11 | 2 | 30 |
| `MuteCommand.lambda` | `src/Vega.Core/Commands/Administration/MuteCommand.cs:129` | 1 | 0 | 1 |
| `MuteCommand.AddTypedAsync` | `src/Vega.Core/Commands/Administration/MuteCommand.cs:145` | 9 | 2 | 26 |
| `MuteCommand.lambda` | `src/Vega.Core/Commands/Administration/MuteCommand.cs:158` | 3 | 2 | 3 |
| `MuteCommand.lambda` | `src/Vega.Core/Commands/Administration/MuteCommand.cs:161` | 1 | 0 | 1 |
| `MuteCommand.CreateAsync` | `src/Vega.Core/Commands/Administration/MuteCommand.cs:172` | 7 | 2 | 28 |
| `MuteCommand.List` | `src/Vega.Core/Commands/Administration/MuteCommand.cs:201` | 8 | 2 | 10 |
| `MuteCommand.Show` | `src/Vega.Core/Commands/Administration/MuteCommand.cs:212` | 4 | 2 | 8 |
| `MuteCommand.lambda` | `src/Vega.Core/Commands/Administration/MuteCommand.cs:216` | 1 | 0 | 1 |
| `MuteCommand.RevokeAsync` | `src/Vega.Core/Commands/Administration/MuteCommand.cs:221` | 5 | 2 | 17 |
| `MuteCommand.lambda` | `src/Vega.Core/Commands/Administration/MuteCommand.cs:228` | 1 | 0 | 1 |
| `MuteCommand.Format` | `src/Vega.Core/Commands/Administration/MuteCommand.cs:239` | 1 | 0 | 10 |
| `MuteCommand.FindPlayers` | `src/Vega.Core/Commands/Administration/MuteCommand.cs:250` | 2 | 1 | 8 |
| `MuteCommand.lambda` | `src/Vega.Core/Commands/Administration/MuteCommand.cs:252` | 1 | 0 | 1 |
| `MuteCommand.lambda` | `src/Vega.Core/Commands/Administration/MuteCommand.cs:254` | 1 | 0 | 1 |
| `MuteCommand.TryParseKind` | `src/Vega.Core/Commands/Administration/MuteCommand.cs:259` | 5 | 2 | 10 |
| `MuteCommand.CanMute` | `src/Vega.Core/Commands/Administration/MuteCommand.cs:270` | 5 | 2 | 12 |
| `MuteCommand.TargetHas` | `src/Vega.Core/Commands/Administration/MuteCommand.cs:275` | 4 | 2 | 3 |
| `MuteCommand.lambda` | `src/Vega.Core/Commands/Administration/MuteCommand.cs:278` | 1 | 0 | 1 |
| `MuteCommand.Level` | `src/Vega.Core/Commands/Administration/MuteCommand.cs:279` | 2 | 1 | 1 |
| `MuteCommand.TryParseDuration` | `src/Vega.Core/Commands/Administration/MuteCommand.cs:283` | 1 | 0 | 2 |
| `MuteCommand.Usage` | `src/Vega.Core/Commands/Administration/MuteCommand.cs:286` | 1 | 0 | 5 |
| `MuteCommand.Failure` | `src/Vega.Core/Commands/Administration/MuteCommand.cs:292` | 1 | 0 | 2 |
| `ItemCommand.ExecuteAsync` | `src/Vega.Modules.Administration/ItemCommands.cs:16` | 17 | 3 | 29 |
| `ItemCommand.ReportDeliveryAsync` | `src/Vega.Modules.Administration/ItemCommands.cs:46` | 19 | 3 | 23 |
| `WorldEventCommand.ExecuteAsync` | `src/Vega.Modules.Administration/WorldCommands.cs:116` | 14 | 2 | 15 |
| `WorldEventCommand.Parse` | `src/Vega.Modules.Administration/WorldCommands.cs:132` | 29 | 3 | 20 |
| `WorldEventCommand.IsEnabled` | `src/Vega.Modules.Administration/WorldCommands.cs:153` | 6 | 2 | 5 |
| `WorldEventCommand.BuildPatch` | `src/Vega.Modules.Administration/WorldCommands.cs:159` | 16 | 4 | 10 |
| `WorldEventCommand.Usage` | `src/Vega.Modules.Administration/WorldCommands.cs:170` | 1 | 0 | 2 |
| `HomeCommand.ExecuteAsync` | `src/Vega.Modules.Navigation/HomeCommand.cs:25` | 22 | 2 | 46 |
| `HomeCommand.lambda` | `src/Vega.Modules.Navigation/HomeCommand.cs:41` | 4 | 2 | 1 |
| `HomeCommand.lambda` | `src/Vega.Modules.Navigation/HomeCommand.cs:47` | 1 | 0 | 1 |
| `HomeCommand.lambda` | `src/Vega.Modules.Navigation/HomeCommand.cs:48` | 1 | 0 | 1 |
| `HomeCommand.lambda` | `src/Vega.Modules.Navigation/HomeCommand.cs:51` | 1 | 0 | 1 |
| `HomeCommand.lambda` | `src/Vega.Modules.Navigation/HomeCommand.cs:57` | 1 | 0 | 1 |
| `HomeCommand.lambda` | `src/Vega.Modules.Navigation/HomeCommand.cs:60` | 1 | 0 | 1 |
| `HomeCommand.ResolveAuthenticatedPlayer` | `src/Vega.Modules.Navigation/HomeCommand.cs:72` | 9 | 2 | 11 |
| `HomeCommand.lambda` | `src/Vega.Modules.Navigation/HomeCommand.cs:78` | 3 | 1 | 1 |
| `HomeCommand.SetHomeAsync` | `src/Vega.Modules.Navigation/HomeCommand.cs:84` | 12 | 2 | 16 |
| `HomeCommand.lambda` | `src/Vega.Modules.Navigation/HomeCommand.cs:90` | 1 | 0 | 1 |
| `HomeCommand.lambda` | `src/Vega.Modules.Navigation/HomeCommand.cs:92` | 1 | 0 | 1 |
| `HomeCommand.Usage` | `src/Vega.Modules.Navigation/HomeCommand.cs:101` | 1 | 0 | 2 |
| `HomeCommand.Missing` | `src/Vega.Modules.Navigation/HomeCommand.cs:103` | 1 | 0 | 1 |
| `HomeCommand.Success` | `src/Vega.Modules.Navigation/HomeCommand.cs:104` | 1 | 0 | 2 |
| `HomeCommand.Failure` | `src/Vega.Modules.Navigation/HomeCommand.cs:106` | 1 | 0 | 2 |
| `NavigationModule.Start` | `src/Vega.Modules.Navigation/NavigationModule.cs:28` | 6 | 2 | 24 |
| `NavigationModule.Dispose` | `src/Vega.Modules.Navigation/NavigationModule.cs:53` | 2 | 1 | 7 |
| `NavigationModule.Register` | `src/Vega.Modules.Navigation/NavigationModule.cs:61` | 1 | 0 | 3 |
| `NavigationModule.SpawnAsync` | `src/Vega.Modules.Navigation/NavigationModule.cs:65` | 3 | 1 | 6 |
| `NavigationModule.HomeAsync` | `src/Vega.Modules.Navigation/NavigationModule.cs:72` | 3 | 1 | 6 |
| `NavigationModule.TeleportNpcAsync` | `src/Vega.Modules.Navigation/NavigationModule.cs:79` | 7 | 2 | 14 |
| `NavigationModule.lambda` | `src/Vega.Modules.Navigation/NavigationModule.cs:86` | 1 | 0 | 1 |
| `NavigationModule.lambda` | `src/Vega.Modules.Navigation/NavigationModule.cs:87` | 1 | 0 | 1 |
| `NavigationModule.lambda` | `src/Vega.Modules.Navigation/NavigationModule.cs:88` | 1 | 0 | 1 |
| `NavigationModule.TeleportAsync` | `src/Vega.Modules.Navigation/NavigationModule.cs:94` | 9 | 2 | 14 |
| `NavigationModule.TeleportHereAsync` | `src/Vega.Modules.Navigation/NavigationModule.cs:109` | 4 | 1 | 7 |
| `NavigationModule.MoveAsync` | `src/Vega.Modules.Navigation/NavigationModule.cs:117` | 7 | 2 | 10 |
| `NavigationModule.TeleportPositionAsync` | `src/Vega.Modules.Navigation/NavigationModule.cs:128` | 5 | 2 | 10 |
| `NavigationModule.PositionAsync` | `src/Vega.Modules.Navigation/NavigationModule.cs:139` | 5 | 2 | 10 |
| `NavigationModule.ToggleAsync` | `src/Vega.Modules.Navigation/NavigationModule.cs:150` | 6 | 1 | 14 |
| `NavigationModule.WarpAsync` | `src/Vega.Modules.Navigation/NavigationModule.cs:165` | 28 | 3 | 40 |
| `NavigationModule.ListWarpsAsync` | `src/Vega.Modules.Navigation/NavigationModule.cs:206` | 6 | 2 | 11 |
| `NavigationModule.lambda` | `src/Vega.Modules.Navigation/NavigationModule.cs:210` | 1 | 0 | 1 |
| `NavigationModule.lambda` | `src/Vega.Modules.Navigation/NavigationModule.cs:211` | 1 | 0 | 1 |
| `NavigationModule.lambda` | `src/Vega.Modules.Navigation/NavigationModule.cs:215` | 1 | 0 | 1 |
| `NavigationModule.TeleportWarpAsync` | `src/Vega.Modules.Navigation/NavigationModule.cs:218` | 15 | 3 | 25 |
| `NavigationModule.lambda` | `src/Vega.Modules.Navigation/NavigationModule.cs:232` | 1 | 0 | 2 |
| `NavigationModule.NotifyWarpTargetAsync` | `src/Vega.Modules.Navigation/NavigationModule.cs:244` | 7 | 2 | 16 |
| `NavigationModule.WarpMutation` | `src/Vega.Modules.Navigation/NavigationModule.cs:261` | 2 | 1 | 3 |
| `NavigationModule.Self` | `src/Vega.Modules.Navigation/NavigationModule.cs:265` | 5 | 2 | 3 |
| `NavigationModule.IsBlocked` | `src/Vega.Modules.Navigation/NavigationModule.cs:269` | 2 | 1 | 4 |
| `NavigationModule.OnClosed` | `src/Vega.Modules.Navigation/NavigationModule.cs:274` | 1 | 0 | 4 |
| `NavigationModule.TryResolve` | `src/Vega.Modules.Navigation/NavigationModule.cs:279` | 6 | 2 | 15 |
| `NavigationModule.lambda` | `src/Vega.Modules.Navigation/NavigationModule.cs:284` | 2 | 1 | 1 |
| `NavigationModule.lambda` | `src/Vega.Modules.Navigation/NavigationModule.cs:285` | 1 | 0 | 1 |
| `NavigationModule.lambda` | `src/Vega.Modules.Navigation/NavigationModule.cs:287` | 3 | 1 | 3 |
| `NavigationModule.lambda` | `src/Vega.Modules.Navigation/NavigationModule.cs:291` | 1 | 0 | 1 |
| `NavigationModule.Result` | `src/Vega.Modules.Navigation/NavigationModule.cs:295` | 3 | 2 | 3 |
| `NavigationModule.Missing` | `src/Vega.Modules.Navigation/NavigationModule.cs:299` | 1 | 0 | 1 |
| `NavigationModule.Failure` | `src/Vega.Modules.Navigation/NavigationModule.cs:300` | 1 | 0 | 2 |
| `NavigationModule.Forbidden` | `src/Vega.Modules.Navigation/NavigationModule.cs:302` | 1 | 0 | 2 |
| `NavigationModule.Usage` | `src/Vega.Modules.Navigation/NavigationModule.cs:304` | 1 | 0 | 2 |
| `NavigationModule.Handler.ExecuteAsync` | `src/Vega.Modules.Navigation/NavigationModule.cs:310` | 1 | 0 | 1 |
| `FakeProviderCommand.ExecuteAsync` | `src/Vega.Modules.Scenes/FakeProviderCommand.cs:17` | 4 | 2 | 7 |
| `FakeProviderCommand.lambda` | `src/Vega.Modules.Scenes/FakeProviderCommand.cs:22` | 1 | 0 | 1 |
| `FakeProviderCommand.DispatchAsync` | `src/Vega.Modules.Scenes/FakeProviderCommand.cs:25` | 5 | 2 | 16 |
| `FakeProviderCommand.lambda` | `src/Vega.Modules.Scenes/FakeProviderCommand.cs:30` | 3 | 2 | 9 |
| `FakeProviderCommand.ImportAsync` | `src/Vega.Modules.Scenes/FakeProviderCommand.cs:42` | 14 | 2 | 44 |
| `FakeProviderCommand.lambda` | `src/Vega.Modules.Scenes/FakeProviderCommand.cs:53` | 1 | 0 | 8 |
| `FakeProviderCommand.lambda` | `src/Vega.Modules.Scenes/FakeProviderCommand.cs:61` | 8 | 3 | 17 |
| `FakeProviderCommand.ExecuteSafely` | `src/Vega.Modules.Scenes/FakeProviderCommand.cs:87` | 4 | 1 | 7 |
| `FakeProviderCommand.Execute` | `src/Vega.Modules.Scenes/FakeProviderCommand.cs:95` | 26 | 2 | 24 |
| `FakeProviderCommand.ListProviders` | `src/Vega.Modules.Scenes/FakeProviderCommand.cs:120` | 6 | 2 | 10 |
| `FakeProviderCommand.CreateProvider` | `src/Vega.Modules.Scenes/FakeProviderCommand.cs:131` | 22 | 3 | 20 |
| `FakeProviderCommand.Describe` | `src/Vega.Modules.Scenes/FakeProviderCommand.cs:152` | 2 | 1 | 2 |
| `FakeProviderCommand.Info` | `src/Vega.Modules.Scenes/FakeProviderCommand.cs:155` | 1 | 0 | 1 |
| `FakeProviderCommand.Failure` | `src/Vega.Modules.Scenes/FakeProviderCommand.cs:156` | 1 | 0 | 1 |
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
| `RestAdministration.WriteSandboxesAsync` | `src/Vega.Operations/Actions/RestAdministration.cs:207` | 19 | 3 | 31 |
| `RestAdministration.CreateSandboxAsync` | `src/Vega.Operations/Actions/RestAdministration.cs:239` | 13 | 2 | 14 |
| `RegionObservations.RegionObservations(ctor)` | `src/Vega.Regions/RegionObservations.cs:31` | 3 | 1 | 15 |
| `RegionObservations.ObserveChange` | `src/Vega.Regions/RegionObservations.cs:47` | 5 | 2 | 9 |
| `RegionObservations.lambda` | `src/Vega.Regions/RegionObservations.cs:51` | 2 | 1 | 4 |
| `RegionObservations.ObserveTick` | `src/Vega.Regions/RegionObservations.cs:57` | 9 | 2 | 27 |
| `RegionObservations.RefreshRegionSnapshot` | `src/Vega.Regions/RegionObservations.cs:85` | 2 | 1 | 15 |
| `RegionObservations.lambda` | `src/Vega.Regions/RegionObservations.cs:90` | 1 | 0 | 1 |
| `RegionObservations.lambda` | `src/Vega.Regions/RegionObservations.cs:90` | 1 | 0 | 6 |
| `RegionObservations.ObservePlayer` | `src/Vega.Regions/RegionObservations.cs:101` | 28 | 3 | 41 |
| `RegionObservations.ContainsRegion` | `src/Vega.Regions/RegionObservations.cs:143` | 3 | 2 | 5 |
| `RegionObservations.ObserveClose` | `src/Vega.Regions/RegionObservations.cs:149` | 2 | 1 | 8 |
| `RegionObservations.lambda` | `src/Vega.Regions/RegionObservations.cs:152` | 2 | 1 | 4 |
| `RegionObservations.LeaveSession` | `src/Vega.Regions/RegionObservations.cs:158` | 3 | 1 | 6 |
| `RegionObservations.Dispose` | `src/Vega.Regions/RegionObservations.cs:165` | 3 | 1 | 7 |
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
| `SandboxControlService.SandboxControlService(ctor)` | `src/Vega.Sandboxes/SandboxControlService.cs:30` | 4 | 1 | 31 |
| `SandboxControlService.lambda` | `src/Vega.Sandboxes/SandboxControlService.cs:55` | 1 | 0 | 1 |
| `SandboxControlService.lambda` | `src/Vega.Sandboxes/SandboxControlService.cs:56` | 1 | 0 | 1 |
| `SandboxControlService.Snapshot` | `src/Vega.Sandboxes/SandboxControlService.cs:62` | 1 | 0 | 10 |
| `SandboxControlService.lambda` | `src/Vega.Sandboxes/SandboxControlService.cs:67` | 1 | 0 | 1 |
| `SandboxControlService.CreateAsync` | `src/Vega.Sandboxes/SandboxControlService.cs:73` | 20 | 4 | 80 |
| `SandboxControlService.lambda` | `src/Vega.Sandboxes/SandboxControlService.cs:99` | 1 | 0 | 1 |
| `SandboxControlService.DescribeExistingCreation` | `src/Vega.Sandboxes/SandboxControlService.cs:154` | 18 | 2 | 20 |
| `SandboxControlService.CheckSchematicCreation` | `src/Vega.Sandboxes/SandboxControlService.cs:175` | 5 | 2 | 12 |
| `SandboxControlService.lambda` | `src/Vega.Sandboxes/SandboxControlService.cs:182` | 1 | 0 | 1 |
| `SandboxControlService.CreateDescriptor` | `src/Vega.Sandboxes/SandboxControlService.cs:188` | 4 | 1 | 25 |
| `SandboxControlService.StartAsync` | `src/Vega.Sandboxes/SandboxControlService.cs:214` | 1 | 0 | 15 |
| `SandboxControlService.StopAsync` | `src/Vega.Sandboxes/SandboxControlService.cs:230` | 1 | 0 | 15 |
| `SandboxControlService.RestartAsync` | `src/Vega.Sandboxes/SandboxControlService.cs:246` | 2 | 1 | 18 |
| `SandboxControlService.ResetAsync` | `src/Vega.Sandboxes/SandboxControlService.cs:265` | 1 | 0 | 4 |
| `SandboxControlService.ResetConfirmedAsync` | `src/Vega.Sandboxes/SandboxControlService.cs:270` | 1 | 0 | 3 |
| `SandboxControlService.ResetCoreAsync` | `src/Vega.Sandboxes/SandboxControlService.cs:274` | 13 | 2 | 47 |
| `SandboxControlService.DestroyAsync` | `src/Vega.Sandboxes/SandboxControlService.cs:322` | 1 | 0 | 3 |
| `SandboxControlService.DestroyConfirmedAsync` | `src/Vega.Sandboxes/SandboxControlService.cs:326` | 1 | 0 | 2 |
| `SandboxControlService.DestroyCoreAsync` | `src/Vega.Sandboxes/SandboxControlService.cs:329` | 9 | 3 | 35 |
| `SandboxControlService.Dispose` | `src/Vega.Sandboxes/SandboxControlService.cs:365` | 5 | 2 | 31 |
| `SandboxControlService.lambda` | `src/Vega.Sandboxes/SandboxControlService.cs:372` | 1 | 0 | 1 |
| `SandboxControlService.lambda` | `src/Vega.Sandboxes/SandboxControlService.cs:373` | 1 | 0 | 1 |
| `SandboxControlService.StartCoreAsync` | `src/Vega.Sandboxes/SandboxControlService.cs:397` | 16 | 3 | 94 |
| `SandboxControlService.lambda` | `src/Vega.Sandboxes/SandboxControlService.cs:408` | 1 | 0 | 1 |
| `SandboxControlService.StopCoreAsync` | `src/Vega.Sandboxes/SandboxControlService.cs:492` | 10 | 2 | 45 |
| `SandboxControlService.Launch` | `src/Vega.Sandboxes/SandboxControlService.cs:538` | 5 | 1 | 49 |
| `SandboxControlService.lambda` | `src/Vega.Sandboxes/SandboxControlService.cs:580` | 1 | 0 | 1 |
| `SandboxControlService.lambda` | `src/Vega.Sandboxes/SandboxControlService.cs:581` | 1 | 0 | 1 |
| `SandboxControlService.MonitorAsync` | `src/Vega.Sandboxes/SandboxControlService.cs:588` | 21 | 6 | 90 |
| `SandboxControlService.ReconcileExistingProcesses` | `src/Vega.Sandboxes/SandboxControlService.cs:679` | 10 | 3 | 31 |
| `SandboxControlService.ReportOrphanedInstanceDirectories` | `src/Vega.Sandboxes/SandboxControlService.cs:711` | 4 | 2 | 16 |
| `SandboxControlService.lambda` | `src/Vega.Sandboxes/SandboxControlService.cs:714` | 1 | 0 | 1 |
| `SandboxControlService.TryRefreshHeartbeat` | `src/Vega.Sandboxes/SandboxControlService.cs:728` | 9 | 2 | 14 |
| `SandboxControlService.ApplyHeartbeat` | `src/Vega.Sandboxes/SandboxControlService.cs:743` | 4 | 1 | 11 |
| `SandboxControlService.SampleCpu` | `src/Vega.Sandboxes/SandboxControlService.cs:755` | 4 | 2 | 24 |
| `SandboxControlService.NextPort` | `src/Vega.Sandboxes/SandboxControlService.cs:780` | 3 | 2 | 10 |
| `SandboxControlService.lambda` | `src/Vega.Sandboxes/SandboxControlService.cs:782` | 1 | 0 | 1 |
| `SandboxControlService.ValidateCreate` | `src/Vega.Sandboxes/SandboxControlService.cs:791` | 13 | 2 | 13 |
| `SandboxControlService.TryGet` | `src/Vega.Sandboxes/SandboxControlService.cs:805` | 1 | 0 | 4 |
| `SandboxControlService.StatusPath` | `src/Vega.Sandboxes/SandboxControlService.cs:810` | 1 | 0 | 1 |
| `SandboxControlService.Snapshot` | `src/Vega.Sandboxes/SandboxControlService.cs:815` | 4 | 1 | 16 |
| `SandboxControlService.Applied` | `src/Vega.Sandboxes/SandboxControlService.cs:832` | 1 | 0 | 2 |
| `SandboxControlService.Already` | `src/Vega.Sandboxes/SandboxControlService.cs:835` | 1 | 0 | 2 |
| `SandboxControlService.NotFound` | `src/Vega.Sandboxes/SandboxControlService.cs:838` | 1 | 0 | 2 |
| `SandboxControlService.Rejected` | `src/Vega.Sandboxes/SandboxControlService.cs:841` | 1 | 0 | 2 |
| `SandboxControlService.Conflict` | `src/Vega.Sandboxes/SandboxControlService.cs:844` | 1 | 0 | 2 |
| `SandboxControlService.Failed` | `src/Vega.Sandboxes/SandboxControlService.cs:847` | 2 | 1 | 2 |
| `SandboxControlService.Log` | `src/Vega.Sandboxes/SandboxControlService.cs:850` | 1 | 0 | 4 |
| `SandboxControlService.LogBackend` | `src/Vega.Sandboxes/SandboxControlService.cs:855` | 3 | 2 | 7 |
| `SandboxControlService.TryTerminate` | `src/Vega.Sandboxes/SandboxControlService.cs:863` | 4 | 2 | 14 |
| `SandboxControlService.ThrowIfDisposed` | `src/Vega.Sandboxes/SandboxControlService.cs:878` | 1 | 0 | 1 |
| `NativeGameplayEventHooks.ResetPlayerStateObservations` | `src/Vega.Terraria/Hooking/NativeGameplayEventHooks.PlayerState.cs:14` | 1 | 0 | 6 |
| `NativeGameplayEventHooks.SamplePlayerStates` | `src/Vega.Terraria/Hooking/NativeGameplayEventHooks.PlayerState.cs:21` | 16 | 3 | 45 |
| `NativeGameplayEventHooks.PublishPlayerStateChanges` | `src/Vega.Terraria/Hooking/NativeGameplayEventHooks.PlayerState.cs:72` | 25 | 4 | 18 |
| `NativeGameplayEventHooks.ReadBuffTypes` | `src/Vega.Terraria/Hooking/NativeGameplayEventHooks.PlayerState.cs:91` | 9 | 3 | 16 |
| `TerrariaPlayerBoundary.ResynchronizeAreaAsync` | `src/Vega.Terraria/Players/TerrariaPlayerBoundary.Administration.cs:11` | 1 | 0 | 15 |
| `TerrariaPlayerBoundary.lambda` | `src/Vega.Terraria/Players/TerrariaPlayerBoundary.Administration.cs:12` | 8 | 2 | 14 |
| `TerrariaPlayerBoundary.ConfigureContentRestrictions` | `src/Vega.Terraria/Players/TerrariaPlayerBoundary.Administration.cs:30` | 1 | 0 | 1 |
| `TerrariaPlayerBoundary.TryGetItemType` | `src/Vega.Terraria/Players/TerrariaPlayerBoundary.Administration.cs:32` | 3 | 2 | 10 |
| `TerrariaPlayerBoundary.GiveAsync` | `src/Vega.Terraria/Players/TerrariaPlayerBoundary.Administration.cs:43` | 1 | 0 | 2 |
| `TerrariaPlayerBoundary.GiveCommandAsync` | `src/Vega.Terraria/Players/TerrariaPlayerBoundary.Administration.cs:46` | 1 | 0 | 2 |
| `TerrariaPlayerBoundary.GiveItemAsync` | `src/Vega.Terraria/Players/TerrariaPlayerBoundary.Administration.cs:49` | 1 | 0 | 22 |
| `TerrariaPlayerBoundary.lambda` | `src/Vega.Terraria/Players/TerrariaPlayerBoundary.Administration.cs:51` | 24 | 2 | 20 |
| `TerrariaPlayerBoundary.TryResolveGiveItemType` | `src/Vega.Terraria/Players/TerrariaPlayerBoundary.Administration.cs:72` | 5 | 2 | 12 |
| `TerrariaPlayerBoundary.lambda` | `src/Vega.Terraria/Players/TerrariaPlayerBoundary.Administration.cs:76` | 1 | 0 | 1 |
| `TerrariaPlayerBoundary.lambda` | `src/Vega.Terraria/Players/TerrariaPlayerBoundary.Administration.cs:77` | 1 | 0 | 1 |
| `TerrariaPlayerBoundary.lambda` | `src/Vega.Terraria/Players/TerrariaPlayerBoundary.Administration.cs:78` | 1 | 0 | 1 |
| `TerrariaPlayerBoundary.PrepareCommandDelivery` | `src/Vega.Terraria/Players/TerrariaPlayerBoundary.Administration.cs:85` | 12 | 2 | 9 |
| `TerrariaPlayerBoundary.lambda` | `src/Vega.Terraria/Players/TerrariaPlayerBoundary.Administration.cs:89` | 2 | 1 | 1 |
| `TerrariaPlayerBoundary.SpawnAndDeliverItem` | `src/Vega.Terraria/Players/TerrariaPlayerBoundary.Administration.cs:95` | 12 | 3 | 26 |
| `TerrariaPlayerBoundary.HealAsync` | `src/Vega.Terraria/Players/TerrariaPlayerBoundary.Administration.cs:122` | 1 | 0 | 10 |
| `TerrariaPlayerBoundary.lambda` | `src/Vega.Terraria/Players/TerrariaPlayerBoundary.Administration.cs:123` | 5 | 2 | 9 |
| `TerrariaPlayerBoundary.BuffAsync` | `src/Vega.Terraria/Players/TerrariaPlayerBoundary.Administration.cs:133` | 1 | 0 | 20 |
| `TerrariaPlayerBoundary.lambda` | `src/Vega.Terraria/Players/TerrariaPlayerBoundary.Administration.cs:134` | 12 | 2 | 19 |
| `TerrariaPlayerBoundary.lambda` | `src/Vega.Terraria/Players/TerrariaPlayerBoundary.Administration.cs:141` | 1 | 0 | 1 |
| `TerrariaPlayerBoundary.lambda` | `src/Vega.Terraria/Players/TerrariaPlayerBoundary.Administration.cs:142` | 1 | 0 | 1 |
| `TerrariaPlayerBoundary.lambda` | `src/Vega.Terraria/Players/TerrariaPlayerBoundary.Administration.cs:143` | 1 | 0 | 1 |
| `TerrariaPlayerBoundary.KillAsync` | `src/Vega.Terraria/Players/TerrariaPlayerBoundary.Administration.cs:156` | 1 | 0 | 9 |
| `TerrariaPlayerBoundary.lambda` | `src/Vega.Terraria/Players/TerrariaPlayerBoundary.Administration.cs:157` | 4 | 2 | 8 |
| `TerrariaPlayerBoundary.SlapAsync` | `src/Vega.Terraria/Players/TerrariaPlayerBoundary.Administration.cs:166` | 1 | 0 | 10 |
| `TerrariaPlayerBoundary.lambda` | `src/Vega.Terraria/Players/TerrariaPlayerBoundary.Administration.cs:167` | 6 | 2 | 9 |
| `TerrariaPlayerBoundary.RespawnAsync` | `src/Vega.Terraria/Players/TerrariaPlayerBoundary.Administration.cs:177` | 1 | 0 | 10 |
| `TerrariaPlayerBoundary.lambda` | `src/Vega.Terraria/Players/TerrariaPlayerBoundary.Administration.cs:178` | 4 | 1 | 9 |
| `TerrariaPlayerInventoryService.TransferVirtualChest` | `src/Vega.Terraria/Players/TerrariaPlayerInventoryService.VirtualChests.cs:10` | 1 | 0 | 2 |
| `TerrariaPlayerInventoryService.TransferVirtualChestCore` | `src/Vega.Terraria/Players/TerrariaPlayerInventoryService.VirtualChests.cs:13` | 23 | 2 | 41 |
| `TerrariaPlayerInventoryService.lambda` | `src/Vega.Terraria/Players/TerrariaPlayerInventoryService.VirtualChests.cs:37` | 1 | 0 | 1 |
| `TerrariaPlayerInventoryService.PrepareVirtualChestTransfer` | `src/Vega.Terraria/Players/TerrariaPlayerInventoryService.VirtualChests.cs:55` | 28 | 2 | 34 |
| `TerrariaPlayerInventoryService.lambda` | `src/Vega.Terraria/Players/TerrariaPlayerInventoryService.VirtualChests.cs:58` | 1 | 0 | 1 |
| `TerrariaPlayerInventoryService.lambda` | `src/Vega.Terraria/Players/TerrariaPlayerInventoryService.VirtualChests.cs:83` | 1 | 0 | 1 |
| `TerrariaSchematicObjects.Prepare` | `src/Vega.Terraria/Worlds/TerrariaSchematicObjects.cs:24` | 4 | 3 | 13 |
| `TerrariaSchematicObjects.Cover` | `src/Vega.Terraria/Worlds/TerrariaSchematicObjects.cs:40` | 16 | 4 | 18 |
| `TerrariaSchematicObjects.PrepareChests` | `src/Vega.Terraria/Worlds/TerrariaSchematicObjects.cs:59` | 15 | 3 | 21 |
| `TerrariaSchematicObjects.lambda` | `src/Vega.Terraria/Worlds/TerrariaSchematicObjects.cs:61` | 1 | 0 | 1 |
| `TerrariaSchematicObjects.lambda` | `src/Vega.Terraria/Worlds/TerrariaSchematicObjects.cs:63` | 1 | 0 | 1 |
| `TerrariaSchematicObjects.PrepareSigns` | `src/Vega.Terraria/Worlds/TerrariaSchematicObjects.cs:81` | 5 | 3 | 12 |
| `TerrariaSchematicObjects.PrepareEntities` | `src/Vega.Terraria/Worlds/TerrariaSchematicObjects.cs:94` | 26 | 4 | 36 |
| `TerrariaSchematicObjects.lambda` | `src/Vega.Terraria/Worlds/TerrariaSchematicObjects.cs:97` | 1 | 0 | 1 |
| `TerrariaSchematicObjects.PrepareWorldItems` | `src/Vega.Terraria/Worlds/TerrariaSchematicObjects.cs:131` | 13 | 3 | 16 |
| `TerrariaSchematicObjects.Apply` | `src/Vega.Terraria/Worlds/TerrariaSchematicObjects.cs:148` | 2 | 1 | 7 |
| `TerrariaSchematicObjects.ApplyChests` | `src/Vega.Terraria/Worlds/TerrariaSchematicObjects.cs:156` | 9 | 3 | 18 |
| `TerrariaSchematicObjects.lambda` | `src/Vega.Terraria/Worlds/TerrariaSchematicObjects.cs:164` | 2 | 1 | 1 |
| `TerrariaSchematicObjects.ApplySigns` | `src/Vega.Terraria/Worlds/TerrariaSchematicObjects.cs:175` | 6 | 3 | 12 |
| `TerrariaSchematicObjects.lambda` | `src/Vega.Terraria/Worlds/TerrariaSchematicObjects.cs:183` | 2 | 1 | 1 |
| `TerrariaSchematicObjects.ApplyEntities` | `src/Vega.Terraria/Worlds/TerrariaSchematicObjects.cs:188` | 18 | 3 | 43 |
| `TerrariaSchematicObjects.lambda` | `src/Vega.Terraria/Worlds/TerrariaSchematicObjects.cs:203` | 3 | 2 | 5 |
| `TerrariaSchematicObjects.IsFreeItemSlot` | `src/Vega.Terraria/Worlds/TerrariaSchematicObjects.cs:232` | 2 | 1 | 1 |
| `TerrariaSchematicObjects.PlaceWorldItem` | `src/Vega.Terraria/Worlds/TerrariaSchematicObjects.cs:234` | 11 | 2 | 37 |
| `TerrariaSchematicObjects.Track` | `src/Vega.Terraria/Worlds/TerrariaSchematicObjects.cs:244` | 7 | 2 | 16 |
| `TerrariaSchematicObjects.lambda` | `src/Vega.Terraria/Worlds/TerrariaSchematicObjects.cs:253` | 2 | 1 | 6 |
| `TerrariaSchematicObjects.Rollback` | `src/Vega.Terraria/Worlds/TerrariaSchematicObjects.cs:272` | 4 | 3 | 10 |
| `TerrariaSchematicObjects.Items` | `src/Vega.Terraria/Worlds/TerrariaSchematicObjects.cs:283` | 11 | 3 | 18 |
| `TerrariaSchematicObjects.CopyItems` | `src/Vega.Terraria/Worlds/TerrariaSchematicObjects.cs:302` | 2 | 1 | 5 |
| `TerrariaSchematicObjects.NeedsRegisteredObject` | `src/Vega.Terraria/Worlds/TerrariaSchematicObjects.cs:308` | 23 | 1 | 11 |
| `TerrariaSchematicPlacement.lambda` | `src/Vega.Terraria/Worlds/TerrariaSchematicPlacement.cs:13` | 1 | 0 | 2 |
| `TerrariaSchematicPlacement.Place` | `src/Vega.Terraria/Worlds/TerrariaSchematicPlacement.cs:16` | 26 | 4 | 55 |
| `TerrariaSchematicPlacement.lambda` | `src/Vega.Terraria/Worlds/TerrariaSchematicPlacement.cs:20` | 1 | 0 | 1 |
| `TerrariaSchematicPlacement.lambda` | `src/Vega.Terraria/Worlds/TerrariaSchematicPlacement.cs:24` | 1 | 0 | 1 |
| `TerrariaSchematicPlacement.Overlaps` | `src/Vega.Terraria/Worlds/TerrariaSchematicPlacement.cs:27` | 4 | 1 | 2 |
| `TerrariaSchematicPlacement.OverlapsPixel` | `src/Vega.Terraria/Worlds/TerrariaSchematicPlacement.cs:29` | 4 | 1 | 2 |
| `TerrariaSchematicPlacement.lambda` | `src/Vega.Terraria/Worlds/TerrariaSchematicPlacement.cs:33` | 2 | 1 | 1 |
| `TerrariaSchematicPlacement.lambda` | `src/Vega.Terraria/Worlds/TerrariaSchematicPlacement.cs:34` | 2 | 1 | 1 |
| `TerrariaSchematicPlacement.lambda` | `src/Vega.Terraria/Worlds/TerrariaSchematicPlacement.cs:35` | 1 | 0 | 1 |
| `TerrariaSchematicPlacement.lambda` | `src/Vega.Terraria/Worlds/TerrariaSchematicPlacement.cs:36` | 2 | 1 | 1 |
| `TerrariaSchematicPlacement.lambda` | `src/Vega.Terraria/Worlds/TerrariaSchematicPlacement.cs:37` | 2 | 1 | 1 |
| `TerrariaSchematicPlacement.lambda` | `src/Vega.Terraria/Worlds/TerrariaSchematicPlacement.cs:41` | 2 | 1 | 1 |
| `TerrariaSchematicPlacement.lambda` | `src/Vega.Terraria/Worlds/TerrariaSchematicPlacement.cs:42` | 1 | 0 | 1 |
| `TerrariaSchematicPlacement.lambda` | `src/Vega.Terraria/Worlds/TerrariaSchematicPlacement.cs:43` | 2 | 1 | 1 |
| `TerrariaSchematicPlacement.PlaceNpcs` | `src/Vega.Terraria/Worlds/TerrariaSchematicPlacement.cs:72` | 12 | 3 | 24 |
| `TerrariaSchematicPlacement.ApplyTiles` | `src/Vega.Terraria/Worlds/TerrariaSchematicPlacement.cs:97` | 3 | 2 | 6 |
| `TerrariaSchematicPlacement.Invalid` | `src/Vega.Terraria/Worlds/TerrariaSchematicPlacement.cs:104` | 1 | 0 | 1 |
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
| `VegaWorldImageReader.Validate` | `src/Vega.Terraria/Worlds/VegaWorldImageFormat.cs:78` | 16 | 2 | 78 |
| `VegaWorldImageReader.ValidateSourceMetadata` | `src/Vega.Terraria/Worlds/VegaWorldImageFormat.cs:157` | 5 | 1 | 27 |
| `VegaWorldImageReader.ValidateSections` | `src/Vega.Terraria/Worlds/VegaWorldImageFormat.cs:185` | 13 | 2 | 36 |
| `VegaWorldImageReader.ValidateSnapshotFlags` | `src/Vega.Terraria/Worlds/VegaWorldImageFormat.cs:222` | 5 | 2 | 16 |
