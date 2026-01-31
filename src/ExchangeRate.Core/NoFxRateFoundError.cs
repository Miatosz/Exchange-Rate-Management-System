using FluentResults;

namespace ExchangeRate.Core;

public class NoFxRateFoundError: Error
{
    public NoFxRateFoundError()
        : base("No fx rate found") { }
}
