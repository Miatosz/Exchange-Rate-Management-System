using System;
using System.Collections.Generic;
using ExchangeRate.Core.Enums;

namespace ExchangeRate.Core.Interfaces
{
    public interface IExchangeRateRepository
    {
        /// <summary>
        /// Returns the exchange rate from the <paramref name="fromCurrency"/> to the <paramref name="toCurrency"/> on the given <paramref name="date"/>.
        /// It will return a previously valid rate, if the database does not contain rate for the specified <paramref name="date"/>.
        /// It will return NULL if there is no rate at all for the <paramref name="fromCurrency"/> - <paramref name="toCurrency"/> pair.
        /// </summary>
        Task<decimal?> GetRateAsync(CurrencyTypes fromCurrency, CurrencyTypes toCurrency, DateTime date, ExchangeRateSources source, ExchangeRateFrequencies frequency);

        /// <summary>
        /// Returns the exchange rate from the <paramref name="fromCurrencyCode"/> to the <paramref name="toCurrencyCode"/> on the given <paramref name="date"/>.
        /// It will return a previously valid rate, if the database does not contain rate for the specified <paramref name="date"/>.
        /// It will return NULL if there is no rate at all for the <paramref name="fromCurrencyCode"/> - <paramref name="toCurrencyCode"/> pair.
        /// </summary>
        Task<decimal?> GetRateAsync(string fromCurrencyCode, string toCurrencyCode, DateTime date, ExchangeRateSources source, ExchangeRateFrequencies frequency);

        /// <summary>
        /// Updates the exhange rates for the last available day.
        /// </summary>
        Task UpdateRatesAsync();

        /// <summary>
        /// Ensures that the database contains all exchange rates after <paramref name="minDate"/>.
        /// </summary>
        Task<bool> EnsureMinimumDateRangeAsync(DateTime minDate, IEnumerable<ExchangeRateSources> exchangeRateSources = null);
    }
}
