using NUnit.Framework;

namespace Vega.Runtime.Tests.Core;

[SetUpFixture]
public sealed class PluginSdkWorldTestEnvironment
{
    private static readonly string Root = Path.Combine(
        Path.GetTempPath(),
        "vega-plugin-sdk-world-tests");

    [OneTimeSetUp]
    public void EnsureRootExists() => Directory.CreateDirectory(Root);

    [OneTimeTearDown]
    public void RemoveRootIfPresent()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}
