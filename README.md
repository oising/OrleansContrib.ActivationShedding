# Orleans Grain Activation Shedding

The purpose of this library is to help in scenarios where the number of silos in your cluster changes at runtime, either through dynamic scaling for load (e.g. K8S, Azure VMSS or other cloud equivalents) or during a rolling upgrade whereby silos are brought up and down one at a time to deploy new builds, or whatever your strategy might be. This can lead to a situation where the activations on your cluster may be distributed unevenly, causing one or more silos to be overloaded while others lay mostly unused. This situation is particularly prevalent when using Orleans Virtual Streams, as selectively controlling the ingress of requests during an upgrade is useless here as the Virtual Stream infrastructure is a "pull" model, so the moment a silo comes up, it will start activating grains. 

The reason this happens is because once a grain is "activated," it is not eligible to be moved to another silo until it is "deactivated" (unloaded.) Grain deactivation is typically non-deterministic and is governed by multiple factors. Grains _can_ be deactivated explicitly through code, and this library takes advantage of that to move grains from one silo to another according to a set of configurable parameters. 

### Requirements

- netcore 3.1 or net 5.0 (same as Orleans 3.x targeting)
- serilog 3.x+ to allow destructuring of objects into structured metrics to a logging provider 

This project uses Orleans 3.5.1 libraries

This project is configured for NuGet package generation, but I'm holding off on publishing until I get some more feedback from testers.

### How this feature works

The behaviour is controlled by configuration of the `ActivationSheddingOptions` options class. This is managed by the standard dotnet configuration plumbing. 

```c#
/// <summary>
/// Options to configure the grain activation shedding feature.
/// </summary>
public class ActivationSheddingOptions
{
    /// <summary>
    /// Minimum number of active grains in the cluster (global) before we should consider rebalancing.
    /// <remarks>The default is 5000</remarks>
    /// </summary>
    [Range(500, int.MaxValue)]
    public int TotalGrainActivationsMinimumThreshold { get; set; } = 5000;

    /// <summary>
    /// This is the baseline percentage overage for triggering rebalancing two silos.
    /// This number is scaled down as the number of silos increases, to a minimum value of 2 (%).
    /// <remarks>The default is 20%</remarks>
    /// </summary>
    [Range(1, 99)]
    public int BaselineTriggerPercentage { get; set; } = 20;

    /// <summary>
    /// How close we should get to the target value of activations before considering stopping the shedding process.
    /// <remarks>The default is 95%</remarks> 
    /// </summary>
    [Range(0.1, 1)]
    public double LowerRecoveryThresholdFactor { get; set; } = 0.95;

    /// <summary>
    /// How often we should (re)calculate the (potential) surplus activations on a silo. The interval is in seconds.
    /// <remarks>The default interval is 10 seconds.</remarks>
    /// </summary>
    [Range(5, int.MaxValue)]
    public int TimerIntervalSeconds { get; set; } = 10;
}
```
By default, every ten seconds (`TimerIntervalSeconds`) we will evaluate the state of the cluster. We won't do any work unless the total number of activations across the cluster is at least 5000 (`TotalGrainActivationsMinimumThreshold`).

Assuming a cluster with two silos (we autodetect the number of actual silos, and monitor the cluster for changes in real time), the baseline overage trigger percentage is 20% (`BaselineTriggerPercentage`) but this is scaled _down_ as the cluster size goes _up_, to a minimum of 2%.

Taking the two silo example with the default configuration, if we find that one silo has more than 20% over the desired "target" percentage of 50% (i.e. 100% / 2 silos = 50%), i.e. it has 70% of the share, then the silo with the greater share of activations will start "shedding" them. In practice, an incoming [Grain Call Filter](https://dotnet.github.io/orleans/docs/grains/interceptors.html) will deactivate the grain after it is called. **This means that grains are only shed from the silo if they are called at least once during rebalancing period.** 

### How to use this feature

Ideally you should be familiar with [Orleans Load Balancing](https://dotnet.github.io/orleans/docs/implementation/load_balancing.html), in particular the **ActivationCountBasedPlacement** strategy. This is employed by applying an attribute to grain types that you wish to be placed according to the number of activations on target silos. When a grain is shed from a silo, you would ideally want the cluster to next activate the grain on a less loaded silo that it was originally on. 

> NOTE
> 
> If you use `[PreferLocalPlacement]` on some of your grains to ensure locality for a call chain, you need only decorate the entry point grain with `[ActivationCountBasedPlacement]`. If this grain is shed from the silo, it will be moved to a less loaded silo and the locally placed grains will almost certainly be shed on the next call and they will follow their caller's placement. 

The first thing you'll need to do is to implement a simple class for determining which grains you want to declare as eligible for shedding, then register it in DI. The simplest implementation could be to filter on the grain type namespace:

```c#
public class MyGrainsOnlyEligibilityCheck : IGrainDeactivationEligibilityCheck
{
    /// <inheritdoc />
    public bool CanDeactivate(Grain grain) => grain.IdentityString.StartsWith("MyNamespace.Grains");
}
```

The grain call filter will not consider SystemTargets (internal Orleans grains) for deactivation even if you decide to return `true` here. 

To use the default configuration, call `UseActivationShedding()` on your `SiloBuilder`. If you wish to tweak the parameters, there is an overload that will allow you to change values on the `ActivationSheddingOptions` class. These values can also be set in appsettings.json or as environment variables, as per the standard dotnet configuration process.

### Metrics and Logging

Internally I was logging CustomEvent data directly to an App Insights Telemetry Client, but for this public release I've reverted to using the standard ILogging infra from Microsoft. That said, you will need to use [Serilog](https://github.com/serilog/serilog-extensions-logging) to correctly destructure the metrics object into a structured format in your chosen target logging system (e.g. Seq, ZipKind, Azure Monitor etc.)

```c#
var customDimensions = new Dictionary<string, string>()
{
    { "orleans.silo.rebalancingPhase", phase.Name}, // started -> rebalancing -> stopped
    { "orleans.silo", $"{_currentSilo.ToLongString()}" },
    { "orleans.cluster.siloCount", _activeSilos.Count.ToString() },
    { "orleans.cluster.totalActivations", totalActivations.ToString() },
    { "orleans.silo.activations", myActivations.ToString() },
    { "orleans.silo.activationsToCull", _surplusActivations.ToString() },
    { "orleans.silo.overagePercent", $"{overagePercent}%" },
    { "orleans.silo.overageThresholdPercent", $"{overagePercentTrigger}%" }
};

_logger.LogInformation(phase, "Silo Activation Shedding {@CustomDimensions}", customDimensions);
```