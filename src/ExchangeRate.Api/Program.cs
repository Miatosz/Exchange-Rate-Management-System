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
const string SectionName = "ExchangeRateApi";

// Configure ExchangeRate services
builder.Services.AddSingleton<ExternalExchangeRateApiConfig>(_ =>
{
    var section = builder.Configuration.GetSection(SectionName);
    var config = section.Get<ExternalExchangeRateApiConfig>();
    
    if (config == null)
    {
        config = new ExternalExchangeRateApiConfig
        {
            BaseAddress = section["BaseAddress"] ?? "http://localhost",
            TokenEndpoint = section["TokenEndpoint"] ?? "/connect/token",
            ClientId = section["ClientId"] ?? "client",
            ClientSecret = section["ClientSecret"] ?? "secret"
        };

        var logger = builder.Services.BuildServiceProvider()
            .GetRequiredService<ILogger<Program>>();
        logger.LogWarning("Using default ExternalExchangeRateApiConfig values – check configuration!");
    }

    return config;
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
    IExchangeRateRepository repository,
    ILogger<Program> logger) =>
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
        logger.LogError(e, "Unhandled exception while processing request");
        return Results.Problem(
            title: "Error retrieving exchange rate",
            statusCode: StatusCodes.Status500InternalServerError);
    }
    
    
});

app.Run();


// Make Program accessible to test project
public partial class Program { }
