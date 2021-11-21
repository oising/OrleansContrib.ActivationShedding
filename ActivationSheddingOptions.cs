using System.ComponentModel.DataAnnotations;

namespace OrleansContrib.ActivationShedding
{
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
        /// How close we should get to the target value of activations before considering stopping the rebalancing process.
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
}