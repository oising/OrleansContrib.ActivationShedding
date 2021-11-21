using System;
using Microsoft.Extensions.DependencyInjection;
using OrleansContrib.ActivationShedding;

// ReSharper disable once CheckNamespace
namespace Orleans.Hosting
{
    public static class SiloBuilderExtensions
    {
        /// <summary>
        /// Enable grain activation shedding (rebalancing)
        /// </summary>
        public static ISiloBuilder UseActivationShedding(this ISiloBuilder siloBuilder)
        {
            return UseActivationShedding(siloBuilder, _ => { });
        }
        
        /// <summary>
        /// Enable grain activation shedding (rebalancing)
        /// </summary>
        public static ISiloBuilder UseActivationShedding(this ISiloBuilder siloBuilder, Action<ActivationSheddingOptions> options)
        {
            siloBuilder.ConfigureServices(((context, collection) =>
            {
                collection.AddOptions<ActivationSheddingOptions>()
                    .Bind(context.Configuration.GetSection("ActivationShedding"))
                    // ReSharper disable once ConvertClosureToMethodGroup
                    .PostConfigure(sheddingOptions => options(sheddingOptions))
                    .ValidateDataAnnotations();
            }));
            
            siloBuilder.AddIncomingGrainCallFilter<ActivationSheddingFilter>();
            
            return siloBuilder;
        }
    }
}