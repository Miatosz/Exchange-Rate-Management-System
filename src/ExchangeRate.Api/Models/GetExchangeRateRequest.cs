using ExchangeRate.Core.Enums;

namespace ExchangeRate.Api.Models;

public record GetExchangeRateRequest(
    string From,
    string To,
    DateTime Date,
    ExchangeRateSources Source,
    ExchangeRateFrequencies Frequency
    );