using ExchangeRate.Core.Enums;
using FluentResults;

namespace ExchangeRate.Core;

public class NotSupportedCurrencyError: Error
{
    public NotSupportedCurrencyError(CurrencyTypes currency)
        : base("Not supported currency: " + currency) { }
}