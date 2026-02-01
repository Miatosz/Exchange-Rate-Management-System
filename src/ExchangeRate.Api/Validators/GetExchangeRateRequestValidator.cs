using ExchangeRate.Api.Models;
using FluentValidation;

namespace ExchangeRate.Api.Validators;

public class GetExchangeRateRequestValidator : AbstractValidator<GetExchangeRateRequest>
{
    public GetExchangeRateRequestValidator()
    {
        RuleFor(x => x.From)
            .NotEmpty()
            .Length(3)
            .Matches("^[A-Z]{3}$");

        RuleFor(x => x.To)
            .NotEmpty()
            .Length(3)
            .Matches("^[A-Z]{3}$");
        
        // Note: Future dates are allowed, system falls back to latest available rate
        //RuleFor(x => x.Date)
        //    .LessThanOrEqualTo(_ => DateTime.UtcNow.Date);

        RuleFor(x => x.Source)
            .IsInEnum();

        RuleFor(x => x.Frequency)
            .IsInEnum();
    }
}