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
using Vega.Contracts.Modules;
using Vega.Contracts.Players;
using Vega.Contracts.Worlds;
using Vega.Core.Messaging;
using Vega.Modules.Mentions;
using Vega.PluginSdk;

namespace Vega.MixedGameplay.Benchmarks;

// Test-only plugin. No inbound client packets are generated; output is either recorded
// in-process or sent to a private localhost-only passive receiver fixture.
public sealed class MixedGameplayBenchmark : VegaPlugin
{
    public override VegaModuleDescriptor Descriptor { get; } = new("Vega.Benchmarks.MixedGameplay", "1.0.0", ["Vega.PluginSdk"]);

    protected override void Configure(IVegaPluginContext plugin)
    {
        if (Environment.GetEnvironmentVariable("VEGA_SYNTHETIC_BENCHMARK") == "1")
        {
            plugin.Commands.Register(new RunCommand(plugin));
            plugin.Commands.Register(new FullWorldMixedCommand(plugin));
        }
    }

    private sealed class RunCommand(IVegaPluginContext plugin) : ICommandHandler
    {
        public CommandDescriptor Descriptor { get; } = new("mixedbench", [], [], Syntax: "mixedbench <legacy|optimized> <ticks> [recording|native|ordered]");

        public ValueTask<CommandExecutionResult> ExecuteAsync(CommandInvocation invocation, CancellationToken cancellationToken)
        {
            if (!invocation.Principal.IsServer || invocation.Arguments.Count is < 2 or > 3 ||
                invocation.Arguments[0] is not ("legacy" or "optimized") ||
                !int.TryParse(invocation.Arguments[1], out int ticks) || ticks is < 120 or > 3600 ||
                invocation.Arguments.Count == 3 && invocation.Arguments[2] is not ("recording" or "native" or "ordered"))
                return ValueTask.FromResult(CommandExecutionResult.Failed("benchmark.invalid_arguments"));

            string mode = invocation.Arguments[0];
            string transport = invocation.Arguments.Count == 3 ? invocation.Arguments[2] : "recording";
            bool posted = plugin.Tasks.Post("mixed-gameplay", () =>
            {
                string path = Path.GetFullPath($"mixed-{mode}.json");
                try
                {
                    File.WriteAllText(path, JsonSerializer.Serialize(Run(mode, ticks, transport), new JsonSerializerOptions { WriteIndented = true }));
                }
                catch (Exception ex)
                {
                    File.WriteAllText(path, JsonSerializer.Serialize(new { Error = ex.ToString() }));
                }
            });
            return ValueTask.FromResult(posted ? CommandExecutionResult.Success : CommandExecutionResult.Failed("benchmark.dispatcher_unavailable"));
        }
    }

    private const int Count = 255;

    private static object Run(string mode, int ticks, string transport)
    {
        if (!Main.dedServ || Main.netMode != 2 || Netplay.Clients.Any(c => c?.IsActive == true))
            throw new InvalidOperationException("A fresh dedicated server without real connections is required.");
        Player[] originalPlayers = (Player[])Main.player.Clone();
        RemoteClient[] originalClients = (RemoteClient[])Netplay.Clients.Clone();
        bool[] originalBroadcast = NetMessage.buffer.Take(Count).Select(b => b.broadcast).ToArray();
        bool day = Main.dayTime;
        double time = Main.time;
        var sockets = new RecordingSocket[Count];
        PacedLoopbackTransport? loopback = null;
        int bossSlot = -1;
        try
        {
            if (transport != "recording") loopback = new(transport, Count);
            float x = Main.spawnTileX * 16, y = Main.spawnTileY * 16 - 250;
            var players = new PlayerSnapshot[Count];
            for (int i = 0; i < Count; i++)
            {
                string name = $"P{i:D3}";
                Main.player[i] = new Player { whoAmI = i, active = true, name = name, statLife = 400, statLifeMax = 400, position = new Vector2(x, y) };
                sockets[i] = new RecordingSocket(loopback?.GetPeer(i));
                var client = new RemoteClient { Id = i };
                client.Activate(sockets[i]);
                client.State = 10;
                Netplay.Clients[i] = client;
                NetMessage.buffer[i].broadcast = true;
                players[i] = new(new PlayerId(i), name, false, 400, 400, 200, 200, new PlayerPosition(x,y), false, 0);
            }

            Main.dayTime = false;
            Main.time = 0;
            bossSlot = NPC.NewNPC(new EntitySource_DebugCommand(), (int)x, (int)y - 90, NPCID.EyeofCthulhu);
            if ((uint)bossSlot >= Main.npc.Length) throw new InvalidOperationException("Boss failed to spawn.");
            NPC boss = Main.npc[bossSlot];
            boss.life = boss.lifeMax = 1_000_000; // Keep the real boss AI alive throughout the trial.
            var pipeline = new ChatPipeline(renderers: [mode == "legacy" ? new LegacyMentionRenderer() : new MentionRenderer()]);
            const int warmup = 120;
            var movement = new double[ticks];
            var combat = new double[ticks];
            var chat = new double[ticks];
            var delivery = new double[ticks];
            var completionPump = new double[ticks];
            var total = new double[ticks];
            long movementUpdates = 0, hits = 0, damage = 0, messages = 0, mentions = 0, rendered = 0, chatAllocated = 0;
            long allocated = 0, movementAllocated = 0, combatAllocated = 0, deliveryAllocated = 0;
            int[] gc = new int[3];
            TimeSpan cpu = default, pause = default;
            using var process = Process.GetCurrentProcess();
            long started = 0;
            for (int tick = -warmup; tick < ticks; tick++)
            {
                if (tick == 0)
                {
                    loopback?.ResetMeasurement();
                    foreach (var socket in sockets) socket.Reset();
                    allocated = GC.GetTotalAllocatedBytes(true);
                    for (int g = 0; g < 3; g++) gc[g] = GC.CollectionCount(g);
                    cpu = process.TotalProcessorTime;
                    pause = GC.GetTotalPauseDuration();
                    started = Stopwatch.GetTimestamp();
                }
                long t0 = Stopwatch.GetTimestamp();
                loopback?.Pump();
                long movementStarted = loopback is null ? t0 : Stopwatch.GetTimestamp();
                long a0 = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < Count; i++)
                {
                    if ((tick + i) % 6 != 0) continue;
                    float angle = i * 0.17f + tick * 0.025f;
                    Main.player[i].position = new Vector2(x + MathF.Cos(angle) * 180, y + MathF.Sin(angle) * 100);
                    NetMessage.SendData(MessageID.PlayerControls, -1, i, null, i);
                    if (tick >= 0) movementUpdates++;
                }
                long t1 = Stopwatch.GetTimestamp();
                long a1 = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < Count; i++)
                {
                    if ((tick + i) % 30 != 0) continue;
                    int actual = boss.StrikeNPC(20, 0, 1, false, false, i, Main.player[i]);
                    if (tick >= 0) { hits++; damage += actual; }
                }
                boss.UpdateNPC(bossSlot);
                if (tick % 6 == 0) NetMessage.SendData(MessageID.SyncNPC, -1, -1, null, bossSlot);
                if (!boss.active) throw new InvalidOperationException("Boss despawned during scenario.");
                long t2 = Stopwatch.GetTimestamp();
                long a2 = GC.GetAllocatedBytesForCurrentThread();
                if (tick >= 0) { movementAllocated += a1 - a0; combatAllocated += a2 - a1; }
                double renderMs = 0, deliverMs = 0;
                for (int i = 0; i < Count; i++)
                {
                    if ((tick + i) % 120 != 0) continue;
                    bool mention = (i + tick / 120) % 5 == 0;
                    string content = mention ? $"attack now @P{(i + 1) % Count:D3}!" : "keep moving around the boss";
                    long bytes = GC.GetAllocatedBytesForCurrentThread();
                    long cr = Stopwatch.GetTimestamp();
                    ChatPipelineResult result = pipeline.Process(new(WorldId.Primary, players[i], content, players));
                    long cd = Stopwatch.GetTimestamp();
                    long ad = GC.GetAllocatedBytesForCurrentThread();
                    if (tick >= 0) chatAllocated += ad - bytes;
                    if (result.RequiresCustomDelivery)
                    {
                        foreach (var item in result.Deliveries)
                            ChatHelper.SendChatMessageToClient(NetworkText.FromLiteral(item.Content), Color.White, item.RecipientId.Slot);
                    }
                    else ChatHelper.BroadcastChatMessage(NetworkText.FromLiteral(result.Deliveries[0].Content), Color.White);
                    if (tick >= 0) deliveryAllocated += GC.GetAllocatedBytesForCurrentThread() - ad;
                    renderMs += Stopwatch.GetElapsedTime(cr, cd).TotalMilliseconds;
                    deliverMs += Stopwatch.GetElapsedTime(cd).TotalMilliseconds;
                    if (tick >= 0) { messages++; if (mention) mentions++; rendered += result.Deliveries.Count; }
                }
                loopback?.FlushTick();
                if (tick >= 0)
                {
                    completionPump[tick] = Stopwatch.GetElapsedTime(t0, movementStarted).TotalMilliseconds;
                    movement[tick] = Stopwatch.GetElapsedTime(movementStarted, t1).TotalMilliseconds;
                    combat[tick] = Stopwatch.GetElapsedTime(t1, t2).TotalMilliseconds;
                    chat[tick] = renderMs;
                    delivery[tick] = deliverMs;
                    total[tick] = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                }
                loopback?.Pace();
            }
            PacedLoopbackTransport.Result? transportResult = loopback?.Finish();
            var wallMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            long sends = sockets.Sum(s => s.Sends);
            long movementSends = sockets.Sum(s => s.Movement);
            long outputBytes = sockets.Sum(s => s.Bytes);
            if (movementSends != movementUpdates * (Count - 1) || damage <= 0 || rendered != messages * Count)
                throw new InvalidOperationException($"Incomplete scenario: movement deliveries={movementSends}, damage={damage}, rendered={rendered}.");
            if (transportResult is not null && (transportResult.CompletedCallbacks != sends || transportResult.ScheduledCallbacks != sends ||
                transportResult.ReceivedBytes != outputBytes || transportResult.SentBytes != outputBytes))
                throw new InvalidOperationException("Loopback transport measurements differ from native output.");
            return new
            {
                Mode = mode, Transport = transport, Paced = loopback is not null, Players = Count, WarmupTicks = warmup, Ticks = ticks, WallMs = wallMs,
                CpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds,
                AllocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated,
                GcCounts = gc.Select((n,g) => GC.CollectionCount(g) - n).ToArray(), GcPauseMs = (GC.GetTotalPauseDuration() - pause).TotalMilliseconds,
                WorkingSetBytes = process.WorkingSet64, PrivateBytes = process.PrivateMemorySize64,
                MovementUpdates = movementUpdates, Hits = hits, Damage = damage, BossRemainingLife = boss.life,
                Messages = messages, Mentions = mentions, RenderedRecipients = rendered, ChatAllocatedBytes = chatAllocated,
                MovementAllocatedBytes = movementAllocated, CombatAllocatedBytes = combatAllocated, ChatDeliveryAllocatedBytes = deliveryAllocated,
                Sends = sends, MovementSends = movementSends, OutputBytes = outputBytes,
                CallbackChanges = sockets.Sum(s => s.CallbackChanges), BorrowedSends = sockets.Sum(s => s.BorrowedSends),
                Loopback = transportResult, CompletionPump = Stats(completionPump),
                Total = Stats(total), Movement = Stats(movement), Combat = Stats(combat), ChatRendering = Stats(chat), ChatDelivery = Stats(delivery)
            };
        }
        finally
        {
            try { loopback?.Dispose(); }
            finally
            {
                if ((uint)bossSlot < Main.npc.Length) Main.npc[bossSlot].active = false;
                Array.Copy(originalPlayers, Main.player, originalPlayers.Length);
                Array.Copy(originalClients, Netplay.Clients, originalClients.Length);
                for (int i = 0; i < Count; i++) NetMessage.buffer[i].broadcast = originalBroadcast[i];
                Main.dayTime = day;
                Main.time = time;
            }
        }
    }

    private static object Stats(double[] values)
    {
        Array.Sort(values);
        double P(double p) => values[(int)Math.Ceiling(values.Length * p) - 1];
        return new { MeanMs = values.Average(), P50Ms = P(.5), P95Ms = P(.95), P99Ms = P(.99), MaxMs = values[^1] };
    }

    private sealed class RecordingSocket(PacedLoopbackTransport.Peer? peer) : ISocket, IBorrowedPacketSocket
    {
        public long Sends, Bytes, Movement, CallbackChanges, BorrowedSends;
        private SocketSendCallback? previousCallback;
        public void Reset() => Sends = Bytes = Movement = CallbackChanges = BorrowedSends = 0;
        public void Close() => peer?.Socket.Close();
        public bool IsConnected() => peer?.Socket.IsConnected() ?? true;
        public bool IsDataAvailable() => false;
        public RemoteAddress GetRemoteAddress() => peer?.Socket.GetRemoteAddress() ?? new TcpAddress(System.Net.IPAddress.Loopback, 0);
        public void Connect(RemoteAddress address) => throw new NotSupportedException();
        public bool StartListening(SocketConnectionAccepted callback) => throw new NotSupportedException();
        public void StopListening() => throw new NotSupportedException();
        public void AsyncReceive(byte[] data, int offset, int size, SocketReceiveCallback callback, object? state = null) => throw new NotSupportedException();
        public void AsyncSend(byte[] data, int offset, int size, SocketSendCallback callback, object? state = null)
        {
            Record(data.AsSpan(offset, size), callback);
            if (peer is null) callback(state);
            else peer.Send(data, offset, size, callback, state);
        }
        public void SendBorrowed(ReadOnlySpan<byte> packet, SocketSendCallback callback, object? state)
        {
            BorrowedSends++;
            Record(packet, callback);
            if (peer is null) callback(state);
            else peer.SendBorrowed(packet, callback, state);
        }
        private void Record(ReadOnlySpan<byte> packet, SocketSendCallback callback)
        {
            Sends++; Bytes += packet.Length;
            if (!ReferenceEquals(previousCallback, callback))
            {
                CallbackChanges++;
                previousCallback = callback;
            }
            if (packet.Length >= 3 && packet[2] == MessageID.PlayerControls) Movement++;
        }
    }

    private sealed class LegacyMentionRenderer : IChatRecipientRenderer
    {
        public ChatRecipientRenderResult Render(ChatMessageContext context, PlayerSnapshot recipient, string content)
        {
            string token = $"@{recipient.Name}";
            int Find(int start)
            {
                int index = content.IndexOf(token, start, StringComparison.OrdinalIgnoreCase);
                while (index >= 0)
                {
                    int end = index + token.Length;
                    if ((index == 0 || !NameChar(content[index - 1])) && (end == content.Length || !NameChar(content[end]))) return index;
                    index = content.IndexOf(token, end, StringComparison.OrdinalIgnoreCase);
                }
                return -1;
            }
            int at = Find(0);
            if (at < 0) return new(content);
            var builder = new System.Text.StringBuilder(content.Length + 16);
            int consumed = 0;
            do
            {
                builder.Append(content, consumed, at - consumed).Append("[MENTION ").Append(content, at, token.Length).Append(']');
                consumed = at + token.Length;
                at = Find(consumed);
            } while (at >= 0);
            return new(builder.Append(content, consumed, content.Length - consumed).ToString(), true);
        }
        private static bool NameChar(char c) => char.IsLetterOrDigit(c) || c == '_';
    }
}
