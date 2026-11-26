using NUnit.Framework;
using Vega.Contracts.Networking;
using Vega.Modules.Administration;
using Vega.Runtime;
using Vega.Terraria.Hooking;
using Vega.Terraria.Networking;

namespace Vega.Runtime.Tests.Commands;

[TestFixture]
public sealed class WhitelistTests
{
    [Test]
    public async Task SharedNetworksMatchEffectiveEndpointsAndRefreshAcrossWorlds()
    {
        string root = Path.Combine(Path.GetTempPath(), "vega-whitelist-network-tests", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root, "whitelist.json");
        try
        {
            var primary = new ConnectionWhitelist(path);
            var sandbox = new ConnectionWhitelist(path);
            Assert.That(await primary.SetAddressAsync("192.0.2.0/24", true), Is.True);
            Assert.That(await primary.SetAddressAsync("2001:db8::/32", true), Is.True);
            Assert.That(await primary.SetEnabledAsync(true), Is.True);
            Assert.That(sandbox.Evaluate(Connection("192.0.2.42:7777")).Kind, Is.EqualTo(PacketDecisionKind.Allow));
            Assert.That(sandbox.Evaluate(Connection("::ffff:192.0.2.42")).Kind, Is.EqualTo(PacketDecisionKind.Allow));
            Assert.That(sandbox.Evaluate(Connection("2001:db8::123")).Kind, Is.EqualTo(PacketDecisionKind.Allow));
            Assert.That(sandbox.Evaluate(Connection("198.51.100.42")).Kind, Is.EqualTo(PacketDecisionKind.Reject));
            Assert.That(await primary.SetAddressAsync("192.0.2.0/24", false), Is.True);
            Assert.That(sandbox.Evaluate(Connection("192.0.2.42")).Kind, Is.EqualTo(PacketDecisionKind.Reject));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Test]
    public void OfflineTShockWhitelistImportPreservesSourceAndRefusesOverwrite()
    {
        string root = Path.Combine(Path.GetTempPath(), "vega-whitelist-import", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string source = Path.Combine(root, "whitelist.txt");
        try
        {
            File.WriteAllText(source, "# Example\n127.0.0.1\n192.0.2.0/24\n");
            byte[] original = File.ReadAllBytes(source);
            Assert.That(OfflineMigrationCommands.TryRun(["--vega-import-tshock-whitelist", source, "on"], root), Is.Zero);
            var store = new ConnectionWhitelist(Path.Combine(root, "data", "whitelist.json"));
            Assert.That(store.Enabled, Is.True);
            Assert.That(store.Evaluate(Connection("192.0.2.42")).Kind, Is.EqualTo(PacketDecisionKind.Allow));
            Assert.That(OfflineMigrationCommands.TryRun(["--vega-import-tshock-whitelist", source, "off"], root), Is.EqualTo(2));
            Assert.That(store.Evaluate(Connection("198.51.100.42")).Kind, Is.EqualTo(PacketDecisionKind.Reject));
            Assert.That(File.ReadAllBytes(source), Is.EqualTo(original));
        }
        finally { Directory.Delete(root, true); }
    }
    [Test]
    public async Task EnabledWhitelistUsesEffectiveIpAndSurvivesRestart()
    {
        string root = Path.Combine(Path.GetTempPath(), "vega-whitelist-tests", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root, "whitelist.json");
        try
        {
            var whitelist = new ConnectionWhitelist(path);
            Assert.That(whitelist.Evaluate(Connection("192.0.2.10")).Kind, Is.EqualTo(PacketDecisionKind.Allow));
            Assert.That(await whitelist.SetAddressAsync("192.0.2.10", true), Is.True);
            Assert.That(await whitelist.SetAddressAsync("::ffff:192.0.2.10", true), Is.False);
            Assert.That(await whitelist.SetEnabledAsync(true), Is.True);
            var reopened = new ConnectionWhitelist(path);
            Assert.That(reopened.Evaluate(Connection("192.0.2.10:7777")).Kind, Is.EqualTo(PacketDecisionKind.Allow));
            Assert.That(reopened.Evaluate(Connection("::ffff:192.0.2.10")).Kind, Is.EqualTo(PacketDecisionKind.Allow));
            Assert.That(reopened.Evaluate(Connection("192.0.2.11")).Kind, Is.EqualTo(PacketDecisionKind.Reject));
            Assert.That(await reopened.SetAddressAsync("192.0.2.10", false), Is.True);
            Assert.That(reopened.Evaluate(Connection("192.0.2.10")).Kind, Is.EqualTo(PacketDecisionKind.Reject));
            Assert.That(await reopened.SetEnabledAsync(false), Is.True);
            Assert.That(reopened.Evaluate(Connection(null)).Kind, Is.EqualTo(PacketDecisionKind.Allow));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Test]
    public async Task ChangesTakeEffectInAlreadyRunningInstancesAndWritesMerge()
    {
        string root = Path.Combine(Path.GetTempPath(), "vega-whitelist-tests", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root, "whitelist.json");
        try
        {
            var primary = new ConnectionWhitelist(path);
            var sandbox = new ConnectionWhitelist(path);
            Assert.That(sandbox.Evaluate(Connection("192.0.2.10")).Kind, Is.EqualTo(PacketDecisionKind.Allow));

            await primary.SetAddressAsync("192.0.2.10", true);
            await primary.SetEnabledAsync(true);
            Assert.That(sandbox.Evaluate(Connection("192.0.2.10")).Kind, Is.EqualTo(PacketDecisionKind.Allow));
            Assert.That(sandbox.Evaluate(Connection("192.0.2.11")).Kind, Is.EqualTo(PacketDecisionKind.Reject));

            Task<bool>[] writes = Enumerable.Range(11, 12).Select(i =>
                (i % 2 == 0 ? primary : sandbox).SetAddressAsync($"192.0.2.{i}", true).AsTask()).ToArray();
            Assert.That(await Task.WhenAll(writes), Is.All.True);
            var snapshot = await primary.ReadAsync();
            Assert.That(snapshot.Enabled, Is.True);
            Assert.That(snapshot.Addresses, Is.EquivalentTo(Enumerable.Range(10, 13).Select(i => $"192.0.2.{i}")));
            Assert.That((await sandbox.ReadAsync()).Addresses, Is.EqualTo(snapshot.Addresses));

            await sandbox.SetAddressAsync("192.0.2.10", false);
            Assert.That(primary.Evaluate(Connection("192.0.2.10")).Kind, Is.EqualTo(PacketDecisionKind.Reject));
            await sandbox.SetEnabledAsync(false);
            Assert.That(primary.Evaluate(Connection("192.0.2.10")).Kind, Is.EqualTo(PacketDecisionKind.Allow));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestCase("{")]
    [TestCase("{\"addresses\":[]}")]
    [TestCase("{\"enabled\":false,\"addresses\":[\"invalid\"]}")]
    public async Task InvalidStateRejectsNewAdmissionAndDoesNotOverwriteLastValidOrDisk(string invalid)
    {
        string root = Path.Combine(Path.GetTempPath(), "vega-whitelist-tests", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root, "whitelist.json");
        try
        {
            var primary = new ConnectionWhitelist(path);
            var sandbox = new ConnectionWhitelist(path);
            await primary.SetAddressAsync("192.0.2.10", true);
            Assert.That(sandbox.Evaluate(Connection("192.0.2.10")).Kind, Is.EqualTo(PacketDecisionKind.Allow));
            string valid = File.ReadAllText(path);
            File.WriteAllText(path, invalid);

            Assert.That(primary.Evaluate(Connection("192.0.2.10")).Reason, Is.EqualTo("admission.whitelist_unavailable"));
            Assert.That(sandbox.Evaluate(Connection("192.0.2.10")).Kind, Is.EqualTo(PacketDecisionKind.Reject));
            Assert.That(async () => await sandbox.SetEnabledAsync(true), Throws.Exception);
            Assert.That(async () => await primary.ReloadAsync(), Throws.Exception);
            Assert.That(File.ReadAllText(path), Is.EqualTo(invalid));
            Assert.That(primary.Addresses, Is.EqualTo(new[] { "192.0.2.10" }));
            Assert.That(sandbox.Addresses, Is.EqualTo(primary.Addresses));

            File.WriteAllText(path, valid);
            Assert.That(sandbox.Evaluate(Connection("192.0.2.10")).Kind, Is.EqualTo(PacketDecisionKind.Allow));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Test]
    public async Task LostInitializedStateIsNotRecreatedByExistingOrNewInstances()
    {
        string root = Path.Combine(Path.GetTempPath(), "vega-whitelist-tests", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root, "whitelist.json");
        try
        {
            var primary = new ConnectionWhitelist(path);
            var sandbox = new ConnectionWhitelist(path);
            await primary.SetAddressAsync("192.0.2.10", true);
            await primary.SetEnabledAsync(true);
            Assert.That(sandbox.Evaluate(Connection("192.0.2.10")).Kind, Is.EqualTo(PacketDecisionKind.Allow));
            File.Delete(path);

            Assert.That(primary.Evaluate(Connection("192.0.2.10")).Kind, Is.EqualTo(PacketDecisionKind.Reject));
            Assert.That(sandbox.Evaluate(Connection("192.0.2.10")).Kind, Is.EqualTo(PacketDecisionKind.Reject));
            Assert.That(async () => await sandbox.SetEnabledAsync(false), Throws.InstanceOf<IOException>());
            Assert.That(() => new ConnectionWhitelist(path), Throws.InstanceOf<IOException>());
            Assert.That(File.Exists(path), Is.False);
            Assert.That(primary.Enabled && sandbox.Enabled, Is.True);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Test]
    public void AdmissionIsAtPreWorldBoundaryAndDoesNotEjectExistingSessions()
    {
        Assert.That(NativePlayerPacketSecurityHooks.ShouldApplyAdmission(TerrariaConnectionState.ProtocolAccepted,
            global::Terraria.ID.MessageID.RequestWorldData), Is.True);
        Assert.That(NativePlayerPacketSecurityHooks.ShouldApplyAdmission(TerrariaConnectionState.FullyConnected,
            global::Terraria.ID.MessageID.RequestWorldData), Is.False);
    }

    private static ConnectionMetadataSnapshot Connection(string? effective) => new(new ConnectionId(1), "127.0.0.1", effective,
        ConnectionForwardingProvenance.TrustedForwarder, ClientPlatformInfo.Unknown, null, ConnectionLoginPhase.ProtocolAccepted);
}
