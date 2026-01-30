using ExchangeRate.Core.Enums;

namespace ExchangeRate.Api.Models;

public readonly record struct ExchangeRateKey(
    DateTime Date,
    CurrencyTypes CurrencyId,
    ExchangeRateSources Source,
    ExchangeRateFrequencies Frequency
    );