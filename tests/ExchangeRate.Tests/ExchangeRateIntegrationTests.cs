#nullable enable
using System.Net;
using System.Net.Http.Json;
using ExchangeRate.Api.Infrastructure;
using ExchangeRate.Core.Entities;
using ExchangeRate.Core.Enums;
using ExchangeRate.Core.Infrastructure;
using ExchangeRate.Core.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace ExchangeRate.Tests;

/// <summary>
/// True integration tests that test the API endpoint with mocked external HTTP calls via WireMock.
///
/// These tests:
/// - Use WebApplicationFactory to host the real API
/// - Only mock the external exchange rate provider API calls via WireMock
/// - Allow candidates to refactor ANY internal structure (repository, factory, providers, DI) and tests will still pass
///
/// The tests verify behavior through HTTP calls to GET /api/rates endpoint,
/// not internal implementation details.
/// </summary>
public class ExchangeRateIntegrationTests : IDisposable
{
    private readonly ExchangeRateApiFactory _factory;
    private HttpClient? _client;

    public ExchangeRateIntegrationTests()
    {
        // Create a fresh factory for each test to ensure isolation
        // Note: Client is created lazily via GetClient() to allow test setup before service construction
        _factory = new ExchangeRateApiFactory();
    }

    /// <summary>
    /// Gets the HTTP client, creating it if necessary.
    /// This allows tests to set up pegged currencies and other data before the client (and services) are created.
    /// </summary>
    private HttpClient GetClient()
    {
        _client ??= _factory.CreateClient();
        return _client;
    }

    public void Dispose()
    {
        _client?.Dispose();
        _factory.Dispose();
    }

    #region Core Rate Retrieval Tests

    /// <summary>
    /// Tests that the API correctly returns rates fetched from the external exchange rate API.
    /// This is the most basic integration test - verifying end-to-end flow.
    /// </summary>
    [Fact]
    public async Task GetRate_WhenEcbApiReturnsRate_ReturnsCorrectRate()
    {
        // Arrange
        var date = new DateTime(2024, 01, 15);
        var expectedUsdRate = 1.0856m;

        _factory.SetupTokenEndpoint();
        _factory.SetupEcbDailyRatesEndpoint(date, new Dictionary<string, decimal>
        {
            { "USD", expectedUsdRate },
            { "GBP", 0.8572m },
            { "JPY", 161.12m }
        });

        // Act
        var response = await GetClient().GetAsync(
            $"/api/rates?from=EUR&to=USD&date={date:yyyy-MM-dd}&source={ExchangeRateSources.ECB}&frequency={ExchangeRateFrequencies.Daily}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ExchangeRateResponse>();
        result.Should().NotBeNull();
        result!.Rate.Should().Be(expectedUsdRate);
        result.FromCurrency.Should().Be("EUR");
        result.ToCurrency.Should().Be("USD");
    }

    /// <summary>
    /// Tests that same-currency conversion returns 1.
    /// </summary>
    [Fact]
    public async Task GetRate_WhenSameCurrency_ReturnsOne()
    {
        // Arrange
        _factory.SetupTokenEndpoint();

        // Act
        var response = await GetClient().GetAsync(
            $"/api/rates?from=EUR&to=EUR&date={DateTime.Today:yyyy-MM-dd}&source={ExchangeRateSources.ECB}&frequency={ExchangeRateFrequencies.Daily}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ExchangeRateResponse>();
        result.Should().NotBeNull();
        result!.Rate.Should().Be(1m);
    }

    /// <summary>
    /// Tests cross-currency conversion (e.g., USD to GBP via EUR).
    /// This verifies the triangulation logic works correctly.
    /// </summary>
    [Fact]
    public async Task GetRate_CrossCurrency_CalculatesViaProviderCurrency()
    {
        // Arrange
        var date = new DateTime(2024, 01, 15);
        var usdRate = 1.0856m;
        var gbpRate = 0.8572m;

        _factory.SetupTokenEndpoint();
        _factory.SetupEcbDailyRatesEndpoint(date, new Dictionary<string, decimal>
        {
            { "USD", usdRate },
            { "GBP", gbpRate }
        });

        // Act - USD to GBP via EUR (ECB provider currency)
        var response = await GetClient().GetAsync(
            $"/api/rates?from=USD&to=GBP&date={date:yyyy-MM-dd}&source={ExchangeRateSources.ECB}&frequency={ExchangeRateFrequencies.Daily}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ExchangeRateResponse>();
        result.Should().NotBeNull();
        // For ECB (Indirect quote with EUR as base):
        // USD to GBP = (1/usdRate) * gbpRate = gbpRate / usdRate
        var expectedRate = gbpRate / usdRate;
        result!.Rate.Should().BeApproximately(expectedRate, 0.0001m);
    }

    #endregion

    #region Fallback Behavior Tests

    /// <summary>
    /// Tests that when a rate is not available for a specific date,
    /// the system falls back to the most recent available rate.
    /// This is critical for weekend/holiday handling.
    /// </summary>
    [Fact]
    public async Task GetRate_WhenDateNotAvailable_FallsBackToLastAvailableRate()
    {
        // Arrange
        var availableDate = new DateTime(2024, 01, 12); // Friday
        var requestedDate = new DateTime(2024, 01, 14); // Sunday - no rate
        var expectedRate = 1.0856m;

        _factory.SetupTokenEndpoint();
        // API returns only Friday's rate (no weekend rates)
        _factory.SetupEcbDailyRatesEndpoint(availableDate, requestedDate, new Dictionary<DateTime, Dictionary<string, decimal>>
        {
            { availableDate, new Dictionary<string, decimal> { { "USD", expectedRate } } }
        });

        // Act
        var response = await GetClient().GetAsync(
            $"/api/rates?from=EUR&to=USD&date={requestedDate:yyyy-MM-dd}&source={ExchangeRateSources.ECB}&frequency={ExchangeRateFrequencies.Daily}");

        // Assert - Should fall back to Friday's rate
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ExchangeRateResponse>();
        result.Should().NotBeNull();
        result!.Rate.Should().Be(expectedRate);
    }

    #endregion

    #region String Currency Code Tests

    /// <summary>
    /// Tests that the API accepts string currency codes and returns correct rates.
    /// </summary>
    [Fact]
    public async Task GetRate_WithStringCurrencyCodes_ReturnsCorrectRate()
    {
        // Arrange
        var date = new DateTime(2024, 01, 15);
        var expectedUsdRate = 1.0856m;

        _factory.SetupTokenEndpoint();
        _factory.SetupEcbDailyRatesEndpoint(date, new Dictionary<string, decimal>
        {
            { "USD", expectedUsdRate }
        });

        // Act
        var response = await GetClient().GetAsync(
            $"/api/rates?from=EUR&to=USD&date={date:yyyy-MM-dd}&source={ExchangeRateSources.ECB}&frequency={ExchangeRateFrequencies.Daily}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ExchangeRateResponse>();
        result.Should().NotBeNull();
        result!.Rate.Should().Be(expectedUsdRate);
    }

    #endregion

    #region Inverse Rate Tests

    /// <summary>
    /// Tests that requesting the inverse rate (e.g., USD to EUR) returns the correct inverted value.
    /// </summary>
    [Fact]
    public async Task GetRate_InverseRate_ReturnsCorrectlyInvertedRate()
    {
        // Arrange
        var date = new DateTime(2024, 01, 15);
        var usdRate = 1.0856m;

        _factory.SetupTokenEndpoint();
        _factory.SetupEcbDailyRatesEndpoint(date, new Dictionary<string, decimal>
        {
            { "USD", usdRate }
        });

        // Act - USD to EUR (inverse of stored EUR to USD rate)
        var response = await GetClient().GetAsync(
            $"/api/rates?from=USD&to=EUR&date={date:yyyy-MM-dd}&source={ExchangeRateSources.ECB}&frequency={ExchangeRateFrequencies.Daily}");

        // Assert - For indirect quote: USD to EUR = 1/rate
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ExchangeRateResponse>();
        result.Should().NotBeNull();
        var expectedRate = 1 / usdRate;
        result!.Rate.Should().BeApproximately(expectedRate, 0.0001m);
    }

    #endregion

    #region Pegged Currency Tests
    // NOTE: These tests require pegged currencies to be configured in the data store.
    // They are skipped by default because the basic API setup doesn't include pegged currencies.
    // Candidates implementing pegged currency support should enable these tests.

    /// <summary>
    /// Tests EUR to AED conversion using pegged currency (AED pegged to USD).
    /// AED is pegged to USD at 0.27229 ratio (1 AED = 0.27229 USD) from InitialData.
    /// </summary>
    [Fact]
    public async Task GetRate_EurToAed_WithPeggedCurrency_ReturnsCorrectRate()
    {
        // Arrange
        var date = new DateTime(2024, 09, 09);
        var usdRate = 2m; // EUR-USD rate (Indirect: 1 EUR = 2 USD)
        var aedPegRate = 0.27229m; // Real rate from PeggedCurrency.InitialData

        // Add AED pegged to USD (must be done BEFORE GetClient() creates services)
        _factory.AddPeggedCurrency(new PeggedCurrency
        {
            CurrencyId = CurrencyTypes.AED,
            PeggedTo = CurrencyTypes.USD,
            Rate = aedPegRate
        });

        _factory.SetupTokenEndpoint();
        _factory.SetupEcbDailyRatesEndpoint(date, new Dictionary<string, decimal>
        {
            { "USD", usdRate }
        });

        // Act
        var response = await GetClient().GetAsync(
            $"/api/rates?from=EUR&to=AED&date={date:yyyy-MM-dd}&source={ExchangeRateSources.ECB}&frequency={ExchangeRateFrequencies.Daily}");

        // Assert
        // EUR to AED via USD: EUR->USD = 2, USD->AED = 1/0.27229 = 3.6725
        // EUR->AED = 2 * 3.6725 = 7.345
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ExchangeRateResponse>();
        result.Should().NotBeNull();
        var expectedRate = usdRate / aedPegRate; // 2 / 0.27229 = 7.345
        result!.Rate.Should().BeApproximately(expectedRate, 0.001m);
    }

    /// <summary>
    /// Tests AED to EUR conversion using pegged currency (AED pegged to USD).
    /// </summary>
    [Fact]
    public async Task GetRate_AedToEur_WithPeggedCurrency_ReturnsCorrectRate()
    {
        // Arrange
        var date = new DateTime(2024, 09, 09);
        var usdRate = 2m;
        var aedPegRate = 0.27229m;

        // Add pegged currency BEFORE GetClient()
        _factory.AddPeggedCurrency(new PeggedCurrency
        {
            CurrencyId = CurrencyTypes.AED,
            PeggedTo = CurrencyTypes.USD,
            Rate = aedPegRate
        });

        _factory.SetupTokenEndpoint();
        _factory.SetupEcbDailyRatesEndpoint(date, new Dictionary<string, decimal>
        {
            { "USD", usdRate }
        });

        // Act
        var response = await GetClient().GetAsync(
            $"/api/rates?from=AED&to=EUR&date={date:yyyy-MM-dd}&source={ExchangeRateSources.ECB}&frequency={ExchangeRateFrequencies.Daily}");

        // Assert
        // AED to EUR = 1 / (EUR to AED) = aedPegRate / usdRate = 0.27229 / 2 = 0.136145
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ExchangeRateResponse>();
        result.Should().NotBeNull();
        var expectedRate = aedPegRate / usdRate;
        result!.Rate.Should().BeApproximately(expectedRate, 0.0001m);
    }

    /// <summary>
    /// Tests USD to AED conversion with pegged currency.
    /// </summary>
    [Fact]
    public async Task GetRate_UsdToAed_WithPeggedCurrency_ReturnsCorrectRate()
    {
        // Arrange
        var date = new DateTime(2024, 09, 09);
        var usdRate = 2m;
        var aedPegRate = 0.27229m;

        // Add pegged currency BEFORE GetClient()
        _factory.AddPeggedCurrency(new PeggedCurrency
        {
            CurrencyId = CurrencyTypes.AED,
            PeggedTo = CurrencyTypes.USD,
            Rate = aedPegRate
        });

        _factory.SetupTokenEndpoint();
        _factory.SetupEcbDailyRatesEndpoint(date, new Dictionary<string, decimal>
        {
            { "USD", usdRate }
        });

        // Act
        var response = await GetClient().GetAsync(
            $"/api/rates?from=USD&to=AED&date={date:yyyy-MM-dd}&source={ExchangeRateSources.ECB}&frequency={ExchangeRateFrequencies.Daily}");

        // Assert
        // USD to AED = 1/aedPegRate = 1/0.27229 = 3.6725
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ExchangeRateResponse>();
        result.Should().NotBeNull();
        var expectedRate = 1 / aedPegRate;
        result!.Rate.Should().BeApproximately(expectedRate, 0.001m);
    }

    /// <summary>
    /// Tests AED to USD conversion with pegged currency.
    /// </summary>
    [Fact]
    public async Task GetRate_AedToUsd_WithPeggedCurrency_ReturnsCorrectRate()
    {
        // Arrange
        var date = new DateTime(2024, 09, 09);
        var usdRate = 2m;
        var aedPegRate = 0.27229m;

        // Add pegged currency BEFORE GetClient()
        _factory.AddPeggedCurrency(new PeggedCurrency
        {
            CurrencyId = CurrencyTypes.AED,
            PeggedTo = CurrencyTypes.USD,
            Rate = aedPegRate
        });

        _factory.SetupTokenEndpoint();
        _factory.SetupEcbDailyRatesEndpoint(date, new Dictionary<string, decimal>
        {
            { "USD", usdRate }
        });

        // Act
        var response = await GetClient().GetAsync(
            $"/api/rates?from=AED&to=USD&date={date:yyyy-MM-dd}&source={ExchangeRateSources.ECB}&frequency={ExchangeRateFrequencies.Daily}");

        // Assert
        // AED to USD = aedPegRate = 0.27229
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ExchangeRateResponse>();
        result.Should().NotBeNull();
        result!.Rate.Should().BeApproximately(aedPegRate, 0.0001m);
    }

    /// <summary>
    /// Tests pegged currency with EUR-pegged currency (BAM is pegged to EUR).
    /// </summary>
    [Fact]
    public async Task GetRate_EurToBam_WithEurPeggedCurrency_ReturnsCorrectRate()
    {
        // Arrange
        var date = new DateTime(2024, 09, 09);
        var bamPegRate = 0.60000m; // From PeggedCurrency.InitialData

        // Add pegged currency BEFORE GetClient()
        _factory.AddPeggedCurrency(new PeggedCurrency
        {
            CurrencyId = CurrencyTypes.BAM,
            PeggedTo = CurrencyTypes.EUR,
            Rate = bamPegRate
        });

        _factory.SetupTokenEndpoint();
        _factory.SetupEcbDailyRatesEndpoint(date, new Dictionary<string, decimal>
        {
            { "USD", 1.10m }
        });

        // Act
        var response = await GetClient().GetAsync(
            $"/api/rates?from=EUR&to=BAM&date={date:yyyy-MM-dd}&source={ExchangeRateSources.ECB}&frequency={ExchangeRateFrequencies.Daily}");

        // Assert
        // EUR to BAM = 1/bamPegRate = 1/0.60000 = 1.6667
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ExchangeRateResponse>();
        result.Should().NotBeNull();
        var expectedRate = 1 / bamPegRate;
        result!.Rate.Should().BeApproximately(expectedRate, 0.001m);
    }

    /// <summary>
    /// Tests BAM to EUR conversion with EUR-pegged currency.
    /// </summary>
    [Fact]
    public async Task GetRate_BamToEur_WithEurPeggedCurrency_ReturnsCorrectRate()
    {
        // Arrange
        var date = new DateTime(2024, 09, 09);
        var bamPegRate = 0.60000m;

        // Add pegged currency BEFORE GetClient()
        _factory.AddPeggedCurrency(new PeggedCurrency
        {
            CurrencyId = CurrencyTypes.BAM,
            PeggedTo = CurrencyTypes.EUR,
            Rate = bamPegRate
        });

        _factory.SetupTokenEndpoint();
        _factory.SetupEcbDailyRatesEndpoint(date, new Dictionary<string, decimal>
        {
            { "USD", 1.10m }
        });

        // Act
        var response = await GetClient().GetAsync(
            $"/api/rates?from=BAM&to=EUR&date={date:yyyy-MM-dd}&source={ExchangeRateSources.ECB}&frequency={ExchangeRateFrequencies.Daily}");

        // Assert
        // BAM to EUR = bamPegRate = 0.60000
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ExchangeRateResponse>();
        result.Should().NotBeNull();
        result!.Rate.Should().BeApproximately(bamPegRate, 0.0001m);
    }

    /// <summary>
    /// Tests cross-currency conversion between two pegged currencies.
    /// </summary>
    [Fact]
    public async Task GetRate_AedToBam_CrossPeggedCurrencies_ReturnsCorrectRate()
    {
        // Arrange
        var date = new DateTime(2024, 09, 09);
        var usdRate = 1.10m; // EUR-USD
        var aedPegRate = 0.27229m;
        var bamPegRate = 0.60000m;

        // Add pegged currencies BEFORE GetClient()
        _factory.AddPeggedCurrency(new PeggedCurrency
        {
            CurrencyId = CurrencyTypes.AED,
            PeggedTo = CurrencyTypes.USD,
            Rate = aedPegRate
        });
        _factory.AddPeggedCurrency(new PeggedCurrency
        {
            CurrencyId = CurrencyTypes.BAM,
            PeggedTo = CurrencyTypes.EUR,
            Rate = bamPegRate
        });

        _factory.SetupTokenEndpoint();
        _factory.SetupEcbDailyRatesEndpoint(date, new Dictionary<string, decimal>
        {
            { "USD", usdRate }
        });

        // Act
        var response = await GetClient().GetAsync(
            $"/api/rates?from=AED&to=BAM&date={date:yyyy-MM-dd}&source={ExchangeRateSources.ECB}&frequency={ExchangeRateFrequencies.Daily}");

        // Assert
        // AED->EUR = aedPegRate / usdRate = 0.27229 / 1.10 = 0.2475
        // EUR->BAM = 1/bamPegRate = 1/0.60000 = 1.6667
        // AED->BAM = 0.2475 * 1.6667 = 0.4125
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ExchangeRateResponse>();
        result.Should().NotBeNull();
        var aedToEur = aedPegRate / usdRate;
        var eurToBam = 1 / bamPegRate;
        var expectedRate = aedToEur * eurToBam;
        result!.Rate.Should().BeApproximately(expectedRate, 0.001m);
    }

    /// <summary>
    /// Tests pegged currency same-currency returns 1.
    /// </summary>
    [Fact]
    public async Task GetRate_SamePeggedCurrency_ReturnsOne()
    {
        // Arrange
        var date = new DateTime(2024, 09, 09);
        var aedPegRate = 0.27229m;

        // Add pegged currency BEFORE GetClient()
        _factory.AddPeggedCurrency(new PeggedCurrency
        {
            CurrencyId = CurrencyTypes.AED,
            PeggedTo = CurrencyTypes.USD,
            Rate = aedPegRate
        });

        _factory.SetupTokenEndpoint();
        _factory.SetupEcbDailyRatesEndpoint(date, new Dictionary<string, decimal>
        {
            { "USD", 1.10m }
        });

        // Act
        var response = await GetClient().GetAsync(
            $"/api/rates?from=AED&to=AED&date={date:yyyy-MM-dd}&source={ExchangeRateSources.ECB}&frequency={ExchangeRateFrequencies.Daily}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ExchangeRateResponse>();
        result.Should().NotBeNull();
        result!.Rate.Should().Be(1m);
    }

    #endregion

    #region Monthly Frequency Tests
    // NOTE: These tests require the ECB monthly frequency to be properly configured.
    // They are skipped by default because monthly frequency requires specific provider setup.
    // Candidates implementing monthly frequency support should enable these tests.

    /// <summary>
    /// Tests ECB monthly rate retrieval for CZK.
    /// </summary>
    [Fact]
    public async Task GetRate_EcbMonthlyRate_ForCzk_ReturnsCorrectRate()
    {
        // Arrange
        var date = new DateTime(2024, 01, 15);
        var monthStart = new DateTime(2024, 01, 01);
        var expectedRate = 25.21559m;

        _factory.SetupTokenEndpoint();
        _factory.SetupEcbMonthlyRatesEndpoint(monthStart, new Dictionary<string, decimal>
        {
            { "CZK", expectedRate }
        });

        // Act
        var response = await GetClient().GetAsync(
            $"/api/rates?from=EUR&to=CZK&date={date:yyyy-MM-dd}&source={ExchangeRateSources.ECB}&frequency={ExchangeRateFrequencies.Monthly}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ExchangeRateResponse>();
        result.Should().NotBeNull();
        result!.Rate.Should().Be(expectedRate);
    }

    /// <summary>
    /// Tests monthly rate inverse calculation.
    /// </summary>
    [Fact]
    public async Task GetRate_EcbMonthlyRate_InverseCalculation_ReturnsCorrectRate()
    {
        // Arrange
        var date = new DateTime(2024, 01, 15);
        var monthStart = new DateTime(2024, 01, 01);
        var czkRate = 25.21559m;

        _factory.SetupTokenEndpoint();
        _factory.SetupEcbMonthlyRatesEndpoint(monthStart, new Dictionary<string, decimal>
        {
            { "CZK", czkRate }
        });

        // Act
        var response = await GetClient().GetAsync(
            $"/api/rates?from=CZK&to=EUR&date={date:yyyy-MM-dd}&source={ExchangeRateSources.ECB}&frequency={ExchangeRateFrequencies.Monthly}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ExchangeRateResponse>();
        result.Should().NotBeNull();
        result!.Rate.Should().BeApproximately(1 / czkRate, 0.00001m);
    }

    /// <summary>
    /// Tests monthly rate with string currency codes.
    /// </summary>
    [Fact]
    public async Task GetRate_MonthlyRate_WithStringCurrencyCodes_ReturnsCorrectRate()
    {
        // Arrange
        var date = new DateTime(2024, 01, 15);
        var monthStart = new DateTime(2024, 01, 01);
        var expectedRate = 25.21559m;

        _factory.SetupTokenEndpoint();
        _factory.SetupEcbMonthlyRatesEndpoint(monthStart, new Dictionary<string, decimal>
        {
            { "CZK", expectedRate }
        });

        // Act - using string currency codes
        var response = await GetClient().GetAsync(
            $"/api/rates?from=EUR&to=CZK&date={date:yyyy-MM-dd}&source={ExchangeRateSources.ECB}&frequency={ExchangeRateFrequencies.Monthly}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ExchangeRateResponse>();
        result.Should().NotBeNull();
        result!.Rate.Should().Be(expectedRate);
    }

    /// <summary>
    /// Tests that monthly rate applies to any day within the month.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(15)]
    [InlineData(28)]
    public async Task GetRate_MonthlyRate_UsedForAnyDayInMonth(int dayOfMonth)
    {
        // Arrange
        var monthStart = new DateTime(2024, 06, 01);
        var requestDate = new DateTime(2024, 06, dayOfMonth);
        var expectedRate = 25.50m;

        _factory.SetupTokenEndpoint();
        _factory.SetupEcbMonthlyRatesEndpoint(monthStart, new Dictionary<string, decimal>
        {
            { "CZK", expectedRate }
        });

        // Act
        var response = await GetClient().GetAsync(
            $"/api/rates?from=EUR&to=CZK&date={requestDate:yyyy-MM-dd}&source={ExchangeRateSources.ECB}&frequency={ExchangeRateFrequencies.Monthly}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ExchangeRateResponse>();
        result.Should().NotBeNull();
        result!.Rate.Should().Be(expectedRate);
    }

    #endregion

    #region Mexican Cross-Rate Tests (MXCB)
    // NOTE: These tests require the MXCB provider to be registered in the DI container.
    // They are skipped by default because the basic API setup only includes ECB provider.
    // Candidates implementing MXCB support should enable these tests.

    /// <summary>
    /// Tests MXN to USD conversion with MXCB monthly rate.
    /// MXCB uses Direct quote: 1 USD = X MXN
    /// </summary>
    [Fact]
    public async Task GetRate_MxnToUsd_WithMxcbMonthlyRate_ReturnsInverseRate()
    {
        // Arrange
        var date = new DateTime(2024, 06, 15);
        var monthStart = new DateTime(2024, 06, 01);
        var usdMxnRate = 17.5m; // 1 USD = 17.5 MXN

        _factory.SetupTokenEndpoint();
        _factory.SetupMxcbMonthlyRatesEndpoint(monthStart, new Dictionary<string, decimal>
        {
            { "USD", usdMxnRate }
        });

        // Act
        var response = await GetClient().GetAsync(
            $"/api/rates?from=MXN&to=USD&date={date:yyyy-MM-dd}&source={ExchangeRateSources.MXCB}&frequency={ExchangeRateFrequencies.Monthly}");

        // Assert
        // Direct quote: MXN to USD = 1/rate = 1/17.5 = 0.05714
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ExchangeRateResponse>();
        result.Should().NotBeNull();
        result!.Rate.Should().BeApproximately(1 / usdMxnRate, 0.00001m);
    }

    /// <summary>
    /// Tests USD to MXN conversion with MXCB monthly rate.
    /// </summary>
    [Fact]
    public async Task GetRate_UsdToMxn_WithMxcbMonthlyRate_ReturnsStoredRate()
    {
        // Arrange
        var date = new DateTime(2024, 06, 15);
        var monthStart = new DateTime(2024, 06, 01);
        var usdMxnRate = 17.5m;

        _factory.SetupTokenEndpoint();
        _factory.SetupMxcbMonthlyRatesEndpoint(monthStart, new Dictionary<string, decimal>
        {
            { "USD", usdMxnRate }
        });

        // Act
        var response = await GetClient().GetAsync(
            $"/api/rates?from=USD&to=MXN&date={date:yyyy-MM-dd}&source={ExchangeRateSources.MXCB}&frequency={ExchangeRateFrequencies.Monthly}");

        // Assert
        // Direct quote: USD to MXN = stored rate = 17.5
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ExchangeRateResponse>();
        result.Should().NotBeNull();
        result!.Rate.Should().Be(usdMxnRate);
    }

    /// <summary>
    /// Tests EUR to MXN cross-rate via USD using MXCB.
    /// When MXCB has both EUR and USD rates, cross-rate is calculated.
    /// </summary>
    [Fact]
    public async Task GetRate_EurToMxn_CrossRateViaUsd_ReturnsCalculatedRate()
    {
        // Arrange
        var date = new DateTime(2024, 06, 15);
        var monthStart = new DateTime(2024, 06, 01);
        var usdMxnRate = 17.5m;
        var eurMxnRate = 19.25m; // EUR-MXN = EUR-USD * USD-MXN = 1.10 * 17.5 = 19.25

        _factory.SetupTokenEndpoint();
        _factory.SetupMxcbMonthlyRatesEndpoint(monthStart, new Dictionary<string, decimal>
        {
            { "USD", usdMxnRate },
            { "EUR", eurMxnRate }
        });

        // Act
        var response = await GetClient().GetAsync(
            $"/api/rates?from=EUR&to=MXN&date={date:yyyy-MM-dd}&source={ExchangeRateSources.MXCB}&frequency={ExchangeRateFrequencies.Monthly}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ExchangeRateResponse>();
        result.Should().NotBeNull();
        result!.Rate.Should().Be(eurMxnRate);
    }

    /// <summary>
    /// Tests MXN to EUR cross-rate.
    /// </summary>
    [Fact]
    public async Task GetRate_MxnToEur_CrossRateViaUsd_ReturnsCalculatedRate()
    {
        // Arrange
        var date = new DateTime(2024, 06, 15);
        var monthStart = new DateTime(2024, 06, 01);
        var usdMxnRate = 17.5m;
        var eurMxnRate = 19.25m;

        _factory.SetupTokenEndpoint();
        _factory.SetupMxcbMonthlyRatesEndpoint(monthStart, new Dictionary<string, decimal>
        {
            { "USD", usdMxnRate },
            { "EUR", eurMxnRate }
        });

        // Act
        var response = await GetClient().GetAsync(
            $"/api/rates?from=MXN&to=EUR&date={date:yyyy-MM-dd}&source={ExchangeRateSources.MXCB}&frequency={ExchangeRateFrequencies.Monthly}");

        // Assert
        // MXN to EUR = 1/eurMxnRate
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ExchangeRateResponse>();
        result.Should().NotBeNull();
        result!.Rate.Should().BeApproximately(1 / eurMxnRate, 0.00001m);
    }

    /// <summary>
    /// Tests USD to EUR cross-rate via MXN.
    /// </summary>
    [Fact]
    public async Task GetRate_UsdToEur_CrossRateViaMxn_ReturnsCalculatedRate()
    {
        // Arrange
        var date = new DateTime(2024, 06, 15);
        var monthStart = new DateTime(2024, 06, 01);
        var usdMxnRate = 17.5m;
        var eurMxnRate = 19.25m;

        _factory.SetupTokenEndpoint();
        _factory.SetupMxcbMonthlyRatesEndpoint(monthStart, new Dictionary<string, decimal>
        {
            { "USD", usdMxnRate },
            { "EUR", eurMxnRate }
        });

        // Act
        var response = await GetClient().GetAsync(
            $"/api/rates?from=USD&to=EUR&date={date:yyyy-MM-dd}&source={ExchangeRateSources.MXCB}&frequency={ExchangeRateFrequencies.Monthly}");

        // Assert
        // USD->MXN = 17.5, MXN->EUR = 1/19.25
        // USD->EUR = 17.5 / 19.25 = 0.90909
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ExchangeRateResponse>();
        result.Should().NotBeNull();
        result!.Rate.Should().BeApproximately(usdMxnRate / eurMxnRate, 0.00001m);
    }

    /// <summary>
    /// Tests EUR to USD cross-rate via MXN.
    /// </summary>
    [Fact]
    public async Task GetRate_EurToUsd_CrossRateViaMxn_ReturnsCalculatedRate()
    {
        // Arrange
        var date = new DateTime(2024, 06, 15);
        var monthStart = new DateTime(2024, 06, 01);
        var usdMxnRate = 17.5m;
        var eurMxnRate = 19.25m;

        _factory.SetupTokenEndpoint();
        _factory.SetupMxcbMonthlyRatesEndpoint(monthStart, new Dictionary<string, decimal>
        {
            { "USD", usdMxnRate },
            { "EUR", eurMxnRate }
        });

        // Act
        var response = await GetClient().GetAsync(
            $"/api/rates?from=EUR&to=USD&date={date:yyyy-MM-dd}&source={ExchangeRateSources.MXCB}&frequency={ExchangeRateFrequencies.Monthly}");

        // Assert
        // EUR->MXN = 19.25, MXN->USD = 1/17.5
        // EUR->USD = 19.25 / 17.5 = 1.10
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ExchangeRateResponse>();
        result.Should().NotBeNull();
        result!.Rate.Should().BeApproximately(eurMxnRate / usdMxnRate, 0.00001m);
    }

    /// <summary>
    /// Tests MXCB monthly rate is used for any day in the month.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(15)]
    [InlineData(30)]
    public async Task GetRate_MxcbMonthlyRate_UsedForAnyDayInMonth(int dayOfMonth)
    {
        // Arrange
        var monthStart = new DateTime(2024, 06, 01);
        var requestDate = new DateTime(2024, 06, dayOfMonth);
        var usdMxnRate = 17.5m;

        _factory.SetupTokenEndpoint();
        _factory.SetupMxcbMonthlyRatesEndpoint(monthStart, new Dictionary<string, decimal>
        {
            { "USD", usdMxnRate }
        });

        // Act
        var response = await GetClient().GetAsync(
            $"/api/rates?from=USD&to=MXN&date={requestDate:yyyy-MM-dd}&source={ExchangeRateSources.MXCB}&frequency={ExchangeRateFrequencies.Monthly}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ExchangeRateResponse>();
        result.Should().NotBeNull();
        result!.Rate.Should().Be(usdMxnRate);
    }

    /// <summary>
    /// Tests MXN to MXN same currency returns 1.
    /// </summary>
    [Fact]
    public async Task GetRate_MxnToMxn_SameCurrency_ReturnsOne()
    {
        // Arrange
        var date = new DateTime(2024, 06, 15);

        _factory.SetupTokenEndpoint();

        // Act
        var response = await GetClient().GetAsync(
            $"/api/rates?from=MXN&to=MXN&date={date:yyyy-MM-dd}&source={ExchangeRateSources.MXCB}&frequency={ExchangeRateFrequencies.Monthly}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ExchangeRateResponse>();
        result.Should().NotBeNull();
        result!.Rate.Should().Be(1m);
    }

    #endregion

    #region Historical/Future Date Tests

    /// <summary>
    /// Tests that future date falls back to latest available rate.
    /// </summary>
    [Fact]
    public async Task GetRate_FutureDate_FallsBackToLatestRate()
    {
        // Arrange
        var today = DateTime.Today;
        var futureDate = today.AddDays(30);
        var expectedRate = 1.0856m;

        _factory.SetupTokenEndpoint();
        _factory.SetupEcbDailyRatesEndpoint(today, new Dictionary<string, decimal>
        {
            { "USD", expectedRate }
        });

        // Act
        var response = await GetClient().GetAsync(
            $"/api/rates?from=EUR&to=USD&date={futureDate:yyyy-MM-dd}&source={ExchangeRateSources.ECB}&frequency={ExchangeRateFrequencies.Daily}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ExchangeRateResponse>();
        result.Should().NotBeNull();
        result!.Rate.Should().Be(expectedRate);
    }

    /// <summary>
    /// Tests that old date with historical data available returns that data.
    /// </summary>
    [Fact]
    public async Task GetRate_OldDateWithHistoricalData_ReturnsHistoricalRate()
    {
        // Arrange
        var oldDate = new DateTime(2020, 01, 15);
        var expectedRate = 1.1100m;

        _factory.SetupTokenEndpoint();
        _factory.SetupEcbDailyRatesEndpoint(oldDate, oldDate, new Dictionary<DateTime, Dictionary<string, decimal>>
        {
            { oldDate, new Dictionary<string, decimal> { { "USD", expectedRate } } }
        });

        // Act
        var response = await GetClient().GetAsync(
            $"/api/rates?from=EUR&to=USD&date={oldDate:yyyy-MM-dd}&source={ExchangeRateSources.ECB}&frequency={ExchangeRateFrequencies.Daily}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ExchangeRateResponse>();
        result.Should().NotBeNull();
        result!.Rate.Should().Be(expectedRate);
    }

    /// <summary>
    /// Tests that very old date with no available data returns an error response.
    /// NOTE: The exact response code depends on implementation - could be 404 or 500.
    /// </summary>
    [Fact(Skip = "Response varies by implementation - enable after implementing proper error handling")]
    public async Task GetRate_VeryOldDateNoData_ReturnsNotFound()
    {
        // Arrange
        var veryOldDate = new DateTime(1990, 01, 15);

        _factory.SetupTokenEndpoint();
        // Return empty rates for the requested date range
        _factory.SetupEcbDailyRatesEndpoint(veryOldDate, veryOldDate, new Dictionary<DateTime, Dictionary<string, decimal>>());

        // Act
        var response = await GetClient().GetAsync(
            $"/api/rates?from=EUR&to=USD&date={veryOldDate:yyyy-MM-dd}&source={ExchangeRateSources.ECB}&frequency={ExchangeRateFrequencies.Daily}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    #endregion

    #region Error Handling Tests

    /// Test failing due to my changes with validation, I left them this way on purpose,
    /// <summary>
    /// Tests that invalid "from" currency code triggers error path in repository.
    /// </summary>
    [Fact]
    public async Task GetRate_InvalidFromCurrency_ReturnsInternalServerError()
    {
        // Arrange
        var date = new DateTime(2024, 01, 15);
        _factory.SetupTokenEndpoint();

        // Act
        var response = await GetClient().GetAsync(
            $"/api/rates?from=INVALID&to=USD&date={date:yyyy-MM-dd}&source={ExchangeRateSources.ECB}&frequency={ExchangeRateFrequencies.Daily}");

        // Assert - Invalid currency code throws exception
        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
    }
    /// Test failing due to my changes with validation, I left them this way on purpose,
    /// <summary>
    /// Tests that invalid "to" currency code triggers error path in repository.
    /// </summary>
    [Fact]
    public async Task GetRate_InvalidToCurrency_ReturnsInternalServerError()
    {
        // Arrange
        var date = new DateTime(2024, 01, 15);
        _factory.SetupTokenEndpoint();

        // Act
        var response = await GetClient().GetAsync(
            $"/api/rates?from=EUR&to=INVALID&date={date:yyyy-MM-dd}&source={ExchangeRateSources.ECB}&frequency={ExchangeRateFrequencies.Daily}");

        // Assert - Invalid currency code throws exception
        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
    }

    /// <summary>
    /// Tests that empty "from" currency code triggers error path.
    /// </summary>
    [Fact]
    public async Task GetRate_EmptyFromCurrency_ReturnsInternalServerError()
    {
        // Arrange
        var date = new DateTime(2024, 01, 15);
        _factory.SetupTokenEndpoint();

        // Act
        var response = await GetClient().GetAsync(
            $"/api/rates?from=&to=USD&date={date:yyyy-MM-dd}&source={ExchangeRateSources.ECB}&frequency={ExchangeRateFrequencies.Daily}");

        // Assert - Empty string is invalid and handled by ASP.NET
        response.StatusCode.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.InternalServerError);
    }

    #endregion

    #region Edge Case Tests

    #endregion

    #region Cross-Currency Complex Scenarios

    /// <summary>
    /// Tests cross-currency conversion when both currencies need triangulation.
    /// </summary>
    [Fact]
    public async Task GetRate_CrossCurrency_BothNonProviderCurrencies_ReturnsCalculatedRate()
    {
        // Arrange
        var date = new DateTime(2024, 01, 15);
        var usdRate = 1.10m;
        var gbpRate = 0.85m;
        var jpyRate = 160.00m;

        _factory.SetupTokenEndpoint();
        _factory.SetupEcbDailyRatesEndpoint(date, new Dictionary<string, decimal>
        {
            { "USD", usdRate },
            { "GBP", gbpRate },
            { "JPY", jpyRate }
        });

        // Act - JPY to GBP via EUR
        var response = await GetClient().GetAsync(
            $"/api/rates?from=JPY&to=GBP&date={date:yyyy-MM-dd}&source={ExchangeRateSources.ECB}&frequency={ExchangeRateFrequencies.Daily}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ExchangeRateResponse>();
        result.Should().NotBeNull();
        var expectedRate = gbpRate / jpyRate;
        result!.Rate.Should().BeApproximately(expectedRate, 0.00001m);
    }

    /// <summary>
    /// Tests multiple pegged currency conversions in a chain.
    /// </summary>
    [Fact]
    public async Task GetRate_MultiplePeggedCurrencies_ReturnsCorrectRate()
    {
        // Arrange
        var date = new DateTime(2024, 09, 09);
        var usdRate = 1.10m;
        var aedPegRate = 0.27229m;
        var bamPegRate = 0.60000m;

        _factory.AddPeggedCurrency(new PeggedCurrency
        {
            CurrencyId = CurrencyTypes.AED,
            PeggedTo = CurrencyTypes.USD,
            Rate = aedPegRate
        });
        _factory.AddPeggedCurrency(new PeggedCurrency
        {
            CurrencyId = CurrencyTypes.BAM,
            PeggedTo = CurrencyTypes.EUR,
            Rate = bamPegRate
        });

        _factory.SetupTokenEndpoint();
        _factory.SetupEcbDailyRatesEndpoint(date, new Dictionary<string, decimal>
        {
            { "USD", usdRate },
            { "GBP", 0.85m }
        });

        // Act - BAM to AED via EUR and USD
        var response = await GetClient().GetAsync(
            $"/api/rates?from=BAM&to=AED&date={date:yyyy-MM-dd}&source={ExchangeRateSources.ECB}&frequency={ExchangeRateFrequencies.Daily}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ExchangeRateResponse>();
        result.Should().NotBeNull();
        var bamToEur = bamPegRate;
        var eurToUsd = usdRate;
        var usdToAed = 1 / aedPegRate;
        var expectedRate = bamToEur * eurToUsd * usdToAed;
        result!.Rate.Should().BeApproximately(expectedRate, 0.001m);
    }

    #endregion

    #region Fallback Scenario Tests

    /// <summary>
    /// Tests fallback over multiple days.
    /// </summary>
    [Fact]
    public async Task GetRate_FallbackOverMultipleDays_ReturnsLastAvailableRate()
    {
        // Arrange
        var availableDate = new DateTime(2024, 01, 10); // Wednesday
        var requestedDate = new DateTime(2024, 01, 15); // Monday (5 days later)
        var expectedRate = 1.0856m;

        _factory.SetupTokenEndpoint();
        _factory.SetupEcbDailyRatesEndpoint(availableDate, requestedDate, new Dictionary<DateTime, Dictionary<string, decimal>>
        {
            { availableDate, new Dictionary<string, decimal> { { "USD", expectedRate } } }
        });

        // Act
        var response = await GetClient().GetAsync(
            $"/api/rates?from=EUR&to=USD&date={requestedDate:yyyy-MM-dd}&source={ExchangeRateSources.ECB}&frequency={ExchangeRateFrequencies.Daily}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ExchangeRateResponse>();
        result.Should().NotBeNull();
        result!.Rate.Should().Be(expectedRate);
    }

    #endregion
}

/// <summary>
/// Custom WebApplicationFactory that configures WireMock for external API mocking.
/// This allows tests to mock external exchange rate APIs while using real internal implementations.
/// </summary>
public class ExchangeRateApiFactory : WebApplicationFactory<Program>
{
    private readonly WireMockServer _wireMockServer;
    private readonly InMemoryExchangeRateDataStore _dataStore;

    public ExchangeRateApiFactory()
    {
        _wireMockServer = WireMockServer.Start();
        _dataStore = new InMemoryExchangeRateDataStore();
    }

    public string WireMockUrl => _wireMockServer.Url!;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            // Remove existing data store registration
            var dataStoreDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IExchangeRateDataStore));
            if (dataStoreDescriptor != null)
            {
                services.Remove(dataStoreDescriptor);
            }

            // Register our shared in-memory data store for each test run
            services.AddSingleton<IExchangeRateDataStore>(_dataStore);

            // Override the ExternalExchangeRateApiConfig to point to WireMock
            var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(ExternalExchangeRateApiConfig));
            if (descriptor != null)
            {
                services.Remove(descriptor);
            }

            services.AddSingleton(new ExternalExchangeRateApiConfig
            {
                BaseAddress = _wireMockServer.Url!,
                TokenEndpoint = "/connect/token",
                ClientId = "test-client",
                ClientSecret = "test-secret"
            });
        });
    }

    public void SetupTokenEndpoint()
    {
        _wireMockServer
            .Given(Request.Create()
                .WithPath("/connect/token")
                .UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(@"{""access_token"": ""test-token"", ""token_type"": ""Bearer"", ""expires_in"": 3600}"));
    }

    #region ECB Daily Rates Setup

    public void SetupEcbDailyRatesEndpoint(DateTime date, Dictionary<string, decimal> rates)
    {
        var ratesJson = BuildRatesJson("EUECB", "EUR", "Indirect", date, rates);

        // Setup Latest endpoint
        _wireMockServer
            .Given(Request.Create()
                .WithPath("/v1/Banks/EUECB/DailyRates/Latest")
                .UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(ratesJson));

        // Setup TimeSeries endpoint (used for historical data)
        _wireMockServer
            .Given(Request.Create()
                .WithPath("/v1/Banks/EUECB/DailyRates/TimeSeries")
                .UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(ratesJson));
    }

    public void SetupEcbDailyRatesEndpoint(DateTime startDate, DateTime endDate, Dictionary<DateTime, Dictionary<string, decimal>> ratesByDate)
    {
        var ratesJson = BuildRatesJson("EUECB", "EUR", "Indirect", ratesByDate);

        // Setup TimeSeries endpoint
        _wireMockServer
            .Given(Request.Create()
                .WithPath("/v1/Banks/EUECB/DailyRates/TimeSeries")
                .UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(ratesJson));

        // Also setup Latest endpoint
        if (ratesByDate.Any())
        {
            var latestDate = ratesByDate.Keys.Max();
            var latestRates = BuildRatesJson("EUECB", "EUR", "Indirect", latestDate, ratesByDate[latestDate]);

            _wireMockServer
                .Given(Request.Create()
                    .WithPath("/v1/Banks/EUECB/DailyRates/Latest")
                    .UsingGet())
                .RespondWith(Response.Create()
                    .WithStatusCode(200)
                    .WithHeader("Content-Type", "application/json")
                    .WithBody(latestRates));
        }
    }

    #endregion

    #region ECB Monthly Rates Setup

    public void SetupEcbMonthlyRatesEndpoint(DateTime monthStart, Dictionary<string, decimal> rates)
    {
        var ratesJson = BuildRatesJson("EUECB", "EUR", "Indirect", monthStart, rates);

        // Setup Latest endpoint
        _wireMockServer
            .Given(Request.Create()
                .WithPath("/v1/Banks/EUECB/MonthlyRates/Latest")
                .UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(ratesJson));

        // Setup wildcard endpoint for any month (repository fetches historical months)
        _wireMockServer
            .Given(Request.Create()
                .WithPath(new WireMock.Matchers.RegexMatcher(@"/v1/Banks/EUECB/MonthlyRates/\d{4}/\d{1,2}"))
                .UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(ratesJson));
    }

    #endregion

    #region MXCB Monthly Rates Setup

    /// <summary>
    /// Sets up MXCB (Mexican Central Bank) monthly rates endpoint.
    /// MXCB uses Direct quote type: foreign currency to bank currency (e.g., 1 USD = 17.5 MXN).
    /// </summary>
    public void SetupMxcbMonthlyRatesEndpoint(DateTime monthStart, Dictionary<string, decimal> rates)
    {
        var ratesJson = BuildRatesJson("MXCB", "MXN", "Direct", monthStart, rates);

        // Setup Latest endpoint
        _wireMockServer
            .Given(Request.Create()
                .WithPath("/v1/Banks/MXCB/MonthlyRates/Latest")
                .UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(ratesJson));

        // Setup wildcard endpoint for any month (repository fetches historical months)
        _wireMockServer
            .Given(Request.Create()
                .WithPath(new WireMock.Matchers.RegexMatcher(@"/v1/Banks/MXCB/MonthlyRates/\d{4}/\d{1,2}"))
                .UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(ratesJson));
    }

    #endregion

    #region Pegged Currency Setup

    /// <summary>
    /// Adds a pegged currency to the data store.
    /// Pegged currencies have fixed exchange rates relative to another currency.
    /// </summary>
    public void AddPeggedCurrency(PeggedCurrency peggedCurrency)
    {
        _dataStore.AddPeggedCurrency(peggedCurrency);
    }

    #endregion

    public void ResetWireMock()
    {
        _wireMockServer.Reset();
    }

    private static string BuildRatesJson(string bankId, string baseCurrency, string quoteType, DateTime date, Dictionary<string, decimal> rates)
    {
        return BuildRatesJson(bankId, baseCurrency, quoteType, new Dictionary<DateTime, Dictionary<string, decimal>> { { date, rates } });
    }

    private static string BuildRatesJson(string bankId, string baseCurrency, string quoteType, Dictionary<DateTime, Dictionary<string, decimal>> ratesByDate)
    {
        var ratesEntries = new List<string>();

        foreach (var (date, rates) in ratesByDate)
        {
            var currencyEntries = rates.Select(r =>
                $"\"{r.Key}\": {{\"rate\": {r.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}");
            ratesEntries.Add($"\"{date:yyyy-MM-dd}T00:00:00\": {{{string.Join(", ", currencyEntries)}}}");
        }

        return $@"{{
            ""bankId"": ""{bankId}"",
            ""baseCurrency"": ""{baseCurrency}"",
            ""quoteType"": ""{quoteType}"",
            ""rates"": {{{string.Join(", ", ratesEntries)}}}
        }}";
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _wireMockServer.Stop();
            _wireMockServer.Dispose();
        }
    }
}

/// <summary>
/// Response model matching the API's ExchangeRateResponse.
/// </summary>
public record ExchangeRateResponse(
    string FromCurrency,
    string ToCurrency,
    DateTime Date,
    string Source,
    string Frequency,
    decimal Rate);
