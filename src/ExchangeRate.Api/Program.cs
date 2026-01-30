using ExchangeRate.Api.Extensions;
using ExchangeRate.Api.Infrastructure;
using ExchangeRate.Api.Models;
using ExchangeRate.Api.Validators;
using ExchangeRate.Core;
using ExchangeRate.Core.Enums;
using ExchangeRate.Core.Infrastructure;
using ExchangeRate.Core.Interfaces;
using ExchangeRate.Core.Interfaces.Providers;
using ExchangeRate.Core.Models;
using ExchangeRate.Core.Providers;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

// Configure ExchangeRate services
builder.Services.AddSingleton<ExternalExchangeRateApiConfig>(sp =>
{
    var config = builder.Configuration.GetSection("ExchangeRateApi").Get<ExternalExchangeRateApiConfig>();
    return config ?? new ExternalExchangeRateApiConfig
    {
        BaseAddress = builder.Configuration["ExchangeRateApi:BaseAddress"] ?? "http://localhost",
        TokenEndpoint = builder.Configuration["ExchangeRateApi:TokenEndpoint"] ?? "/connect/token",
        ClientId = builder.Configuration["ExchangeRateApi:ClientId"] ?? "client",
        ClientSecret = builder.Configuration["ExchangeRateApi:ClientSecret"] ?? "secret"
    };
});

// Register HttpClient for providers
builder.Services.AddHttpClient<EUECBExchangeRateProvider>();
builder.Services.AddHttpClient<MXCBExchangeRateProvider>();

// Register validators
builder.Services.AddValidatorsFromAssemblyContaining<GetExchangeRateRequestValidator>();

// Register application services
builder.Services.AddApplicationServices();

var app = builder.Build();

app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        context.Response.StatusCode = 500;
        context.Response.ContentType = "application/problem+json";

        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Title = "Unexpected error",
            Detail = "An unexpected error occurred while processing your request",
            Status = 500
        });
    });
});

// GET /api/rates?from={currency}&to={currency}&date={date}&source={source}&frequency={frequency}
app.MapGet("/api/rates", async(
    GetExchangeRateRequest request,
    IValidator<GetExchangeRateRequest> validator,
    IExchangeRateRepository repository) =>
{
    
    var validation = await validator.ValidateAsync(request);
    if (!validation.IsValid)
    {
        return Results.ValidationProblem(validation.ToDictionary());
    }

    try
    {
        var rate = repository.GetRate(request.From, request.To, request.Date, request.Source, request.Frequency);
        
        return rate is null ? Results.NotFound() : Results.Ok(new ExchangeRateResponse(
            request.From, 
            request.To, 
            request.Date, 
            request.Source.ToString(), 
            request.Frequency.ToString(), 
            rate.Value));
    }
    catch (Exception e)
    {
        return Results.Problem(
            title: "Error retrieving exchange rate",
            detail: e.Message,
            statusCode: StatusCodes.Status500InternalServerError);
    }
    
    
});

app.Run();


// Make Program accessible to test project
public partial class Program { }
