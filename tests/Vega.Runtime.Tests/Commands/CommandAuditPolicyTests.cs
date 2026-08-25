using NUnit.Framework;
using Vega.Contracts.Commands;

namespace Vega.Runtime.Tests.Commands;

[TestFixture]
public sealed class CommandAuditPolicyTests
{
    [Test]
    public void DescriptorDefaultsToNameOnlyAudit()
    {
        var descriptor = new CommandDescriptor(
            "login",
            Array.Empty<string>(),
            Array.Empty<string>());

        Assert.That(descriptor.AuditMode, Is.EqualTo(CommandAuditMode.NameOnly));
    }

    [TestCase(CommandAuditMode.NameOnly)]
    [TestCase(CommandAuditMode.NameAndArguments)]
    [TestCase(CommandAuditMode.None)]
    public void DescriptorPreservesExplicitAuditMode(CommandAuditMode auditMode)
    {
        var descriptor = new CommandDescriptor(
            "test",
            Array.Empty<string>(),
            Array.Empty<string>(),
            AuditMode: auditMode);

        Assert.That(descriptor.AuditMode, Is.EqualTo(auditMode));
    }
}
