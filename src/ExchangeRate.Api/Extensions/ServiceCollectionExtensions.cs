using ExchangeRate.Api.Infrastructure;
using ExchangeRate.Core;
using ExchangeRate.Core.Infrastructure;
using ExchangeRate.Core.Interfaces;
using ExchangeRate.Core.Interfaces.Providers;
using ExchangeRate.Core.Models;
using ExchangeRate.Core.Providers;

namespace ExchangeRate.Api.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        services.AddSingleton<EUECBExchangeRateProvider>(sp =>
        {
            var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(EUECBExchangeRateProvider));
            var config = sp.GetRequiredService<ExternalExchangeRateApiConfig>();
            return new EUECBExchangeRateProvider(httpClient, config);
        });

        services.AddSingleton<MXCBExchangeRateProvider>(sp =>
        {
            var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(MXCBExchangeRateProvider));
            var config = sp.GetRequiredService<ExternalExchangeRateApiConfig>();
            return new MXCBExchangeRateProvider(httpClient, config);
        });

        services.AddSingleton<IExchangeRateProvider>(sp => sp.GetRequiredService<EUECBExchangeRateProvider>());
        services.AddSingleton<IExchangeRateProvider>(sp => sp.GetRequiredService<MXCBExchangeRateProvider>());

        // Register the provider factory
        services.AddSingleton<IExchangeRateProviderFactory, ExchangeRateProviderFactory>();

        // Register the data store - this can be replaced by candidates with a real DB implementation
        services.AddSingleton<IExchangeRateDataStore, InMemoryExchangeRateDataStore>();

        // Register the repository
        services.AddSingleton<IExchangeRateRepository, ExchangeRateRepository>();
        
        return services;
    }
}