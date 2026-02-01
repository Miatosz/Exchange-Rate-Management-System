# Exchange Rate API - Refactoring Notes

## Changes Made

### Rate Corrections

Problem: System threw exceptions when attempting to update existing exchange rates. Users could not correct previously entered rates.

Analysis:
1. `InMemoryExchangeRateDataStore.SaveExchangeRatesAsync` used `TryAdd()` which ignores duplicate keys
2. `ExchangeRateRepository.AddRateToDictionaries` threw exception when detecting rate differences

Solution Implemented:
- Changed `TryAdd(key, rate)` to indexer assignment `[key] = rate` for upsert semantics
- Removed exception throwing, added warning log when updating rates
- Dictionary now properly updates existing values instead of silently ignoring them

Impact: Rate corrections now work as expected - existing rates can be replaced with corrected values.

---

### Async/Await Refactoring

Problem: Multiple locations used `GetAwaiter().GetResult()` causing thread blocking and deadlock risks (sync-over-async anti-pattern).

Solution Implemented:
1. Converted `LoadRatesFromDb` to `LoadRatesFromDbAsync`
2. Replaced all `.GetAwaiter().GetResult()` with `await`
3. Converted methods to async in `ExchangeRateRepository`:
    - `GetRateAsync`
    - `UpdateRatesAsync`
    - `EnsureMinimumDateRangeAsync`
    - `LoadRatesFromDbAsync`
    - `GetMinFxDate`
4. Updated `IExchangeRateRepository` interface with async signatures
5. Modified `Program.cs` endpoint to use `await repository.GetRateAsync`

Result: All I/O operations now properly async, zero blocking calls remaining.

---

### Additional Improvements

#### Type Safety Enhancement
Problem: `ExchangeRateKey` used primitive types (int, string) instead of enums, causing type mismatches.

Solution: Changed `ExchangeRateKey` properties to use proper enum types.

---

## Test Results Analysis

### Passing Tests: 37/39

All core functionality tests pass, including:
- Basic exchange rate lookups (EUR-USD, USD-EUR, cross-rates)
- Historical date handling with fallback logic
- Multiple providers (ECB, HMRC, CB sources)
- Multiple frequencies (Daily, Monthly, Weekly, BiWeekly)
- Pegged currencies
- Edge cases (same currency, provider currency matches)

### Failing Tests: 2/39

#### Test 1: `GetRate_InvalidFromCurrency_ReturnsInternalServerError`

- Expected: 500 Internal Server Error
- Actual: 400 BadRequest
- Root Cause: Added FluentValidation (`GetExchangeRateRequestValidator`) which validates currency codes at request level
- Validation Rule: `RuleFor(x => x.From).Matches("^[A-Z]{3}$")` - rejects "INVALID" before it reaches repository

Analysis:

- Before refactoring: Invalid input -> Repository throws exception -> 500 error
- After refactoring:  Invalid input -> Validator rejects -> 400 BadRequest

Decision: Intentionally left failing

Justification (REST API Best Practices):
- 400 BadRequest = Client error - malformed request, invalid input
- 500 Internal Server Error = Server error - unexpected exceptions, bugs, database failures
- Invalid currency code is client's fault → should return 4xx, not 5xx
- Catching validation errors early (at request level) prevents unnecessary processing

What should be done: Update test assertion from `HttpStatusCode.InternalServerError` to `HttpStatusCode.BadRequest`

Test marked with comment:
`Test failing due to my changes with validation, I left them this way on purpose`



#### Test 2: `GetRate_InvalidToCurrency_ReturnsInternalServerError`

- Expected: 500 Internal Server Error
- Actual: 400 BadRequest
- Root Cause: Same as Test 1 - FluentValidation validates "to" currency code
- Validation Rule: `RuleFor(x => x.To).Matches("^[A-Z]{3}$")`

Decision: Intentionally left failing

Justification: Identical reasoning as Test 1 - proper HTTP status code usage per REST standards.

What should be done: Update test assertion from `HttpStatusCode.InternalServerError` to `HttpStatusCode.BadRequest`

Test marked with comment:
` Test failing due to my changes with validation, I left them this way on purpose `


---

### Verdict

Code is correct, tests expect outdated behavior.

The 2 failing tests were written before proper input validation existed. They test the error path (invalid currency codes) but expect the wrong HTTP status code.

This is actually an improvement:
- Security: Input validation at API boundary (defense in depth)
- Performance: Invalid requests rejected immediately without hitting repository/database
- Clarity: 400 clearly communicates "client sent bad data" vs 500 "server broke"

Production fix (if tests must be updated):

Change `response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);` -> `response.StatusCode.Should().Be(HttpStatusCode.BadRequest);`

Add `var content = await response.Content.ReadAsStringAsync(); content.Should().Contain("From");`

The original tests expected incorrect behavior. The new validation behavior is correct and should be kept.

---

## Issues Identified But Not Fixed

### SOLID Violations

#### 1. ExchangeRateRepository - God Class
Problem: Violates Single Responsibility Principle by handling:
- Data access (loading from `IExchangeRateDataStore`)
- In-memory caching
- Business logic (cross-currency calculations, pegged currencies)
- Provider orchestration (deciding when to fetch vs cache)

Impact:
- Hard to unit test individual concerns
- Changes to caching affect data access logic
- Complex state management

Proposed Refactoring:
Split into:
- `ExchangeRateCache` - In-memory caching with thread-safe operations
- `ExchangeRateCalculator` - Pure business logic (cross-rates, pegged currencies)
- `ExchangeRateRepository` - Orchestration only (delegates to cache + calculator)

Decision: Document it and leave for now - high risk refactoring - time consuming refactoring, potential test breakage. 

#### 2. ExchangeRateProviderFactory - Null Dereference Risk
Problem: `_serviceProvider.GetService()` which can return null, but no null check performed.

Impact: runtime exception if provider not properly registered in DI container.

Decision: document only.


### Security Issues

#### 1. Placeholder Secrets in Configuration
Problem: `appsettings.json` contains placeholder value `"ClientSecret": "your-client-secret"`

Impact: Critical if deployed as-is to production.

Mitigation: Code already uses `IConfiguration` and is ready for:
- User Secrets (development)
- Environment variables (production)
- Azure Key Vault / AWS Secrets Manager (production)

Decision: Document with production guidance - this is expected for sample code, tbd on production which way to choose.


#### 2. Exception Messages Leak ClientId
Problem: `ExternalApiExchangeRateProvider.cs` include `ClientId` in exception messages that may be logged.

Impact: medium - ClientId exposure in logs.

Decision: Document only.

### Performance Concerns

#### 1. Nested Dictionary
Structure:
`Dictionary<(Source, Freq), Dictionary<Currency, Dictionary<Date, decimal>>>`

Impact: Medium - complex access patterns, memory overhead

Trade-off: O(1) lookups vs memory complexity - acceptable for current scale.

Decision: Document only - pre-existing design, acceptable performance characteristics.

#### 2. No Response Caching
Problem: `Program.cs` has no OutputCache/ResponseCache middleware.

Impact: Low for current scope - exchange rates change infrequently.

Decision: document only - could add `[ResponseCache]` attribute for production.

### Thread-Safety

#### Dictionary Mutations Not Thread-Safe
Problem: `ExchangeRateRepository` uses regular `Dictionary<>` for cache, which is not thread-safe for concurrent writes.

Impact: multiple concurrent `UpdateRatesAsync` calls may cause race conditions.

Mitigation Options:
- Use `ConcurrentDictionary<>`
- Add locking strategy (`lock` or `SemaphoreSlim`)
- Document limitation and recommend single-threaded updates

Decision: pre-existing limitation, documented.


---

## AI Usage Summary

### Approach Strategy
- Used AI for: initial code review, identifying refactoring opportunities, async conversion strategy, code review of changes
- Critical evaluation: rejected "automatic async conversion" approach in favor of strategic bottom-up propagation
- Chose minimal safe changes over aggressive refactoring to minimize risk

### Key Decisions Made With AI Consultation
- Async refactoring - AI recommended strategic approach vs "add async everywhere"
- God Class - AI agreed with "document and defer" decision after risk analysis
- Test failures - AI confirmed new validation behavior is correct, tests are outdated

### What Worked Well
- Incremental approach to async refactoring
- Identifying root causes before implementing solutions
- Understanding trade-offs (code quality vs delivery risk vs time)

---

## Recommendations for Production


Required:
- Configure User Secrets / Key Vault for ClientSecret
- Update failing tests to expect correct HTTP status codes
- Add response caching middleware for exchange rate endpoints

Recommended:
- Implement thread-safety strategy
- Implement API versioning (/api/v1/rates)
- Add structured logging with correlation IDs
- Add health check endpoint


---

## Conclusion

Successfully completed rate corrections and async refactoring. All critical business logic tests pass. 
The 2 failing tests are due to improved validation that follows REST API best practices - the tests expect incorrect behavior and should be updated.

Identified several areas for improvement (God Class, thread-safety, security) but made the pragmatic decision to document rather than implement high-risk refactoring. 

