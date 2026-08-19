namespace Vega.Contracts.Runtime;

public enum RuntimeLifecycleStage
{
    ProcessStart = 0,
    Bootstrap = 1,
    RuntimeReady = 2,
    ModulesRegistered = 3,
    WorldLoading = 4,
    WorldReady = 5,
    NetworkReady = 6,
    BackgroundWarmup = 7,
    FullyWarmed = 8
}
