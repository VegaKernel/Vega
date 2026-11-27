using System.Globalization;
using System.Net;
using System.Net.Sockets;
using TerraRuntime.Schematics;
using TerrariaRuntime;
using Vega.Contracts.Scenes;
using Vega.Contracts.Threading;
using Vega.Contracts.Worlds;

namespace Vega.Terraria.Worlds;

/// <summary>One-shot native template materialization, with no runtime modules or account services.</summary>
internal static class TerrariaSchematicTemplateHost
{
    public static int Build(string worldPath, SchematicDocument document, SceneTileArea tiles, SceneBounds bounds, long maximumBytes)
    {
        string path = Path.GetFullPath(worldPath);
        string directory = Path.GetDirectoryName(path)!;
        string saveDirectory = Path.Combine(directory, "saves");
        Directory.CreateDirectory(saveDirectory);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        string[] args = ["-world", path, "-savedirectory", saveDirectory, "-ip", "127.0.0.1", "-port",
            port.ToString(CultureInfo.InvariantCulture), "-maxplayers", "1", "-password", Guid.NewGuid().ToString("N"), "-noupnp"];
        bool completed = false;
        void OnReady()
        {
            try
            {
                if (global::Terraria.Main.ActiveWorldFileData is not { IsCloudSave: false } active ||
                    !string.Equals(Path.GetFullPath(active.Path), path, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The native server did not load the fresh staged world.");

                // The native ready hook runs on DedServ's owning thread, before its first tick.
                var placement = new TerrariaSchematicPlacement(new OwningThread(), new WorldId("schematic-build"));
                var result = placement.Place(document, tiles, bounds);
                if (result.Status != WorldSchematicPlacementStatus.Applied)
                    throw new InvalidOperationException(result.Error ?? result.Status.ToString());
                bool saved = false;
                Exception? saveFailure = null;
                void OnSave(bool success, Exception error) { saved = success; saveFailure = error; }
                Hooks.Main.DedicatedWorldSaveCompleted += OnSave;
                try { global::Terraria.IO.WorldFile.SaveWorld(resetTime: false, useTemps: false, canBeSkipped: false); }
                finally { Hooks.Main.DedicatedWorldSaveCompleted -= OnSave; }
                if (!saved || new FileInfo(path).Length is <= 0 || new FileInfo(path).Length > maximumBytes)
                    throw new IOException("The native save did not complete within the template budget.", saveFailure);
                // The controller accepts only a successful exited child with this marker. A
                // native exit status alone is not evidence that placement and validated save ran.
                using var marker = new FileStream(Path.Combine(directory, "complete"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                marker.WriteByte(1);
                marker.Flush(flushToDisk: true);
                completed = true;
                Console.WriteLine($"Native schematic template saved with {result.NpcsPlaced} NPCs.");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Native schematic placement/save failed: {ex.Message}");
            }
            finally
            {
                global::Terraria.Netplay.SaveOnServerExit = false;
                global::Terraria.Netplay.Disconnect = true;
            }
        }

        Hooks.Main.DedicatedWorldReady += OnReady;
        try
        {
            TerrariaServerHost.PrepareSavePath(args);
            TerrariaServerHost.InitialiseTerrariaInternals();
            TerrariaServerHost.LaunchNativeServer(args);
            return completed ? 0 : 1;
        }
        finally { Hooks.Main.DedicatedWorldReady -= OnReady; }
    }

    private sealed class OwningThread : IGameThreadDispatcher
    {
        private readonly int id = Environment.CurrentManagedThreadId;
        public bool IsGameThreadKnown => true;
        public bool IsGameThread => Environment.CurrentManagedThreadId == id;
        public void AssertGameThread() { if (!IsGameThread) throw new InvalidOperationException("Native template mutation must stay on the owning thread."); }
        public bool TryPost(string name, Action action) => false;
        public bool TrySchedule(string name, Func<bool> step) => false;
    }
}
