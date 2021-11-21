using Orleans;

namespace Hilo.Sys.Orleans.GrainActivationBalancing
{
    /// <summary>
    /// Defines an implementation that will decide which grains are eligible to be deactivated on a silo that is rebalancing activations.
    /// </summary>
    public interface IGrainDeactivationEligibilityCheck
    {
        /// <summary>
        /// Called to decide if a grain is eligible to be deactivated on a silo that is rebalancing activations.
        /// Typically one might decide to filter on the grain's Type namespace, e.g. Foobar.Grains.*
        /// </summary>
        /// <param name="grain">The contextual grain to check</param>
        /// <returns>True if we can deactivate the grain, false if not.</returns>
        bool CanDeactivate(Grain grain);
    }
}