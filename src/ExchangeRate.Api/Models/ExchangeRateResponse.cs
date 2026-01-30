namespace ExchangeRate.Api.Models;

public record ExchangeRateResponse(
    string FromCurrency,
    string ToCurrency,
    DateTime Date,
    string Source,
    string Frequency,
    decimal Rate);