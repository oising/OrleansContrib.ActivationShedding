using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Runtime;

namespace OrleansContrib.ActivationShedding
{
    [UsedImplicitly]
    public sealed class ActivationSheddingFilter : IIncomingGrainCallFilter, IDisposable
    {
        private readonly IClusterMembershipService _clusterMembershipService;
        private readonly IGrainMigrationEligibilityCheck _eligibilityCheck;
        private readonly ILogger<ActivationSheddingFilter> _logger;
        private readonly CancellationTokenSource _cts;
        private readonly IManagementGrain _managementGrain;
        private readonly SiloAddress _currentSilo;
        private readonly ActivationSheddingOptions _options;
        private HashSet<SiloAddress> _activeSilos;
        private int _surplusActivations;
        private bool _isRebalancing;

        private readonly SemaphoreSlim _lock = new(1, 1);

        private static readonly EventId StartEvent = new(58001, "Starting");
        private static readonly EventId SheddingEvent = new(58002, "Shedding");
        private static readonly EventId StopEvent = new(58003, "Stopping");
        
        public ActivationSheddingFilter(
            ILogger<ActivationSheddingFilter> logger,
            IOptions<ActivationSheddingOptions> digitalTwinOptions,
            IGrainFactory grainFactory,
            IGrainRuntime runtime,
            ILocalSiloDetails localSiloDetails,
            IClusterMembershipService clusterMembershipService,
            IGrainMigrationEligibilityCheck eligibilityCheck)
        {
            _logger = logger;
            _options = digitalTwinOptions.Value;
            _clusterMembershipService = clusterMembershipService;
            _eligibilityCheck = eligibilityCheck;
            _currentSilo = localSiloDetails.SiloAddress;
            _managementGrain = grainFactory.GetGrain<IManagementGrain>(0);
            _cts = new CancellationTokenSource();
            _activeSilos = new HashSet<SiloAddress>();

            Initialize();
        }

        /// <inheritdoc />
        public async Task Invoke(IIncomingGrainCallContext context)
        {
            if (_surplusActivations > 0 &&
                context.Grain is not SystemTarget &&
                context.Grain is IGrainBase grain &&
                _eligibilityCheck.ShouldBeMigrated(grain))
            {
                _ = Interlocked.Decrement(ref _surplusActivations);

                // allow allow placement strategy to relocate grain
                grain.MigrateOnIdle();
            }

            await context.Invoke();
        }

        private void Initialize()
        {
            // watch for silo arrival/departure
            MonitorClusterChanges().Ignore();
            
            var interval = TimeSpan.FromSeconds(_options.TimerIntervalSeconds);

            TaskUtility.RepeatEvery(UpdateStatistics, interval, _cts.Token, _logger).Ignore();
        }

        private async Task MonitorClusterChanges()
        {
            var snapshotUpdates = _clusterMembershipService.MembershipUpdates;
            
            await foreach (var snapshot in snapshotUpdates.WithCancellation(_cts.Token))
            {
                try
                {
                    await _lock.WaitAsync();
                    
                    var activeSilos = (
                        from member in snapshot.Members.Values
                        where member.Status == SiloStatus.Active
                        select member.SiloAddress
                    ).ToHashSet();

                    if (_activeSilos.SetEquals(activeSilos))
                    {
                        continue;
                    }

                    // update with new cluster info
                    _activeSilos = activeSilos;
                }
                finally
                {
                    _lock.Release();
                }

                // immediately update stats
                await UpdateStatistics();
            }
        }
        
        private async Task UpdateStatistics()
        {
            await _lock.WaitAsync();

            try
            {
                // only work with two or more silos
                if (_activeSilos.Count > 1 )
                {
                    var activeSilos = _activeSilos.ToArray();
                    var stats = await _managementGrain.GetRuntimeStatistics(activeSilos);

                    int totalActivations = 0;
                    int myActivations = 0;
                    double myPercentage;
                    double targetPercentage;
                    double overagePercentTrigger;
                    double overagePercent;
                    
                    // compute the cluster grain activation ratios
                    void ComputeRatios()
                    {
                        // e.g. if I have 1200 of 5000 activations, I own 24%
                        myPercentage = Math.Floor(((double)myActivations / totalActivations) * 100);

                        // e.g. for three silos, 33% - the average each should aim to have
                        targetPercentage = Math.Floor(100d / activeSilos.Length);

                        // e.g. 20% overage = 33% (1/3) + 20% = 53% would be the trigger
                        overagePercentTrigger = _options.BaselineTriggerPercentage;

                        // scale overagePercent by silo count, if > 2
                        // this goes something like: 2:20, 3:16, 4:12, 5:8, 6:4, n:2 for siloCount:percentOverage
                        if (_activeSilos.Count > 2)
                        {
                            overagePercentTrigger = (overagePercentTrigger * (1 + ((2 - _activeSilos.Count) * 0.2)));
                            if (overagePercentTrigger < 2)
                            {
                                overagePercentTrigger = 2;
                            }
                        }

                        overagePercent = Math.Floor(myPercentage - targetPercentage);
                    }

                    // figure out how many grains we should cull / shed
                    void UpdateCullingData()
                    {
                        double averageActivationsPerSilo = Math.Floor((double)totalActivations / activeSilos.Length);

                        // update counter (i.e. we set the "recovery" point at 95% of the overage activations beyond target)
                        int surplusActivations =
                            (int)Math.Floor((myActivations - averageActivationsPerSilo) * _options.LowerRecoveryThresholdFactor);
                        
                        _ = Interlocked.Exchange(ref _surplusActivations, surplusActivations);
                    }
                    
                    // compute total activations for overall threshold, and snag this silo's specifically
                    for (int index = 0; index < _activeSilos.Count; index++)
                    {
                        var stat = stats[index];
                        totalActivations += stat.ActivationCount;
                        if (activeSilos[index].Equals(_currentSilo))
                        {
                            myActivations = stat.ActivationCount;
                        }
                    }
                    
                    // validate against an absolute threshold for cluster-level activations
                    if (totalActivations > _options.TotalGrainActivationsMinimumThreshold)
                    {
                        ComputeRatios();
                        
                        // am I above the average expected?  (e.g. > 33% for 3 silos)
                        if (myPercentage > targetPercentage)
                        {
                            // by how much? are we over by more than the overage trigger?
                            if (overagePercent >= overagePercentTrigger)
                            {
                                // only emit event if not already rebalancing
                                if (!_isRebalancing)
                                {
                                    _isRebalancing = true;
                                    
                                    EmitRebalancingEvent(totalActivations,
                                        myActivations,
                                        overagePercent,
                                        overagePercentTrigger,
                                        StartEvent);
                                }

                                UpdateCullingData();
                            }
                            else
                            {
                                // we're over target but under threshold - if already rebalancing, then stop if no more surplus activations
                                if (_isRebalancing && _surplusActivations <= 0)
                                {
                                    // this may be less than zero due to timing (We don't lock in invoke, perf)
                                    Interlocked.Exchange(ref _surplusActivations, 0);
                                    _isRebalancing = false;
                                }
                            }
                        }
                        else
                        {
                            // we've dropped below the threshold, reset surplus if required
                            if (_isRebalancing)
                            {
                                Interlocked.Exchange(ref _surplusActivations, 0);
                                _isRebalancing = false;
                            }
                        }
                        
                        // dump stats for this interval
                        EmitRebalancingEvent(totalActivations,
                            myActivations,
                            overagePercent,
                            overagePercentTrigger,
                            _isRebalancing
                                ? SheddingEvent
                                : StopEvent);
                    }
                }
            }
            finally
            {
                _lock.Release();
            }
        }

        private void EmitRebalancingEvent(int totalActivations,
            int myActivations,
            double overagePercent,
            double overagePercentTrigger,
            EventId phase)
        {
            var customDimensions = new Dictionary<string, string>()
            {
                { "orleans.silo.rebalancingPhase", phase.Name ?? "<unnamed>"}, // started -> shedding -> stopped
                { "orleans.silo", $"{_currentSilo.ToString()}" },
                { "orleans.cluster.siloCount", _activeSilos.Count.ToString() },
                { "orleans.cluster.totalActivations", totalActivations.ToString() },
                { "orleans.silo.activations", myActivations.ToString() },
                { "orleans.silo.activationsToCull", _surplusActivations.ToString() },
                { "orleans.silo.overagePercent", $"{overagePercent}%" },
                { "orleans.silo.overageThresholdPercent", $"{overagePercentTrigger}%" }
            };
            
            _logger.LogInformation(phase, "Silo Activation Shedding {@CustomDimensions}", customDimensions);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            _cts.Dispose();
        }
    }
}