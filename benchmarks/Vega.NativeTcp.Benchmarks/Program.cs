using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Terraria.Net.Sockets;
using Vega.Terraria.Networking;

const int PerPeerPerTick = 32;
const int Ticks = 120;
const int WarmupTicks = 15;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
var process = Process.GetCurrentProcess();
Console.WriteLine($"OS={System.Runtime.InteropServices.RuntimeInformation.OSDescription} cores={Environment.ProcessorCount} runtime={Environment.Version}");
Console.WriteLine("TcpSocket constructors: " + string.Join("; ", typeof(TcpSocket).GetConstructors(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).Select(c=>c.ToString())));
if (args.Contains("--probe")) return;
if (args.Contains("--sustained"))
{
    // Thirty seconds of the same ordinary loopback workload, rather than a short burst.
    foreach (bool ordered in new[]{false,true})
        await Run(255, ordered, 1, perPeerPerTick: 8, measureQueueAge: ordered, measurementTicks: 1800);
    return;
}
if (args.Contains("--queue-age"))
{
    foreach (int peers in new[]{200,255})
        foreach (bool measureAge in new[]{false,true,true,false})
            await Run(peers, true, 1, perPeerPerTick: 8, measureQueueAge: measureAge);
    return;
}
if (args.Contains("--production-pump-cap"))
{
    foreach (int peers in new[]{200,255})
        foreach (int cap in new[]{2048,8192,8192,2048})
        {
            try { await Run(peers, true, 1, perPeerPerTick: 32, maxCallbacks: cap); }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or InvalidOperationException)
            { Console.WriteLine($"FAILED mode=ordered peers={peers} callback_cap={cap} reason={ex.GetType().Name}: {ex.Message}"); }
        }
    return;
}
if (args.Contains("--production-batch"))
{
    foreach (int peers in new[]{200,255})
        foreach (int batch in new[]{1,16,16,1})
        {
            try { await Run(peers, true, batch, perPeerPerTick: 8); }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or InvalidOperationException)
            { Console.WriteLine($"FAILED mode=ordered peers={peers} batch={batch} reason={ex.GetType().Name}: {ex.Message}"); }
        }
    return;
}
if (args.Contains("--production-peers") || args.Contains("--production-overload"))
{
    int sends = args.Contains("--production-overload") ? 32 : 8;
    foreach (int peers in new[]{200,255})
        foreach (bool ordered in new[]{false,true,true,false})
        {
            try { await Run(peers, ordered, 1, perPeerPerTick: sends); }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or InvalidOperationException)
            { Console.WriteLine($"FAILED mode={(ordered?"ordered":"vanilla")} peers={peers} sends_per_peer_per_tick={sends} reason={ex.GetType().Name}: {ex.Message}"); }
        }
    return;
}
if (args.Contains("--send-cancellation"))
{
    foreach (int peers in new[]{32,8})
        foreach (bool cancel in new[]{true,false,false,true})
            await Run(peers, true, 1, cancelPendingSocketSend: cancel);
    return;
}
if (args.Contains("--pump"))
{
    foreach (int peers in new[]{1,8,32})
        foreach (bool active in new[]{false,true,true,false})
            await Run(peers, true, 1, activePump: active);
    return;
}
if (args.Contains("--wake"))
{
    foreach (int peers in new[]{32,8})
        foreach (bool edge in new[]{false,true,true,false})
            await Run(peers, true, 1, edge);
    return;
}
if (args.Contains("--batch"))
{
    foreach (int peers in new[]{32,8})
        foreach (int batch in new[]{1,16,16,1})
            await Run(peers, true, batch);
    return;
}
foreach (int peers in new[]{1,8,32})
{
    foreach (bool ordered in new[]{false,true,true,false})
        await Run(peers, ordered, 1);
}

async Task Run(int peers, bool ordered, int batch, bool edge = true, bool activePump = true, bool cancelPendingSocketSend = true,
    int perPeerPerTick = PerPeerPerTick, int maxCallbacks = NativeTcpTransportOptions.DefaultCallbacksPerPass, bool measureQueueAge = false,
    int measurementTicks = Ticks)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(peers >= 200 ? 60 : 25, measurementTicks / 60 + 30)));
    CancellationToken ct = timeout.Token;
    var socks = new ISocket[peers]; // host uses this only after setup
    using var host = ordered ? new NativeTcpTransportHost(new NativeTcpTransportOptions { MaxBatchRequests = batch, EdgeTriggeredWake = edge, CancelPendingSocketSend = cancelPendingSocketSend, MaxCallbacksPerPass = maxCallbacks, MeasureQueueAge = measureQueueAge }, currentSocket: slot => slot < socks.Length ? socks[slot] : null, activeSlotSnapshot: activePump) : null;
    using var listener = new TcpListener(IPAddress.Loopback,0);
    listener.Start();
    var clients = new TcpClient[peers];
    var payload = new byte[peers][];
    int totalRequests = checked(peers*measurementTicks*perPeerPerTick);
    int warmupRequests = peers*WarmupTicks*perPeerPerTick;
    var completions = new long[totalRequests+warmupRequests];
    var enqueues = new long[completions.Length];
    var states = Enumerable.Range(0, completions.Length).Select(i=>(object)i).ToArray();
    int callbackCount=0;
    SocketSendCallback callback=state=>{int i=(int)state!; completions[i]=Stopwatch.GetTimestamp(); Interlocked.Increment(ref callbackCount);};
    int[] sizes = [64, 256, 1024, 4096];
    int bytesPerTick = perPeerPerTick / sizes.Length * sizes.Sum();
    int peerBytes = checked((measurementTicks+WarmupTicks)*bytesPerTick);
    try
    {
        for(int i=0;i<peers;i++)
        {
            var client = clients[i] = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback,((IPEndPoint)listener.LocalEndpoint).Port,ct);
            TcpClient accepted = await listener.AcceptTcpClientAsync(ct);
            accepted.NoDelay=true;
            if (ordered)
            {
                var connection=host!.CreateAccepted(accepted.Client) ?? throw new Exception("accept");
                socks[i]=connection; host.BindActivated(i,connection);
                // NativeTcpConnection owns the accepted socket; do not dispose the TcpClient wrapper.
            }
            else
            {
                socks[i]=(ISocket)Activator.CreateInstance(typeof(TcpSocket), BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,[accepted],null)!;
            }
            payload[i] = Enumerable.Repeat((byte)(i+1),sizes.Max()).ToArray();
        }
        var readers=clients.Select((client,peer)=>ReadPeer(client,(byte)(peer+1),peerBytes,ct)).ToArray();
        int index=0;
        long cadence=Stopwatch.Frequency/60;
        long start=Stopwatch.GetTimestamp();
        int peakEntries=0, peakInflight=0, peakCompletions=0;
        long peakBytes=0;
        double peakSendAge=0, peakCompletionAge=0;
        var pumpTime = new long[measurementTicks];
        var producerTime = new long[measurementTicks];
        Task delayUntil(long target) => DelayUntil(target,ct);
        void sample()
        {
            if (!ordered) return;
            if (measureQueueAge)
            {
                var ages = host!.ReadQueueAges();
                peakSendAge = Math.Max(peakSendAge, ages.Send.TotalMilliseconds);
                peakCompletionAge = Math.Max(peakCompletionAge, ages.Completion.TotalMilliseconds);
            }
            var retained=host!.Retained;
            peakEntries=Math.Max(peakEntries,retained.Entries); peakBytes=Math.Max(peakBytes,retained.Bytes);
            int inflight=0, comps=0;
            foreach(var s in socks)
            {
                var q=((NativeTcpConnection)s).OutboundSnapshot;
                inflight += q.OutstandingRequests-q.PendingRequests-q.PendingCompletions;
                comps += q.PendingCompletions;
            }
            peakInflight=Math.Max(peakInflight,inflight); peakCompletions=Math.Max(peakCompletions,comps);
        }
        async Task tick(int t)
        {
            long deadline=start+(t+1)*cadence;
            long p0=Stopwatch.GetTimestamp();
            for(int k=0;k<perPeerPerTick;k++)
            {
                int size=sizes[k%sizes.Length];
                for(int i=0;i<peers;i++)
                {
                    enqueues[index]=Stopwatch.GetTimestamp();
                    socks[i].AsyncSend(payload[i],0,size,callback,states[index++]);
                }
            }
            long pt=Stopwatch.GetTimestamp()-p0;
            if (ordered && socks.Any(socket => socket is NativeTcpConnection { IsClosed: true }))
                throw new InvalidOperationException($"Connection closed under offered load; callbacks={Volatile.Read(ref callbackCount)} retained={host!.Retained}.");
            if(ordered) sample();
            if(t>=WarmupTicks) producerTime[t-WarmupTicks]=pt;
            await delayUntil(deadline);
            long before=Stopwatch.GetTimestamp();
            if(ordered) { sample(); host!.DrainCompletions(); sample(); }
            if(t>=WarmupTicks) pumpTime[t-WarmupTicks]=Stopwatch.GetTimestamp()-before;
            ct.ThrowIfCancellationRequested();
        }
        for(int t=0;t<WarmupTicks;t++) await tick(t);
        while(Volatile.Read(ref callbackCount)<warmupRequests)
        { if(ordered)host!.DrainCompletions(); await Task.Delay(1,ct); }
        // Reset measurements after warmup. Buffers, clients and callback arrays preallocated.
        peakEntries=peakInflight=peakCompletions=0; peakBytes=0;
        peakSendAge=peakCompletionAge=0;
        long alloc0=GC.GetTotalAllocatedBytes(true); var cpu0=process.TotalProcessorTime;
        int gc0=GC.CollectionCount(0),gc1=GC.CollectionCount(1),gc2=GC.CollectionCount(2);
        long measure=Stopwatch.GetTimestamp();
        start=measure-WarmupTicks*cadence;
        for(int t=WarmupTicks;t<WarmupTicks+measurementTicks;t++) await tick(t);
        while(Volatile.Read(ref callbackCount)<completions.Length)
        {
            if(ordered) { sample();host!.DrainCompletions(); }
            await Task.Delay(1,ct);
        }
        await Task.WhenAll(readers);
        double wall=Stopwatch.GetElapsedTime(measure).TotalSeconds;
        long alloc=GC.GetTotalAllocatedBytes(true)-alloc0; double cpu=(process.TotalProcessorTime-cpu0).TotalSeconds;
        var latency=new long[totalRequests];
        for(int i=0;i<totalRequests;i++) latency[i]=completions[i+warmupRequests]-enqueues[i+warmupRequests];
        double mb=(long)peers*measurementTicks*bytesPerTick/1_048_576d;
        Console.WriteLine($"mode={(ordered?"ordered":"vanilla")} batch={batch} wake={(edge?"edge":"counted")} pump={(activePump?"active":"all")} sendCancellation={(cancelPendingSocketSend?"token":"dispose")} peers={peers} sends_per_peer_per_tick={perPeerPerTick} callback_cap={maxCallbacks} requests={totalRequests} MiB={mb:F2} wall_s={wall:F3} req/s={totalRequests/wall:F0} MiB/s={mb/wall:F2} cpu_s={cpu:F3} alloc_MB={alloc/1e6:F2} bytes/request={(double)alloc/totalRequests:F1} GC={GC.CollectionCount(0)-gc0}/{GC.CollectionCount(1)-gc1}/{GC.CollectionCount(2)-gc2} cb_ms={Percentiles(latency)} producer_ms={Percentiles(producerTime)} pump_ms={Percentiles(pumpTime)} samples_peak_entries={peakEntries} samples_peak_retained={peakBytes} samples_peak_inflight={peakInflight} samples_peak_completions={peakCompletions} queue_age={(measureQueueAge?"on":"off")} samples_peak_send_age_ms={peakSendAge:F3} samples_peak_completion_age_ms={peakCompletionAge:F3} callbacks={callbackCount-warmupRequests} validated_bytes={mb:F2}MiB");
    }
    finally
    {
        foreach(var s in socks) s?.Close();
        foreach(var c in clients) c?.Dispose();
        if(host is not null) { using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)); await host.StopAsync(cleanupTimeout.Token); }
    }
}

static async Task DelayUntil(long deadline,CancellationToken ct)
{
    while(true)
    {
        double ms=(deadline-Stopwatch.GetTimestamp())*1000d/Stopwatch.Frequency;
        if(ms<=0) return;
        await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1,ms)),ct);
    }
}
static async Task ReadPeer(TcpClient c,byte expected,int bytes,CancellationToken ct)
{
    byte[] buffer=new byte[64*1024];
    var stream=c.GetStream();
    while(bytes>0)
    {
        int n=await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length,bytes)),ct);
        if(n==0) throw new EndOfStreamException();
        if(buffer.AsSpan(0,n).ContainsAnyExcept(expected)) throw new InvalidDataException("Payload mismatch");
        bytes-=n;
    }
}
static string Percentiles(long[] a)
{
    Array.Sort(a);
    double P(double x)=>a[(int)Math.Clamp(Math.Ceiling(a.Length*x)-1,0,a.Length-1)]*1000d/Stopwatch.Frequency;
    return $"{P(.5):F3}/{P(.95):F3}/{P(.99):F3}";
}
