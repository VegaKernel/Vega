# Native TCP transport: audit and implementation design

Current production-cap comparison on TerrariaRuntime 1.1.5: the original 2048 callback-count ceiling exhausted the shared completion reservation budget at 200/255 synthetic local peers with 32 offered sends each per tick. The opt-in transport now defaults to `orderedCallbacksPerPass: 8192` (normalized to 1–8192), with the same cooperative 2 ms time budget, FIFO, generation checks and per-world memory/entry limits. All four raised-cap ABBA runs completed and verified exact bytes and callbacks. At 255 peers throughput was 617–621 MiB/s versus 168–169 MiB/s for the vanilla baseline; this is an isolated loopback send workload, not a world TPS or live-client result. Higher allocations and game-thread-drained completion latency remain tradeoffs. Vanilla stays the default. See [performance measurements](performance-2026-11-26.md).

Status: implemented experimental adapter and local loopback evidence. Vega keeps vanilla TCP by default; `config/network.json` with `useOrderedTcp: true` selects the adapter at startup. `NativeOutboundQueue`, `NativeTcpConnection`, `NativeTcpListener`, `NativeOutboundBudget` and `NativeTcpTransportHost` own FIFO sends, per-world shared budget, accepted socket lifecycle, receive-close completion and native game-thread send completions. Existing TerrariaRuntime 1.1.2 hooks suffice; no new patch is required. This is not a replacement gameplay or packet engine. The vanilla-client join, kick reason, SSC and representative performance gates are still open.

The ordered writer now drains already queued work without counting a semaphore operation for every request. A producer signals only an explicitly waiting writer, under the same queue lock; Complete shares that wake rule, abort cancels pending waits. The `--wake` ABBA benchmark retains the counted mode for measurement. Edge wake reduced allocations at 32 receivers in the first ABBA test, but the integrated repeat did not reproduce that allocation win. Both runs validated FIFO and byte/entry caps; the completion-pump cadence remains a separate optimization.

Candidate startup limits (internal, not tuned production sizing): 512 transport connections including pending acceptance; 16,384 outstanding sends and 32 MiB retained send rentals per connection; 131,072 outstanding sends and 256 MiB retained send rentals per world process; 1 MiB single send and 128 KiB receive scratch per read; 2,048 callbacks or 2 ms per native packet pass. Retained send budgets include in-flight and pending completions, not OS socket memory, read scratch or short-lived rejected rentals. Receive scratch is bounded independently by connection/read size. The existing native pending admission limit and native per-slot frame budget remain active. Adjacent coalescing (up to 16 requests / 32 KiB) has been implemented and measured, but remains disabled (`MaxBatchRequests=1`) after the A/B showed higher allocations. See [performance results](performance-2026-11-26.md).

## 1. What can be reused

| Source | Reusable mechanism | Adaptation required for native Terraria |
| --- | --- | --- |
| `../../TerraRuntime/src/TerraRuntime.Network/BoundedOutboundQueue.cs` (lines 48–121, 124–209) | Bounded channel, one reader, serialized nonblocking producers, independent byte/entry limits, atomic batch admission | Queue entries need to own copied buffers and callback state. Its counters release on dequeue; native transport must also account for in-flight buffers and completed-but-undelivered callbacks. |
| `../../TerraRuntime/src/TerraRuntime.Network/TerrariaOutboundFrameWriter.cs` (lines 63–110) | One ordered writer, adjacent small-frame batching, pooled batch buffer, cancellation | Do not parse or reorder protocol frames here. A native `AsyncSend` request can contain multiple frames. Every successful send request retains its original completion callback and state. |
| `../../TerraRuntime/src/TerraRuntime.Network/TerrariaConnectionOutboundQueue.cs` (lines 42–85) | Per-connection overflow signal; ordinary ordered state is not silently dropped | Do not port opportunistic/drop policy into raw `ISocket`; only a higher-level producer that knows the packet is replaceable can make that decision. Overflow closes the originating connection rather than corrupting its byte stream. |
| `../../TerraRuntime/src/TerraRuntime.Network/TerrariaSocketConnection.cs` (lines 19–26, 108–158, 199–214) | Cancellation of I/O, bounded drain, socket disposal fallback, explicit stop reasons | Native `RemoteClient.Reset` already owns slot reuse. Its `Close()` must not wait on an outbound drain. Keep graceful application shutdown distinct from immediate socket close. |
| `../../TerraRuntime/src/TerraRuntime.Application/ConnectionOutboundQueueSizing.cs`; `../../TerraRuntime/docs/en/outbound-queue-sizing-evidence.md` | Structural bootstrap floor and measured high-water sizing with 25% headroom | Recalculate the floor using actual native Terraria joins; TerraRuntime's own protocol baselines cannot establish a safe queue capacity for a different server implementation. |

Do not import TerraRuntime's `PipeReader` frame pump, packet command decoder or world authority. Native `RemoteClient.ServerReadCallBack`, `NetMessage.ReceiveBytes`, `NetMessage.CheckBytes` and the existing Vega frame budget continue owning input. This keeps Terraria's packet order, section state and gameplay on its world thread.

## 2. Native integration audit

The following was checked against the pinned vanilla Windows 1.4.6.1 assembly and the generated TerrariaRuntime 1.1.2 assembly using Mono.Cecil. It was an inspection of lifecycle and transport contracts, not a client/network stress test.

- `../../TerrariaRuntime/src/TerrariaRuntime.Scripts/Mods/HookClientSockets.Server.cs:85` exposes `Hooks.Netplay.ServerTcpListenerFactory`. `InvokeCreateServerTcpListener()` falls back to the legacy factory and vanilla `TcpSocket`. It replaces only the dedicated TCP listener; leave the legacy and other transport paths untouched.
- `../../TerrariaRuntime/src/TerrariaRuntime.Scripts/Mods/HookClientSendBytes.Server.cs` exposes `Hooks.NetMessage.SendBytes` for selected `NetMessage` sends. Generated `NetMessage.terraria_runtime_hook_SendPacket` calls this hook; at least one generated `SendData` wrapper path still calls `ISocket.AsyncSend` directly. `InvokeSendBytes` uses the original arguments unless the hook cancels. Its mutable event fields are not a complete replacement transport contract.
- Vega itself has direct `ISocket.AsyncSend` calls: `../src/Vega.Terraria/Scenes/TerrariaSceneRenderSink.cs:174`, `../src/Vega.Terraria/Items/TerrariaCustomItemDelivery.cs:69`, `../src/Vega.Terraria/Networking/TerrariaSandboxPlayerTransfer.cs:126`. Consequently, putting a FIFO only in `SendBytes` would leave direct sends outside it. The listener must produce an accepted **socket adapter**; every subsequent call through that socket uses the same FIFO.
- `../src/Vega.Terraria/Hooking/NativeNetworkMetricsHooks.cs:73` records offered bytes from `SendBytes`; these are not actual wire-delivered bytes and do not cover every direct send. Keep this distinction explicit in metrics.
- `../src/Vega.Terraria/Hooking/NativeNetworkBudgetHooks.cs` already bounds pending admission and frames per native pass. The replacement transport must still call the native accepted-socket callback, preserving those budgets, admission policy and lifecycle notifications.

### Native socket contract observations

| Native method | Observed behavior and required compatibility |
| --- | --- |
| `ISocket.StartListening(SocketConnectionAccepted)` | Vanilla reads the configured `-ip` (default `IPAddress.Any`) and `Netplay.ListenPort`; starts the listener, returns `false` if binding fails; accepted connections are handed to the callback as `ISocket`. |
| `ISocket.AsyncSend(byte[], int offset, int size, SocketSendCallback, object state)` | Vanilla passes callback + original state to the asynchronous completion. A successful write calls `callback(state)`; a failed write closes the connection without a success callback. The supplied array is not guaranteed immutable. The new adapter must honor the given offset and size on every platform. |
| `RemoteClient.ServerWriteCallBack(object)` | Decrements `NetMessage.buffer[client.Id].spamCount`, a slot-indexed native counter. `NetMessage.SendPacket` increments it before initiating its send. Do not invoke this callback for a new occupant of a reused slot, invent a callback for every protocol frame, or add another increment inside the socket adapter. Existing Vega direct-send call sites deserve their own counter-ownership review before enabling the adapter. |
| `ISocket.AsyncReceive(byte[], int offset, int size, SocketReceiveCallback, object state)` | Native TCP calls `callback(state, count)` when the read completes, or closes and supplies zero upon failure. `RemoteClient.ServerReadCallBack` appends received bytes on the transport completion thread and clears `_isReading`. Do not call `MessageBuffer.GetData` in the transport. |
| `RemoteClient.Reset()` | Closes the old socket and waits for the outstanding native read to complete before clearing/reusing connection state. Never suppress a pending read completion solely because close has begun; otherwise native reset can wait indefinitely. Old send-completion callbacks must instead be retired with their socket generation. |
| `ISocket.GetRemoteAddress()` | A TCP endpoint is represented by native `Terraria.Net.TcpAddress(IPAddress, int)`. Preserve it for metadata, IP bans and trusted-forwarder handling. |

Use `Hooks.Netplay.ServerConnectionActivated`, `ServerPlayerEnteringWorld`, `ServerConnectionClosing` and `ServerConnectionClosed` from 1.1.2. Closing is synchronous before native reset removes the old socket and character. Entering-world is useful for size/phase telemetry, not a transport admission decision. Identify a connection by the adapter reference and monotonically assigned generation, never by a reusable slot alone.

## 3. Concrete adapter shape

Keep these implementation types internal to `Vega.Terraria/Networking`; no additional generic transport abstraction or dependency is needed.

- `NativeTcpTransportOptions`: internal maximum outstanding send requests, retained bytes per connection/process, maximum send/receive request and completion-drain budget. `ServerNetworkOptions.UseOrderedTcp` selects the implementation. Batching, production sizing and detailed telemetry remain separate measured improvements; default mode remains vanilla until live compatibility tests pass.
- `OrderedTcpListener : ISocket`: binds `IPAddress` and `Netplay.ListenPort` using native-equivalent selection; owns an asynchronous accept loop, `CancellationTokenSource` and listener socket. Accepted `Socket` objects become `OrderedTcpConnection` objects and go through the same native `SocketConnectionAccepted` callback that calls `Netplay.OnConnectionAccepted`. If stop wins the accept race, dispose the accepted socket instead of publishing it. `StopListening` stops accepts, not already-owned connections. The listener's connection-only methods are not used by the dedicated path; fail clearly rather than mixing listener and connection state. No custom listener for client, LAN discovery, Steam or GNS.
- `OrderedTcpConnection : ISocket`: owns the accepted socket, endpoint, one outbound FIFO and writer, one outstanding read, cancellation, and native binding `(slot, generation)` assigned on activation. Use `socket.NoDelay = true`, native `TcpAddress`, a closed flag, and normal socket state/EOF checks; `Socket.Connected` alone is not a live-peer test. Never read or mutate Terraria player/world state from send workers.
- `OutboundSend` (internal struct/record): `{ sequence, generation, byte[] ownedBuffer, length, SocketSendCallback? callback, object? state, enqueuedTimestamp }`. Entries represent **send requests**, not parsed protocol frames. Buffer lifetime is independent from the original caller's array. Completion records retain callback/state/sequence/generation, not the payload.
- `NativeSendCompletionPump`: per-connection bounded FIFO of completed native sends drained on the game thread before the next native send/packet pass (or at the beginning of `Main.Update` and in the idle update hook when required). Never replace `ServerPacketPass` just to drain completions; its contract replaces the whole native pass. Reserve completion capacity when admitting sends, so a full generic dispatcher cannot silently discard successful completions. Round-robin active connections under a bounded callback/time budget; check identity again immediately before callback invocation.
- `NativeTransportHost`: installs/restores `ServerTcpListenerFactory`, binds adapter instances from lifecycle events, owns listener/connection stop and exposes aggregate telemetry. Use process-local pools/budgets/registry, separate per world worker. Register and release lifecycle subscriptions together; capture `slot` and `generation` on activation. Closing invalidates send-completion delivery for that generation. Closed removes only the matching socket, never a replacement.

### Send ownership and ordering

1. Validate `offset` and `size` against the supplied array. A producer obtains its connection's short publish gate, confirms it is open, reserves an outstanding request/completion credit and a byte budget, rents an owned buffer, and synchronously copies the exact caller slice before returning from `AsyncSend`. Include the actual rented buffer capacity in retained-memory accounting; return it immediately on rejection. Avoid reusing `ReadOnlyMemory` over an array owned by Terraria.
2. Publication through one FIFO is the order linearization point for all producers using that connection. Assign the sequence and publish together. `TryEnqueue` must not wait for the writer or for queue capacity. On overflow, abort that peer and release only its pending resources. Do not send later entries after dropping an earlier ordered request.
3. A single asynchronous writer dequeues in order. It may concatenate only adjacent requests, without changing bytes or changing callback cardinality. Start with TerraRuntime's bounded small-batch envelope (up to 16 requests, 32 KiB contiguous bytes, individual entries up to 1 KiB); treat these as experiment defaults, not proven production limits. Charge the extra batch buffer to retained-memory budgets. For `Socket.SendAsync`, handle partial successful writes until the complete requested bytes are sent. A zero/failed write retires the connection.
4. After all bytes of a request are written, return its rented array exactly once, update delivered-byte/latency telemetry, and publish completion metadata in FIFO order. Keep the outstanding request credit until that completion is delivered or retired. On partial failure/close, release every buffer and retire unsent completions; no success callback after the generation is closed. One completed request maps to at most one original callback with its exact original `state`, even when bytes are batched.
5. Deliver native send callbacks on the authoritative world thread after validating `(slot, generation, socket)`, preventing a concurrent reset from corrupting another slot's counter. This changes completion timing relative to native TCP, so prove that the next native spam-accounting pass observes timely completions. If transport credits or callback-drain capacity cannot sustain ordinary joins, do not enable this adapter. Never wait on a callback from the writer or on a socket worker from the world thread.

Both queued and in-flight buffers count against the retained byte cap; completion records count against outstanding entry limits. Reserve process-wide credits in addition to per-connection credits, so a large player limit does not multiply memory without bound. A limit rejection must affect only the connection whose reservation failed. Do not make per-tick traffic from healthy connections block behind another peer's `_write` gate. Pool returns occur independently of native success callbacks, including cancellation and exceptions.

### Receive and reset ownership

Leave the vanilla `AsyncReceive` cadence intact. Prefer a per-read owned scratch buffer for the candidate adapter: perform `ReceiveAsync` asynchronously into it, then copy completed bytes to the exact supplied receive window and invoke its native callback once. The scratch does not belong to Terraria. Close and the worker compete for a single completion claim for that read request. If close wins, invoke its callback with zero so native `_isReading` can clear; the remaining underlying I/O can only touch the scratch buffer, which is returned after the actual read finishes. If the worker wins, allow that claimed callback to finish before native reset reuses state; close must not issue a second completion. Do not dispatch this receive completion to a later main-thread tick, because reset may already be waiting for it on that world thread.

`Close()` is idempotent, nonblocking with respect to outbound drains, and retires the generation's sends. Cancel and dispose/shutdown the socket to unblock pending I/O. Release the pending receive as above. No tasks, queues or callbacks may migrate to a different generation. For coordinated host shutdown, a separate optional background drain can be time-bounded (TerraRuntime uses two seconds); afterward close, cancel and await transport tasks off the world thread. Preserve the native kick/shutdown reason through a live-client acceptance test rather than making `Close()` block to flush it. No unbounded peer drain or wait is permitted.

## 4. Correctness/performance acceptance gates

Start with tests in an isolated adapter project or Vega runtime tests using a controlled fake byte sink and ordinary serialized packets. These are local correctness tests, not adversarial traffic or remote load tests.

| Gate | Expected result |
| --- | --- |
| Queue identity | Publish payloads A/B/C via ordinary sends from two producers. Capture the actual publish order; sink bytes equal that exact concatenation and callbacks arrive in that order, with their original state. |
| Buffer ownership | Send a nonzero-offset slice of an ordinary byte array, immediately reuse the caller array, then release the fake writer. Sink receives the original slice, not the later contents. Track rents/returns on success, write failure, concurrent close and cancellation. |
| Batch and partial writes | For controlled partial successful writes, total output equals the original request sequence. Batching does not alter callback count or order. Simulated transport errors stop that connection and dispose all retained buffers. |
| Slow-peer isolation | Stall one fake sink until its configured outstanding request/byte bound is reached; ordinary traffic to a second sink still completes. Only the stalled connection closes; its retained bytes and completion credits return to zero. |
| Native callback generation | Close the old synthetic socket, reuse its slot with a new synthetic socket, then deliver a delayed old send completion. Its callback does not change the new slot's `spamCount`; new-generation completions still drain. |
| Native read completion | Exercise close while one read is pending, both orderings of worker/close completion claim, and EOF. Exactly one native receive callback; zero completion releases the pending read; no stale writes to the caller array. Native reset completes. |
| Lifecycle | Start/stop listener idempotently; ordinary accept/activation/close notifications preserve socket identity; no surviving accept/read/write tasks or retained memory after stopping. Test two independent world-worker listener instances, each with its own budgets. |
| Native compatibility | On local vanilla Terraria 1.4.6.1: normal join/world sync, character login, movement, item delivery, scene updates, sandbox transfer, kick with reason, same-slot reconnect, and native save/exit. Compare packet delivery behavior to vanilla, including idle/no-player periods. Verify Windows and Linux. |

Benchmark vanilla vs adapter with the same worlds, clients and join/steady-state actions. Record attempted sends vs accepted requests, wire bytes written, writer call count, outstanding/peak request and retained-byte budgets, oldest queued age (p50/p95/p99), send/callback delay, queue rejection and stop reasons, process allocation and GC rates, tick p50/p95/p99. A low peak never proves that a smaller limit can accommodate native bootstrap. Large join/world sync bursts must be measured and structurally bounded first. Use TerraRuntime's measured envelope rule `ceil(peak / 0.75)` independently for request count and retained bytes, never below the established native structural floor. The process-wide cap must also hold at the configured player cap. Keep mode opt-in until correctness and representative performance gates pass.

## 5. Local measurement already performed

An isolated .NET 11 Release diagnostic linked the existing unmodified `BoundedOutboundQueue`, `OutboundFrame`, `OutboundQueueOptions`, and `OutboundEnqueueResult` source files from TerraRuntime. One thread performed enqueue/read pairs on a 64-entry queue (`maxBytes = 16 KiB`), one million requests per round, each with a 256-byte payload. Case A reused immutable payload memory. Case B rented a 256-byte buffer, copied the source, queued/read it and returned the rental. Both cases were warmed first; the first measured round was affected by tiered compilation. Later rounds:

| Case | Round 2 | Round 3 | Current-thread allocation per request (warmed) |
| --- | ---: | ---: | ---: |
| Existing immutable queue | 56.63 ms | 55.04 ms | ~0.0001 B |
| Existing queue + pool copy | 74.19 ms | 73.73 ms | ~0.0001 B |

This confirms only that a synchronous copy into a reusable pool buffer can be measured independently and adds no meaningful managed allocation in this particular warmed sequential microbenchmark. It does not include simultaneous producer/consumer contention, socket I/O, native callbacks, large world joins, pool retention, or full Vega execution. It is not a throughput or latency claim for the proposed network adapter. No production source or defaults were changed by this experiment.

### Completion pump: sparse active-slot A/B (2026-11-26)

The original round-robin pump revisited all 256 native slots between callbacks. The candidate snapshots the occupied slot indices once per pass into a small stack-allocated span, in round-robin order, and revisits only those candidates. It still reads the current connection and validates its generation and native socket identity before every callback. New slots published after the snapshot become eligible at the next pass. The same callback-count and time budgets apply; no callbacks move onto worker threads. The original scan remains available internally for regression comparison.

A .NET 11 Release loopback ABBA benchmark compared the two pump implementations using TerrariaRuntime 1.1.3, edge-triggered queue wake enabled, batching disabled, 15 warm-up and 120 measured nominal 60 Hz ticks, 32 sends per peer per tick, and a fixed 64/256/1024/4096-byte payload mix. Each run validated byte contents and counts plus total callback count. Reproduce with `dotnet run --project benchmarks/Vega.NativeTcp.Benchmarks -c Release -- --pump`. Raw output from the source-linked temporary benchmark is in `../artifacts/performance-2026-11-26/pump-results.txt`.

| Local peers | Original pump p95 | Active-slot pump p95 | Fixed delivered throughput |
| ---: | ---: | ---: | ---: |
| 1 | 0.271–0.300 ms | 0.009–0.011 ms | 2.48–2.49 MiB/s |
| 8 | 0.290–0.315 ms | 0.036–0.048 ms | 19.84–19.87 MiB/s |
| 32 | 0.407–0.409 ms | 0.191–0.193 ms | 79.28–79.64 MiB/s |

The measured duration includes the same per-pass queue sampling overhead in both variants. All 12 runs completed; the independent transport tests also check sparse-slot fairness when only one callback fits per pass. Enqueue-to-callback p95 remained approximately 29–30 ms on this Windows timer-paced harness: the drain's computational cost improved, not the callback delivery cadence. Throughput was paced by the offered rate rather than a maximum-throughput measurement. CPU samples include local reader tasks. These are not measurements of full native Terraria world ticks or gameplay. Active-slot scanning is enabled for the opt-in Ordered TCP host; vanilla TCP is still the server default.

### Send cancellation A/B (2026-11-26)

The accepted-socket adapter always retires the queue and disposes its own socket on
close (including overflow). A bounded experiment compared passing the queue's
cancellation token into every `Socket.SendAsync` call versus relying on disposal
to interrupt in-flight socket I/O. In both variants the queue's own cancellation
checks and wake cancellation remain active, and a write's owned buffer remains
charged until the asynchronous operation actually returns. Local tests close with
outstanding writes and verify that all buffer and entry credits are released with
no stale completion callbacks. Native socket buffering may complete an operation
before the test observes it, so completed-but-undelivered metadata is checked too.

The same nominal 60 Hz fixed-rate Release loopback ABBA workload was used at 8 and
32 peers. Reproduce with `dotnet run --project benchmarks/Vega.NativeTcp.Benchmarks -c Release -- --send-cancellation`.
All eight runs validated byte contents, byte counts and callback counts.

| Local peers | Token passed, bytes allocated/request | Dispose-only, bytes allocated/request | Token CPU time | Dispose-only CPU time |
| ---: | ---: | ---: | ---: | ---: |
| 8 | 18.3 / 21.3 | 17.6 / 17.7 | 0.078 / 0.000 s | 0.125 / 0.312 s |
| 32 | 184.7 / 315.5 | 335.7 / 350.7 | 3.625 / 3.797 s | 4.234 / 4.828 s |

The 32-peer dispose-only result was worse on allocation and CPU in both runs; the
8-peer allocation difference was small and CPU samples are noisy at this duration.
Delivered rates and callback latency were similar. This does **not** justify
dropping the token from production sends: the token-passing mode remains enabled,
and dispose-only is retained as an internal A/B experiment. Raw output:
`../artifacts/performance-2026-11-26/send-cancellation-results.txt`. These are
aggregate same-process local sender/reader measurements, not server TPS.

## 6. Implementation order

1. **Implemented:** internal owned-send FIFO and a single byte writer with exact callback metadata, in-flight accounting and pool lifetime. Fake-sink tests.
2. **Implemented locally:** listener/accepted `ISocket`, native binding, receive-close completion and per-world generation-aware completion pump. Standalone loopback integration tests cover local accepted sockets, FIFO, receive close, slot identity, shared/per-connection budgets and factory restoration. Full real-client acceptance and wire/latency telemetry remain pending.
3. Measure real joins at representative player caps, ordinary steady state and two world workers. Tune budgets, including completion drain and native bootstrap envelope; review existing direct-send `spamCount` accounting. Only then consider enabling the new transport by default.

No new TerrariaRuntime hook is necessary to start this candidate: 1.1.2 already has the required listener, socket lifecycle and native game-thread boundaries. A small dedicated pre-packet-pass or send-completion callback hook may be justified later **only** if measurements show existing update/idle hooks cannot drain callbacks accurately without changing native scheduling.
