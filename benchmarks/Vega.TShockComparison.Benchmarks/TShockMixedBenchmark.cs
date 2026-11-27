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
using TerrariaApi.Server;
using Vega.MixedGameplay.Benchmarks;

namespace Vega.TShockComparison.Benchmarks;

// Test-only, opt-in server plugin. It generates game-side native output to passive
// private loopback receivers, never incoming client traffic or public connections.
[ApiVersion(2, 1)]
public sealed class TShockMixedBenchmark(Main game) : TerrariaPlugin(game)
{
    private int started;
    public override string Name => "Vega mixed comparison fixture";
    public override string Author => "Vega";
    public override Version Version => new(1, 0);
    public override void Initialize()
    {
        ServerApi.Hooks.GamePostInitialize.Register(this, OnUpdate);
        ServerApi.Hooks.GameUpdate.Register(this, OnUpdate);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ServerApi.Hooks.GamePostInitialize.Deregister(this, OnUpdate);
            ServerApi.Hooks.GameUpdate.Deregister(this, OnUpdate);
        }
        base.Dispose(disposing);
    }

    private void OnUpdate(EventArgs _)
    {
        if (Environment.GetEnvironmentVariable("VEGA_SYNTHETIC_BENCHMARK") != "1" || !Main.dedServ ||
            Main.maxTilesX <= 0 || Main.spawnTileX <= 0 || !int.TryParse(Environment.GetEnvironmentVariable("VEGA_TSHOCK_COMPARISON_TICKS"), out int ticks) ||
            ticks is < 120 or > 1800 || Interlocked.Exchange(ref started, 1) != 0) return;
        try { File.WriteAllText("tshock-mixed.json", JsonSerializer.Serialize(Run(ticks), new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception ex) { File.WriteAllText("tshock-mixed.json", JsonSerializer.Serialize(new { Error = ex.ToString() })); }
    }

    private const int Count = 255;
    private static object Run(int ticks)
    {
        if (!Main.dedServ || Netplay.Clients.Any(c => c?.IsActive == true))
            throw new InvalidOperationException("A fresh local dedicated server without real connections is required.");
        var originalPlayers = (Player[])Main.player.Clone();
        var originalClients = (RemoteClient[])Netplay.Clients.Clone();
        var broadcasts = NetMessage.buffer.Take(Count).Select(b => b.broadcast).ToArray();
        bool day = Main.dayTime; double time = Main.time; int netMode = Main.netMode;
        int bossSlot = -1;
        using var loopback = new PacedLoopbackTransport("native", Count);
        try
        {
            float x = Main.spawnTileX * 16, y = Main.spawnTileY * 16 - 250;
            for (int i = 0; i < Count; i++)
            {
                Main.player[i] = new Player { whoAmI = i, active = true, name = $"P{i:D3}", statLife = 400, statLifeMax = 400, position = new(x, y) };
                Netplay.Clients[i] = new RemoteClient { Id = i, State = 10, IsActive = true, Socket = new FixtureSocket(loopback.GetPeer(i)) };
                NetMessage.buffer[i].broadcast = true;
            }
            Main.netMode = 2; Main.dayTime = false; Main.time = 0;
            bossSlot = NPC.NewNPC(new EntitySource_DebugCommand(), (int)x, (int)y - 90, NPCID.EyeofCthulhu);
            if ((uint)bossSlot >= Main.npc.Length) throw new InvalidOperationException("Boss failed to spawn.");
            NPC boss = Main.npc[bossSlot];
            boss.life = boss.lifeMax = 1_000_000;
            const int warmup = 120;
            var total = new double[ticks];
            using var process = Process.GetCurrentProcess();
            long allocation = 0, start = 0, movement = 0, damage = 0, hits = 0, messages = 0, mentions = 0;
            TimeSpan cpu = default, pause = default;
            for (int tick = -warmup; tick < ticks; tick++)
            {
                if (tick == 0)
                {
                    loopback.ResetMeasurement(); allocation = GC.GetTotalAllocatedBytes(true);
                    cpu = process.TotalProcessorTime; pause = GC.GetTotalPauseDuration(); start = Stopwatch.GetTimestamp();
                }
                long t0 = Stopwatch.GetTimestamp(); loopback.Pump();
                for (int i = 0; i < Count; i++)
                {
                    if ((tick + i) % 6 != 0) continue;
                    float angle = i * 0.17f + tick * 0.025f;
                    Main.player[i].position = new(x + MathF.Cos(angle) * 180, y + MathF.Sin(angle) * 100);
                    NetMessage.SendData(MessageID.PlayerControls, -1, i, null, i);
                    if (tick >= 0) movement++;
                }
                for (int i = 0; i < Count; i++)
                {
                    if ((tick + i) % 30 != 0) continue;
                    int actual = boss.StrikeNPC(20, 0, 1, false, false, i, Main.player[i]);
                    if (tick >= 0) { hits++; damage += actual; }
                }
                boss.UpdateNPC(bossSlot);
                if (tick % 6 == 0) NetMessage.SendData(MessageID.SyncNPC, -1, -1, null, bossSlot);
                if (!boss.active) throw new InvalidOperationException("Boss despawned.");
                for (int i = 0; i < Count; i++)
                {
                    if ((tick + i) % 120 != 0) continue;
                    bool mention = (i + tick / 120) % 5 == 0;
                    if (mention)
                    {
                        // Equivalent already-rendered text, not a claim that TShock has Vega's mention module.
                        int target = (i + 1) % Count;
                        for (int recipient = 0; recipient < Count; recipient++)
                            ChatHelper.SendChatMessageToClient(NetworkText.FromLiteral(recipient == target ? $"P{i:D3}: attack now [MENTION @P{target:D3}]!" : $"P{i:D3}: attack now @P{target:D3}!"), Color.White, recipient);
                    }
                    else ChatHelper.BroadcastChatMessage(NetworkText.FromLiteral($"P{i:D3}: keep moving around the boss"), Color.White);
                    if (tick >= 0) { messages++; if (mention) mentions++; }
                }
                if (tick >= 0) total[tick] = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                loopback.Pace();
            }
            var result = loopback.Finish();
            double wall = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            Array.Sort(total);
            double P(double percentile) => total[(int)Math.Ceiling(total.Length * percentile) - 1];
            if (movement != (long)ticks * Count / 6 || hits != (long)ticks * Count / 30 || damage <= 0 || result.CompletedCallbacks != result.ScheduledCallbacks)
                throw new InvalidOperationException("Incomplete or non-matching gameplay work.");
            return new { Server = "TShock 6.2.1 / Terraria 1.4.5.8", Players = Count, Ticks = ticks, WarmupTicks = warmup,
                MovementUpdates = movement, Hits = hits, Damage = damage, Messages = messages, Mentions = mentions, WallMs = wall,
                CpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds, AllocatedBytes = GC.GetTotalAllocatedBytes(true) - allocation,
                GcPauseMs = (GC.GetTotalPauseDuration() - pause).TotalMilliseconds, PrivateBytes = process.PrivateMemorySize64,
                P50Ms = P(.5), P95Ms = P(.95), P99Ms = P(.99), Loopback = result };
        }
        finally
        {
            if ((uint)bossSlot < Main.npc.Length) Main.npc[bossSlot].active = false;
            Array.Copy(originalPlayers, Main.player, originalPlayers.Length);
            Array.Copy(originalClients, Netplay.Clients, originalClients.Length);
            for (int i = 0; i < Count; i++) NetMessage.buffer[i].broadcast = broadcasts[i];
            Main.dayTime = day; Main.time = time; Main.netMode = netMode;
        }
    }

    private sealed class FixtureSocket(PacedLoopbackTransport.Peer peer) : ISocket
    {
        public void Close() { }
        public bool IsConnected() => peer.Socket.IsConnected();
        public bool IsDataAvailable() => false;
        public RemoteAddress GetRemoteAddress() => new TcpAddress(System.Net.IPAddress.Loopback, 0);
        public void Connect(RemoteAddress address) => throw new NotSupportedException();
        public bool StartListening(SocketConnectionAccepted callback) => throw new NotSupportedException();
        public void StopListening() => throw new NotSupportedException();
        public void AsyncReceive(byte[] data, int offset, int size, SocketReceiveCallback callback, object? state = null) => throw new NotSupportedException();
        public void AsyncSend(byte[] data, int offset, int size, SocketSendCallback callback, object? state = null) => peer.Send(data, offset, size, callback, state);
    }
}
