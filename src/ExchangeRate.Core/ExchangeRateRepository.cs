using Microsoft.Extensions.Logging;
using FluentResults;
using ExchangeRate.Core.Exceptions;
using ExchangeRate.Core.Helpers;
using ExchangeRate.Core.Interfaces;
using ExchangeRate.Core.Interfaces.Providers;
using ExchangeRate.Core.Entities;
using ExchangeRate.Core.Enums;
using ExchangeRate.Core.Infrastructure;

namespace ExchangeRate.Core
{
    /// <summary>
    ///  Current implementation has thread-safety limitations:
    /// - Dictionary mutations are not thread-safe
    /// - Multiple concurrent UpdateRatesAsync calls may race on dictionary updates
    /// - This is a pre-existing limitation, not introduced by async refactoring
    /// - For production, consider: ConcurrentDictionary or locking strategy
    /// </summary>
    class ExchangeRateRepository : IExchangeRateRepository
    {
        private static readonly IEnumerable<ExchangeRateSources> SupportedSources = System.Enum.GetValues(typeof(ExchangeRateSources)).Cast<ExchangeRateSources>().ToList();

        /// <summary>
        /// Maps currecy code string to currency type.
        /// </summary>
        private static readonly Dictionary<string, CurrencyTypes> CurrencyMapping;

        private readonly Dictionary<(ExchangeRateSources, ExchangeRateFrequencies), Dictionary<CurrencyTypes, Dictionary<DateTime, decimal>>> _fxRatesBySourceFrequencyAndCurrency;
        private Dictionary<(ExchangeRateSources, ExchangeRateFrequencies), DateTime> _minFxDateBySourceAndFrequency;
        private readonly Dictionary<CurrencyTypes, PeggedCurrency> _peggedCurrencies;

        private readonly IExchangeRateDataStore _dataStore;

        private readonly ILogger<ExchangeRateRepository> _logger;
        private readonly IExchangeRateProviderFactory _exchangeRateSourceFactory;

        static ExchangeRateRepository()
        {
            var currencies = System.Enum.GetValues(typeof(CurrencyTypes)).Cast<CurrencyTypes>().ToList();
            CurrencyMapping = currencies.ToDictionary(x => x.ToString().ToUpperInvariant());
        }

        private void ResetMinFxDates()
        {
            _minFxDateBySourceAndFrequency = SupportedSources.SelectMany(x => new List<(ExchangeRateSources, ExchangeRateFrequencies)>
            {
                new (x, ExchangeRateFrequencies.Daily),
                new (x, ExchangeRateFrequencies.Monthly),
                new (x, ExchangeRateFrequencies.Weekly),
                new (x, ExchangeRateFrequencies.BiWeekly),
            }).ToDictionary(x => x, _ => DateTime.MaxValue);
        }

        public ExchangeRateRepository(IExchangeRateDataStore dataStore, ILogger<ExchangeRateRepository> logger, IExchangeRateProviderFactory exchangeRateSourceFactory)
        {
            _dataStore = dataStore ?? throw new ArgumentNullException(nameof(dataStore));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _exchangeRateSourceFactory = exchangeRateSourceFactory ?? throw new ArgumentNullException(nameof(exchangeRateSourceFactory));

            _fxRatesBySourceFrequencyAndCurrency = new Dictionary<(ExchangeRateSources, ExchangeRateFrequencies), Dictionary<CurrencyTypes, Dictionary<DateTime, decimal>>>();
            ResetMinFxDates();

            _peggedCurrencies = _dataStore.GetPeggedCurrencies()
                .ToDictionary(x => x.CurrencyId!.Value);
        }

        internal ExchangeRateRepository(IEnumerable<Entities.ExchangeRate> rates, IExchangeRateProviderFactory exchangeRateSourceFactory)
        {
            _fxRatesBySourceFrequencyAndCurrency = new Dictionary<(ExchangeRateSources, ExchangeRateFrequencies), Dictionary<CurrencyTypes, Dictionary<DateTime, decimal>>>();
            ResetMinFxDates();

            LoadRates(rates);

            _exchangeRateSourceFactory = exchangeRateSourceFactory ?? throw new ArgumentNullException(nameof(exchangeRateSourceFactory));
        }

        /// <summary>
        /// Returns the exchange rate for the <paramref name="toCurrency"/> on the given <paramref name="date"/>.
        /// It will return a previously valid rate, if the database does not contain rate for the specified <paramref name="date"/>.
        /// It will return NULL if there is no rate at all for the <paramref name="toCurrency"/>.
        /// </summary>
        public async Task<decimal?> GetRateAsync(CurrencyTypes fromCurrency, CurrencyTypes toCurrency, DateTime date, ExchangeRateSources source, ExchangeRateFrequencies frequency)
        {
            var provider = _exchangeRateSourceFactory.GetExchangeRateProvider(source);

            if (toCurrency == fromCurrency)
                return 1m;

            date = date.Date;
            var minFxDate = await GetMinFxDate(date, source, frequency);
            
            if (fromCurrency != provider.Currency && toCurrency != provider.Currency)
            {
                var fromRate = await GetRateAsync(fromCurrency, provider.Currency, date, source, frequency);
                var toRate = await GetRateAsync(provider.Currency, toCurrency, date, source, frequency);
                return fromRate * toRate;
            }

            var result = GetFxRate(GetRatesByCurrency(source, frequency), date, minFxDate, 
                provider, fromCurrency, toCurrency, out _);

            if (result.IsSuccess)
                return result.Value;

            if (result.Errors.FirstOrDefault() is NoFxRateFoundError)
            {
                await UpdateRatesAsync(provider, minFxDate, date, source, frequency);
                        
                result = GetFxRate(GetRatesByCurrency(source, frequency), date, minFxDate, 
                    provider, fromCurrency, toCurrency, out var currency);

                if (result.IsSuccess)
                    return result.Value;
            }

            _logger.LogError("No {source} {frequency} exchange rate found on {date:yyyy-MM-dd}. Earliest available date: {minFxDate:yyyy-MM-dd}. FromCurrency: {fromCurrency}, ToCurrency: {toCurrency}",
                source, frequency, date, minFxDate, fromCurrency, toCurrency);
            return null;
        }

        /// <summary>
        /// Returns the exchange rate for the <paramref name="currencyCode"/> on the given <paramref name="date"/>.
        /// It will return a previously valid rate, if the database does not contain rate for the specified <paramref name="date"/>.
        /// It will return NULL if there is no rate at all for the <paramref name="currencyCode"/>.
        /// </summary>
        public async Task<decimal?> GetRateAsync(string fromCurrencyCode, string toCurrencyCode, DateTime date, ExchangeRateSources source, ExchangeRateFrequencies frequency)
        {
            var fromCurrency = GetCurrencyType(fromCurrencyCode);

            var toCurrency = GetCurrencyType(toCurrencyCode);

            return await GetRateAsync(fromCurrency, toCurrency, date, source, frequency);
        }

        /// <summary>
        /// Updates the exchange rates for the last available day/month.
        /// </summary>
        public async Task UpdateRatesAsync()
        {
            foreach (var source in _exchangeRateSourceFactory.ListExchangeRateSources())
            {
                try
                {
                    var provider = _exchangeRateSourceFactory.GetExchangeRateProvider(source);

                    var rates = new List<Entities.ExchangeRate>();

                    if (provider is IDailyExchangeRateProvider dailyProvider)
                        rates.AddRange(dailyProvider.GetDailyFxRates().ToList());

                    if (provider is IMonthlyExchangeRateProvider monthlyProvider)
                        rates.AddRange(monthlyProvider.GetMonthlyFxRates().ToList());

                    if (provider is IWeeklyExchangeRateProvider weeklyProvider)
                        rates.AddRange(weeklyProvider.GetWeeklyFxRates().ToList());

                    if (provider is IBiWeeklyExchangeRateProvider biWeeklyProvider)
                        rates.AddRange(biWeeklyProvider.GetBiWeeklyFxRates().ToList());

                    if (rates.Any())
                    {
                        await LoadRatesFromDbAsync(PeriodHelper.GetStartOfMonth(rates.Min(x => x.Date!.Value)));

                        var itemsToSave = new List<Entities.ExchangeRate>();
                        foreach (var rate in rates)
                        {
                            if (AddRateToDictionaries(rate))
                                itemsToSave.Add(rate);
                        }

                        if (itemsToSave.Any())
                            await _dataStore.SaveExchangeRatesAsync(itemsToSave);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to update daily rates for {source}", source.ToString());
                }
            }
        }

        /// <summary>
        /// Ensures that the database contains all exchange rates after <paramref name="minDate"/>.
        /// </summary>
        public async Task<bool> EnsureMinimumDateRangeAsync(DateTime minDate, IEnumerable<ExchangeRateSources> exchangeRateSources = null)
        {
            var result = true;
            foreach (var source in exchangeRateSources ?? _exchangeRateSourceFactory.ListExchangeRateSources())
            {
                var provider = _exchangeRateSourceFactory.GetExchangeRateProvider(source);
                if (provider is IDailyExchangeRateProvider &&
                    !await EnsureMinimumDateRangeAsync(minDate, source, ExchangeRateFrequencies.Daily))
                {
                    result = false;
                }

                if (provider is IMonthlyExchangeRateProvider &&
                    !await EnsureMinimumDateRangeAsync(minDate, source, ExchangeRateFrequencies.Monthly))
                {
                    result = false;
                }

                if (provider is IWeeklyExchangeRateProvider &&
                    !await EnsureMinimumDateRangeAsync(minDate, source, ExchangeRateFrequencies.Weekly))
                {
                    result = false;
                }

                if (provider is IBiWeeklyExchangeRateProvider &&
                    !await EnsureMinimumDateRangeAsync(minDate, source, ExchangeRateFrequencies.BiWeekly))
                {
                    result = false;
                }
            }

            return result;
        }

        /// <summary>
        /// Ensures that the database contains all exchange rates after <paramref name="minDate"/> for the given <paramref name="source"/> and <paramref name="frequency"/>.
        /// </summary>
        private async Task<bool> EnsureMinimumDateRangeAsync(DateTime minDate, ExchangeRateSources source, ExchangeRateFrequencies frequency)
        {
            minDate = PeriodHelper.GetStartOfMonth(minDate);

            var minFxDate = PeriodHelper.GetStartOfMonth(_minFxDateBySourceAndFrequency[(source, frequency)]);

            // if the minimum exchange rate date is lower than or equal to the specified date, then we don't need to update the rates
            if (minFxDate <= minDate)
                return true;

            await LoadRatesFromDbAsync(minDate);

            minFxDate = PeriodHelper.GetStartOfMonth(_minFxDateBySourceAndFrequency[(source, frequency)]);
            if (minFxDate <= minDate)
                return true;

            var provider = _exchangeRateSourceFactory.GetExchangeRateProvider(source);

            return await EnsureMinimumDateRangeAsync(provider, minDate, source, frequency);
        }

        private async Task<bool> EnsureMinimumDateRangeAsync(IExchangeRateProvider provider, DateTime minDate, ExchangeRateSources source, ExchangeRateFrequencies frequency)
        {
            if (!_minFxDateBySourceAndFrequency.TryGetValue((source, frequency), out var minFxDate))
                throw new ExchangeRateException($"Couldn't find min FX date for source {source} with frequency {frequency}");

            // if the minimum exchange rate date is lower than or equal to the specified date, then we don't need to update the rates
            if (minFxDate <= minDate)
                return true;

            if (minFxDate == DateTime.MaxValue)
            {
                minFxDate = DateTime.UtcNow.Date;
            }

            // if there would still be missing FX rates, we need to collect them from the historical data source
            return await UpdateRatesAsync(provider, minDate, minFxDate, source, frequency);
        }

        private async Task<bool> UpdateRatesAsync(IExchangeRateProvider provider, DateTime minDate, DateTime minFxDate, ExchangeRateSources source, ExchangeRateFrequencies frequency)
        {
            var itemsToSave = new List<Entities.ExchangeRate>();

            // Ensure dates are in the correct chronological order (from <= to)
            var from = minDate <= minFxDate ? minDate : minFxDate;
            var to = minDate <= minFxDate ? minFxDate : minDate;

            switch (frequency)
            {
                case ExchangeRateFrequencies.Daily:
                    if (provider is not IDailyExchangeRateProvider dailyProvider)
                        throw new ExchangeRateException($"Provider {provider} does not support frequency {frequency}");
                    itemsToSave.AddRange(dailyProvider.GetHistoricalDailyFxRates(from, to).ToList());
                    break;
                case ExchangeRateFrequencies.Monthly:
                    if (provider is not IMonthlyExchangeRateProvider monthlyProvider)
                        throw new ExchangeRateException($"Provider {provider} does not support frequency {frequency}");
                    itemsToSave.AddRange(monthlyProvider.GetHistoricalMonthlyFxRates(from, to).ToList());
                    break;
                case ExchangeRateFrequencies.Weekly:
                    if (provider is not IWeeklyExchangeRateProvider weeklyProvider)
                        throw new ExchangeRateException($"Provider {provider} does not support frequency {frequency}");
                    itemsToSave.AddRange(weeklyProvider.GetHistoricalWeeklyFxRates(from, to).ToList());
                    break;
                case ExchangeRateFrequencies.BiWeekly:
                    if (provider is not IBiWeeklyExchangeRateProvider biweeklyProvider)
                        throw new ExchangeRateException($"Provider {provider} does not support frequency {frequency}");
                    itemsToSave.AddRange(biweeklyProvider.GetHistoricalBiWeeklyFxRates(from, to).ToList());
                    break;
                default:
                    throw new ExchangeRateException($"Unsupported frequency: {frequency}");
            }

            if (itemsToSave.Count == 0)
            {
                _logger.LogError("No historical data found between date {minDate:yyyy-MM-dd} and {v:yyyy-MM-dd} for source {source} with frequency {frequency}.", minDate, minFxDate, source, frequency);
                return false;
            }

            var newMinFxDate = minFxDate;
            foreach (var item in itemsToSave.ToArray())
            {
                if (!AddRateToDictionaries(item))
                    itemsToSave.Remove(item);

                if (item.Date!.Value < newMinFxDate)
                    newMinFxDate = item.Date.Value;
            }
            _minFxDateBySourceAndFrequency[(source, frequency)] = newMinFxDate;

            // if storing in memory was successful, we can save it to the database
            if (itemsToSave.Any())
                await _dataStore.SaveExchangeRatesAsync(itemsToSave);

            return true;
        }

        /// <summary>
        /// Loads FX rates into cache dictionary starting with the specified date and sets the <see cref="_minFxDate"/>.
        /// </summary>
        private async Task LoadRatesFromDbAsync(DateTime minDate)
        {
            var minFxDate = _minFxDateBySourceAndFrequency.Min(x => x.Value);
            var fxRatesInDb = await _dataStore.GetExchangeRatesAsync(minDate, minFxDate);

            LoadRates(fxRatesInDb);
        }

        /// <summary>
        /// Loads FX rates into cache dictionary starting with the specified date and sets the <see cref="_minFxDateBySourceAndFrequency"/>.
        /// </summary>
        private void LoadRates(IEnumerable<Entities.ExchangeRate> fxRatesInDb)
        {
            // store them in memory and refresh minimum FX rate date
            var minFxDateBySource = _minFxDateBySourceAndFrequency;
            foreach (var item in fxRatesInDb)
            {
                AddRateToDictionaries(item);

                var source = item.Source!.Value;
                var frequency = item.Frequency!.Value;

                if (!minFxDateBySource.TryGetValue((source, frequency), out var minFxDate))
                    throw new ExchangeRateException($"Couldn't find min FX date for source {source} with frequency {frequency}");

                if (item.Date!.Value < minFxDate)
                    minFxDateBySource[(source, frequency)] = item.Date.Value;
            }

            _minFxDateBySourceAndFrequency = minFxDateBySource.ToDictionary(x => x.Key, x => x.Value);
        }

        /// <summary>
        /// Adds exchange rates to the FX rate dictionaries.
        /// It should be called to every currency-date pairs once.
        /// </summary>
        private bool AddRateToDictionaries(Entities.ExchangeRate item)
        {
            var currency = item.CurrencyId!.Value;
            var date = item.Date!.Value;
            var source = item.Source!.Value;
            var frequency = item.Frequency!.Value;
            var newRate = item.Rate!.Value;

            if (!_fxRatesBySourceFrequencyAndCurrency.TryGetValue((source, frequency), out var currenciesBySource))
                _fxRatesBySourceFrequencyAndCurrency.Add((source, frequency), currenciesBySource = new Dictionary<CurrencyTypes, Dictionary<DateTime, decimal>>());

            if (!currenciesBySource.TryGetValue(currency, out var datesByCurrency))
                currenciesBySource.Add(currency, datesByCurrency = new Dictionary<DateTime, decimal>());

            if (datesByCurrency.TryGetValue(date, out var savedRate))
            {
                if (decimal.Round(newRate, Entities.ExchangeRate.Precision) != decimal.Round(savedRate, Entities.ExchangeRate.Precision))
                {
                    _logger.LogWarning("Updating exchange rate. Currency: {currency}, Date: {date:yyyy-MM-dd}, Old rate: {savedRate}, New rate: {newRate}, Source: {source}, Frequency: {frequency}", 
                        currency, date, savedRate, newRate, source, frequency);
            
                    datesByCurrency[date] = newRate;
                    return true;
                }

                return false;
            }
            else
            {
                datesByCurrency.Add(date, newRate);
                return true;
            }
        }

        private Result<decimal> GetFxRate(
            IReadOnlyDictionary<CurrencyTypes, Dictionary<DateTime, decimal>> ratesByCurrencyAndDate,
            DateTime date,
            DateTime minFxDate,
            IExchangeRateProvider provider,
            CurrencyTypes fromCurrency,
            CurrencyTypes toCurrency,
            out CurrencyTypes lookupCurrency)
        {
                // Handle same-currency conversion (can happen in recursive pegged currency lookups)
                if (fromCurrency == toCurrency)
                {
                    lookupCurrency = fromCurrency;
                    return Result.Ok(1m);
                }

                //  always need to find the rate for the currency that is not the provider's currency
                lookupCurrency = toCurrency == provider.Currency ? fromCurrency : toCurrency;
                var nonLookupCurrency = toCurrency == provider.Currency ? toCurrency : fromCurrency;

                if (!ratesByCurrencyAndDate.TryGetValue(lookupCurrency, out var currencyDict))
                {
                    if (!_peggedCurrencies.TryGetValue(lookupCurrency, out var peggedCurrency))
                    {
                        return Result.Fail(new NotSupportedCurrencyError(lookupCurrency));
                    }

                    var peggedToCurrencyResult = GetFxRate(ratesByCurrencyAndDate, date, minFxDate, provider, nonLookupCurrency, peggedCurrency.PeggedTo!.Value, out _);

                    if (peggedToCurrencyResult.IsFailed)
                    {
                        return peggedToCurrencyResult;
                    }

                    var peggedRate = peggedCurrency.Rate!.Value;
                    var resultRate = peggedToCurrencyResult.Value;

                    return Result.Ok(toCurrency == provider.Currency
                        ? peggedRate / resultRate
                        : resultRate / peggedRate);

                }
                // start looking for the date, and decreasing the date if no match found (but only until the minFxDate)

            for (var d = date; d >= minFxDate; d = d.AddDays(-1d))
            {
                if (currencyDict.TryGetValue(d, out var fxRate))
                {
                    /*
                       If your local currency is EUR:
                       - Direct exchange rate: 1 USD = 0.92819 EUR
                       - Indirect exchange rate: 1 EUR = 1.08238 USD
                    */

                    // QuoteType    ProviderCurrency    FromCurrency    ToCurrency    Rate
                    // Direct       EUR                 USD             EUR           fxRate
                    // Direct       EUR                 EUR             USD           1/fxRate
                    // InDirect     EUR                 USD             EUR           1/fxRate
                    // InDirect     EUR                 EUR             USD           fxRate

                    return provider.QuoteType switch
                    {
                        QuoteTypes.Direct when toCurrency == provider.Currency => Result.Ok(fxRate),
                        QuoteTypes.Direct when fromCurrency == provider.Currency => Result.Ok(1 / fxRate),
                        QuoteTypes.Indirect when fromCurrency == provider.Currency => Result.Ok(fxRate),
                        QuoteTypes.Indirect when toCurrency == provider.Currency => Result.Ok(1 / fxRate),
                        _ => throw new InvalidOperationException("Unsupported QuoteType")
                    };
                }
            }

            return Result.Fail(new NoFxRateFoundError());
        }

        private static CurrencyTypes GetCurrencyType(string currencyCode)
        {
            if (string.IsNullOrWhiteSpace(currencyCode))
                throw new ExchangeRateException("Null or empty currency code.");

            if (!CurrencyMapping.TryGetValue(currencyCode.ToUpperInvariant(), out var currency))
                throw new ExchangeRateException("Not supported currency code: " + currencyCode);

            return currency;
        }

        private async Task<DateTime> GetMinFxDate(DateTime date, ExchangeRateSources source, ExchangeRateFrequencies frequency)
        {
            if (!_minFxDateBySourceAndFrequency.TryGetValue((source, frequency), out var minFxDate))
                throw new ExchangeRateException("Couldn't find base min FX date for source: " + source);

            // if the currently available date is higher than the requested date, then we need to get it from the database, or fill the database from the FX rate source
            if (minFxDate > date)
                await EnsureMinimumDateRangeAsync(date.AddMonths(-1), source, frequency);

            // Update minFxDate value after EnsureMinimumDateRange
            _minFxDateBySourceAndFrequency.TryGetValue((source, frequency), out minFxDate);

            return minFxDate;
        }

        private IReadOnlyDictionary<CurrencyTypes, Dictionary<DateTime, decimal>> GetRatesByCurrency(ExchangeRateSources source, ExchangeRateFrequencies frequency)
        {
            if (!_fxRatesBySourceFrequencyAndCurrency.TryGetValue((source, frequency), out var ratesByCurrency))
                throw new ExchangeRateException(
                    $"No exchange rates available for source {source} with frequency {frequency}");

            return ratesByCurrency;
        }
    }
    
}
