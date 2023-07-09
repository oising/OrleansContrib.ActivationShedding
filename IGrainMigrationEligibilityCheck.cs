using Orleans;

namespace OrleansContrib.ActivationShedding
{
    /// <summary>
    /// Defines an implementation that will decide which grains are eligible to be migrated on a silo that is shedding activations.
    /// </summary>
    public interface IGrainMigrationEligibilityCheck
    {
        /// <summary>
        /// Called to decide if a grain is eligible to be migrated on a silo that is shedding activations.
        /// Typically one might decide to filter on the grain's Type namespace, e.g. Foobar.Grains.*
        /// </summary>
        /// <param name="grain">The contextual grain to check</param>
        /// <returns>True if we can try to migrate the grain, false if not.</returns>
        bool ShouldBeMigrated(IGrainBase grain);
    }
}