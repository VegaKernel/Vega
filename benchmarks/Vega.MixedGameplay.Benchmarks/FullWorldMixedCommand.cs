using System.Diagnostics;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.Chat;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.Localization;
using Terraria.Net;
using Terraria.Net.Sockets;
using TerrariaRuntime;
using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Players;
using Vega.Contracts.Worlds;
using Vega.Core.Messaging;
using Vega.Modules.Mentions;
using Vega.PluginSdk;

namespace Vega.MixedGameplay.Benchmarks;

// Disposable test-only world fixture. Unlike mixedbench, this never calls a
// re-entrant Main.Update or manually advances NPC AI: native dedicated ticks
// run normally between each scheduled injection. No client sends ingress.
internal sealed class FullWorldMixedCommand(IVegaPluginContext plugin) : ICommandHandler
{
    private int active;
    public CommandDescriptor Descriptor { get; } = new("fullworldbench", [], [], Syntax: "fullworldbench <ticks> <recording|ordered|native>");

    public ValueTask<CommandExecutionResult> ExecuteAsync(CommandInvocation invocation, CancellationToken cancellationToken)
    {
        if (!invocation.Principal.IsServer || invocation.Arguments.Count != 2 ||
            !int.TryParse(invocation.Arguments[0], out int ticks) || ticks is < 120 or > 3600 ||
            invocation.Arguments[1] is not ("recording" or "ordered" or "native") || Interlocked.Exchange(ref active, 1) != 0)
            return ValueTask.FromResult(CommandExecutionResult.Failed("benchmark.invalid_arguments_or_active"));
        string transport = invocation.Arguments[1];
        bool posted = plugin.Tasks.Post("full-world-mixed", () =>
        {
            Trial? trial = null;
            try { trial = new(ticks, transport, () => Volatile.Write(ref active, 0)); trial.Start(); }
            catch (Exception ex)
            {
                trial?.Dispose();
                Volatile.Write(ref active, 0);
                Write(new { Error = ex.ToString() });
            }
        });
        if (!posted) Volatile.Write(ref active, 0);
        return ValueTask.FromResult(posted ? CommandExecutionResult.Success : CommandExecutionResult.Failed("benchmark.dispatcher_unavailable"));
    }

    private static void Write(object result) => File.WriteAllText("fullworld-optimized.json",
        JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));

    private sealed class Trial(int ticks, string transport, Action finished) : IDisposable
    {
        private const int Players = 255, Warmup = 300;
        private readonly Player[] originalPlayers = (Player[])Main.player.Clone();
        private readonly RemoteClient[] originalClients = (RemoteClient[])Netplay.Clients.Clone();
        private readonly bool[] originalBroadcast = NetMessage.buffer.Take(Players).Select(b => b.broadcast).ToArray();
        private readonly bool day = Main.dayTime;
        private readonly double time = Main.time;
        private readonly PlayerSnapshot[] snapshots = new PlayerSnapshot[Players];
        private readonly PassiveSocket[] sockets = new PassiveSocket[Players];
        private readonly ChatPipeline chat = new(renderers: [new MentionRenderer()]);
        private readonly double[] nativeMs = new double[ticks], injectMs = new double[ticks], busyMs = new double[ticks], wallPeriods = new double[ticks], pumpMs = new double[ticks], flushMs = new double[ticks];
        private readonly Process process = Process.GetCurrentProcess();
        private PacedLoopbackTransport? loopback;
        private int bossSlot = -1, tick = -Warmup, completed;
        private long startedAt, lastInjection, allocated, movements, attacks, damage, messages;
        private uint lastWorldUpdate;
        private long verifiedWorldUpdates;
        private double previousInject, previousPump, previousFlush;
        private TimeSpan cpu, pause;
        private float x, y;
        private bool attached, initialized, disposed;

        public void Start()
        {
            if (!Main.dedServ || Main.netMode != 2 || Netplay.Clients.Any(c => c?.IsActive == true))
                throw new InvalidOperationException("A fresh disposable server without real clients is required.");
            initialized = true;
            if (transport != "recording") loopback = new(transport, Players);
            x = Main.spawnTileX * 16; y = Main.spawnTileY * 16 - 250;
            for (int i = 0; i < Players; i++)
            {
                string name = $"P{i:D3}";
                Main.player[i] = new Player { whoAmI = i, active = true, name = name, statLife = 400, statLifeMax = 400, position = new(x, y) };
                sockets[i] = new(loopback?.GetPeer(i));
                var client = new RemoteClient { Id = i };
                client.Activate(sockets[i]); client.State = 10;
                Netplay.Clients[i] = client; NetMessage.buffer[i].broadcast = true;
                snapshots[i] = new(new PlayerId(i), name, false, 400, 400, 200, 200, new PlayerPosition(x,y), false, 0);
            }
            Main.dayTime = false; Main.time = 0;
            bossSlot = NPC.NewNPC(new EntitySource_DebugCommand(), (int)x, (int)y - 90, NPCID.EyeofCthulhu);
            if ((uint)bossSlot >= Main.npc.Length) throw new InvalidOperationException("Boss spawn failed.");
            Main.npc[bossSlot].life = Main.npc[bossSlot].lifeMax = 1_000_000;
            Hooks.Main.DedicatedTickCompleted += OnTick;
            Hooks.Main.DedicatedWorldStopping += Dispose;
            attached = true;
        }

        private void OnTick(TimeSpan nativeDuration)
        {
            try
            {
                long now = Stopwatch.GetTimestamp();
                if (tick > 0)
                {
                    int index = tick - 1;
                    uint current = Main.GameUpdateCount;
                    uint updates = unchecked(current - lastWorldUpdate);
                    if (updates != 1 || !Netplay.HasFullyConnectedClients)
                        throw new InvalidOperationException($"Dedicated loop did not run exactly one full native world update (observed {updates}).");
                    lastWorldUpdate = current;
                    verifiedWorldUpdates += updates;
                    nativeMs[index] = nativeDuration.TotalMilliseconds;
                    injectMs[index] = previousInject;
                    pumpMs[index] = previousPump;
                    flushMs[index] = previousFlush;
                    busyMs[index] = nativeMs[index] + previousInject;
                    wallPeriods[index] = Stopwatch.GetElapsedTime(lastInjection, now).TotalMilliseconds;
                    completed++;
                }
                if (tick == ticks) { Finish(now); return; }
                if (tick == 0)
                {
                    loopback?.ResetMeasurement();
                    foreach (var socket in sockets) socket.Reset();
                    allocated = GC.GetTotalAllocatedBytes(true); cpu = process.TotalProcessorTime; pause = GC.GetTotalPauseDuration();
                    lastWorldUpdate = Main.GameUpdateCount;
                    startedAt = Stopwatch.GetTimestamp();
                }
                lastInjection = Stopwatch.GetTimestamp();
                loopback?.Pump();
                previousPump = Stopwatch.GetElapsedTime(lastInjection).TotalMilliseconds;
                NPC boss = Main.npc[bossSlot];
                if (!boss.active) throw new InvalidOperationException("Boss despawned during full-world trial.");
                for (int i = 0; i < Players; i++)
                {
                    var player = Main.player[i];
                    var client = Netplay.Clients[i];
                    if (!client.IsActive || client.State != 10 || !ReferenceEquals(client.Socket, sockets[i]))
                        throw new InvalidOperationException($"Synthetic slot {i} left during full-world trial.");
                    // A passive fixture has no inbound keepalive. Maintain its
                    // synthetic avatar and exercise native world simulation/output.
                    client.TimeOutTimer = 0;
                    player.active = true; player.dead = false; player.statLife = 400; player.immune = true; player.immuneTime = 2;
                    if ((tick + i) % 6 == 0)
                    {
                        float angle = i * .17f + tick * .025f;
                        player.position = new(x + MathF.Cos(angle) * 180, y + MathF.Sin(angle) * 100);
                        player.velocity = Vector2.Zero;
                        NetMessage.SendData(MessageID.PlayerControls, -1, i, null, i);
                        if (tick >= 0) movements++;
                    }
                    if ((tick + i) % 30 == 0)
                    {
                        int hit = boss.StrikeNPC(20, 0, 1, false, false, i, player);
                        if (tick >= 0) { attacks++; damage += hit; }
                    }
                    if ((tick + i) % 120 == 0)
                    {
                        string content = (i + tick / 120) % 5 == 0 ? $"attack now @P{(i + 1) % Players:D3}!" : "keep moving around the boss";
                        var result = chat.Process(new(WorldId.Primary, snapshots[i], content, snapshots));
                        if (result.RequiresCustomDelivery)
                        {
                            foreach (var item in result.Deliveries)
                                ChatHelper.SendChatMessageToClient(NetworkText.FromLiteral(item.Content), Color.White, item.RecipientId.Slot);
                        }
                        else ChatHelper.BroadcastChatMessage(NetworkText.FromLiteral(result.Deliveries[0].Content), Color.White);
                        if (tick >= 0) messages++;
                    }
                }
                if (tick % 6 == 0) NetMessage.SendData(MessageID.SyncNPC, -1, -1, null, bossSlot);
                long flushStarted = Stopwatch.GetTimestamp();
                loopback?.FlushTick();
                previousFlush = Stopwatch.GetElapsedTime(flushStarted).TotalMilliseconds;
                previousInject = Stopwatch.GetElapsedTime(lastInjection).TotalMilliseconds;
                tick++;
            }
            catch (Exception ex) { Dispose(); Write(new { Error = ex.ToString(), CompletedNativeTicks = completed }); }
        }

        private void Finish(long now)
        {
            var result = loopback?.Finish();
            long sends = sockets.Sum(s => s.Sends), bytes = sockets.Sum(s => s.Bytes);
            if (completed != ticks || verifiedWorldUpdates != ticks || movements != (long)ticks * Players / 6 || attacks != (long)ticks * Players / 30 || damage <= 0 ||
                result is not null && (result.CompletedCallbacks != sends || result.ReceivedBytes != bytes))
                throw new InvalidOperationException("Full-world fixture did not complete its expected native workload/output.");
            object report = new { FullNativeWorldTicks = true, VerifiedWorldUpdates = verifiedWorldUpdates, Transport = transport, Players, WarmupTicks = Warmup, Ticks = ticks,
                WallMs = Stopwatch.GetElapsedTime(startedAt, now).TotalMilliseconds, CpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds,
                AllocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated, GcPauseMs = (GC.GetTotalPauseDuration() - pause).TotalMilliseconds,
                PrivateBytes = process.PrivateMemorySize64, MovementUpdates = movements, Hits = attacks, Damage = damage, Messages = messages,
                Sends = sends, OutputBytes = bytes, BorrowedSends = sockets.Sum(s => s.Borrowed), Loopback = result,
                BusyOverruns = busyMs.Count(ms => ms > 1000d / 60), Native = Stats(nativeMs), Injection = Stats(injectMs), CompletionPump = Stats(pumpMs), WriterFlush = Stats(flushMs), Busy = Stats(busyMs), Cadence = Stats(wallPeriods) };
            Dispose(); Write(report);
        }

        public void Dispose()
        {
            if (disposed) return; disposed = true;
            if (attached) { Hooks.Main.DedicatedTickCompleted -= OnTick; Hooks.Main.DedicatedWorldStopping -= Dispose; }
            try { loopback?.Dispose(); }
            finally
            {
                if (initialized)
                {
                    if ((uint)bossSlot < Main.npc.Length) Main.npc[bossSlot].active = false;
                    Array.Copy(originalPlayers, Main.player, originalPlayers.Length);
                    Array.Copy(originalClients, Netplay.Clients, originalClients.Length);
                    for (int i = 0; i < Players; i++) NetMessage.buffer[i].broadcast = originalBroadcast[i];
                    Main.dayTime = day; Main.time = time;
                }
                process.Dispose(); finished();
            }
        }
    }

    private static object Stats(double[] values)
    {
        Array.Sort(values);
        double P(double p) => values[(int)Math.Ceiling(values.Length * p) - 1];
        return new { MeanMs = values.Average(), P50Ms = P(.5), P95Ms = P(.95), P99Ms = P(.99), MaxMs = values[^1] };
    }

    private sealed class PassiveSocket(PacedLoopbackTransport.Peer? peer) : ISocket, IBorrowedPacketSocket
    {
        public long Sends, Bytes, Borrowed;
        public void Reset() => Sends = Bytes = Borrowed = 0;
        public void Close() => peer?.Socket.Close();
        public bool IsConnected() => peer?.Socket.IsConnected() ?? true;
        public bool IsDataAvailable() => false;
        public RemoteAddress GetRemoteAddress() => peer?.Socket.GetRemoteAddress() ?? new TcpAddress(System.Net.IPAddress.Loopback, 0);
        public void Connect(RemoteAddress remoteAddress) => throw new NotSupportedException();
        public bool StartListening(SocketConnectionAccepted callback) => throw new NotSupportedException();
        public void StopListening() => throw new NotSupportedException();
        // Native may post one read; passive local receivers never provide input.
        public void AsyncReceive(byte[] data, int offset, int size, SocketReceiveCallback callback, object? state = null) { }
        public void AsyncSend(byte[] data, int offset, int size, SocketSendCallback callback, object? state = null)
        { Sends++; Bytes += size; if (peer is null) callback(state); else peer.Send(data, offset, size, callback, state); }
        public void SendBorrowed(ReadOnlySpan<byte> packet, SocketSendCallback callback, object? state)
        { Borrowed++; Sends++; Bytes += packet.Length; if (peer is null) callback(state); else peer.SendBorrowed(packet, callback, state); }
    }
}
