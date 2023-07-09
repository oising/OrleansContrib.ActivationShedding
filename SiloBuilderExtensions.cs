using System;
using JetBrains.Annotations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OrleansContrib.ActivationShedding;

// ReSharper disable once CheckNamespace
namespace Orleans.Hosting
{
    [UsedImplicitly]
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
        // ReSharper disable once MemberCanBePrivate.Global
        public static ISiloBuilder UseActivationShedding(this ISiloBuilder siloBuilder, Action<ActivationSheddingOptions> options)
        {
            siloBuilder.ConfigureServices((collection) =>
            {
                // get a reference to Configuration
                var configuration = collection.BuildServiceProvider().GetRequiredService<IConfiguration>();
                var section = configuration.GetSection("ActivationShedding");
                if (!section.Exists())
                {
                    throw new ArgumentException("Configuration section 'ActivationShedding' is missing.");
                }
                
                collection.AddOptions<ActivationSheddingOptions>()
                    .Bind(section)
                    // ReSharper disable once ConvertClosureToMethodGroup
                    .PostConfigure(sheddingOptions => options(sheddingOptions))
                    .ValidateDataAnnotations();
            });
            
            siloBuilder.AddIncomingGrainCallFilter<ActivationSheddingFilter>();
            
            return siloBuilder;
        }
    }
}