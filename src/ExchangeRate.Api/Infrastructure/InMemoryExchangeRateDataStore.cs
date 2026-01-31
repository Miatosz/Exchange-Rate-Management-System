using System.Collections.Concurrent;
using ExchangeRate.Api.Models;
using ExchangeRate.Core.Enums;
using ExchangeRate.Core.Infrastructure;

namespace ExchangeRate.Api.Infrastructure;

/// <summary>
/// In-memory implementation of IExchangeRateDataStore.
/// Candidates can replace this with a real database implementation (e.g., EF Core).
/// </summary>
public class InMemoryExchangeRateDataStore : IExchangeRateDataStore
{
    private readonly ConcurrentDictionary<ExchangeRateKey, ExchangeRate.Core.Entities.ExchangeRate> _exchangeRates = new();
    private readonly ConcurrentDictionary<CurrencyTypes, ExchangeRate.Core.Entities.PeggedCurrency> _peggedCurrencies = new();

    public IQueryable<ExchangeRate.Core.Entities.ExchangeRate> ExchangeRates => _exchangeRates.Values.AsQueryable();


    public Task<List<ExchangeRate.Core.Entities.ExchangeRate>> GetExchangeRatesAsync(DateTime minDate, DateTime maxDate)
    {
        if (minDate > maxDate)
            throw new ArgumentException("minDate must be earlier than maxDate");
            
        var rates = _exchangeRates.Values
            .Where(x => x.Date.HasValue &&
                        x.Date.Value >= minDate &&
                        x.Date.Value < maxDate).ToList();
            
        return Task.FromResult(rates);
    }

    public Task SaveExchangeRatesAsync(IEnumerable<ExchangeRate.Core.Entities.ExchangeRate> rates)
    {
        ArgumentNullException.ThrowIfNull(rates);

        foreach (var rate in rates)
        {
            if (rate.Date is null || rate.CurrencyId is null || rate.Source is null || rate.Frequency is null)
                continue;

            var key = new ExchangeRateKey(
                rate.Date.Value,
                rate.CurrencyId.Value,
                rate.Source.Value,
                rate.Frequency.Value);

            _exchangeRates[key] = rate;

        }

        return Task.CompletedTask;
    }

    public List<ExchangeRate.Core.Entities.PeggedCurrency> GetPeggedCurrencies()
    {
        return _peggedCurrencies.Values.ToList();
    }

    public void AddPeggedCurrency(ExchangeRate.Core.Entities.PeggedCurrency peggedCurrency)
    {
        ArgumentNullException.ThrowIfNull(peggedCurrency);

        if (!peggedCurrency.CurrencyId.HasValue)
            throw new ArgumentException("CurrencyId cannot be null", nameof(peggedCurrency));
            
        if (!_peggedCurrencies.TryAdd(peggedCurrency.CurrencyId.Value, peggedCurrency))
        {
            throw new InvalidOperationException($"Pegged currency with ID {peggedCurrency.CurrencyId} already exists");
        }
    }
}