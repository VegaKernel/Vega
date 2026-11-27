namespace Vega.PluginSdk;

/// <summary>
/// Optional assembly declaration of the SDK source/API generation the plugin was
/// built against. A matching generation is a compatibility check, not a stable
/// binary ABI guarantee; compile and qualify against the deployed Vega build.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false, Inherited = false)]
public sealed class VegaPluginApiGenerationAttribute(int generation) : Attribute
{
    public const int Current = 1;

    public int Generation { get; } = generation;
}
