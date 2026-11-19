using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using PaymentGateway.Api.Configuration;
using PaymentGateway.Api.Endpoints;
using PaymentGateway.Api.Filters;
using PaymentGateway.Api.Health;
using PaymentGateway.Api.Middleware;
using PaymentGateway.Api.Services;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;
using Serilog;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// Configure Serilog structured logging
builder.Host.UseSerilog((context, services, configuration) =>
{
    configuration
        .ReadFrom.Configuration(context.Configuration)
        .Enrich.FromLogContext()
        .WriteTo.Console();
});

// Configure strongly-typed GatewaySettings
builder.Services.AddOptions<GatewaySettings>()
    .Bind(builder.Configuration.GetSection(GatewaySettings.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<ResilienceSettings>()
    .Bind(builder.Configuration.GetSection(ResilienceSettings.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

var gatewaySettings = builder.Configuration
    .GetSection(GatewaySettings.SectionName)
    .Get<GatewaySettings>() ?? new GatewaySettings();

var resilienceSettings = builder.Configuration
    .GetSection(ResilienceSettings.SectionName)
    .Get<ResilienceSettings>() ?? new ResilienceSettings();

// Configure the atomic idempotency store (Redis for multi-node, in-memory for local dev)
if (gatewaySettings.UseRedis)
{
    builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    {
        var options = ConfigurationOptions.Parse(gatewaySettings.RedisConnectionString);
        options.AbortOnConnectFail = false;
        options.ClientName = "PaymentGateway";
        return ConnectionMultiplexer.Connect(options);
    });

    builder.Services.AddSingleton<IIdempotencyStore, RedisIdempotencyStore>();
}
else
{
    builder.Services.AddSingleton<IIdempotencyStore, InMemoryIdempotencyStore>();
}

// Register Domain & Application Services
builder.Services.AddScoped<IPaymentService, PaymentService>();

// Register Resilient Downstream Notification Client with Polly Pipeline
builder.Services.AddHttpClient<IDownstreamNotificationService, DownstreamNotificationService>()
    .AddResilienceHandler("downstream-pipeline", pipelineBuilder =>
    {
        // 1. Overall pipeline timeout (outermost)
        pipelineBuilder.AddTimeout(TimeSpan.FromSeconds(resilienceSettings.TotalTimeoutSeconds));

        // 2. Retry: exponential backoff + jitter for transient failures and 429/5xx
        pipelineBuilder.AddRetry(new HttpRetryStrategyOptions
        {
            MaxRetryAttempts = resilienceSettings.MaxRetryAttempts,
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
            Delay = TimeSpan.FromMilliseconds(resilienceSettings.RetryBaseDelayMilliseconds),
            ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                .Handle<HttpRequestException>()
                .Handle<TimeoutRejectedException>()
                .HandleResult(response =>
                    (int)response.StatusCode >= 500 ||
                    response.StatusCode == System.Net.HttpStatusCode.RequestTimeout ||
                    response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        });

        // 3. Per-attempt timeout
        pipelineBuilder.AddTimeout(TimeSpan.FromSeconds(resilienceSettings.AttemptTimeoutSeconds));

        // 4. Circuit breaker: stop hammering a failing downstream
        pipelineBuilder.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
        {
            FailureRatio = resilienceSettings.CircuitBreakerFailureRatio,
            SamplingDuration = TimeSpan.FromSeconds(resilienceSettings.CircuitBreakerSamplingSeconds),
            MinimumThroughput = resilienceSettings.CircuitBreakerMinimumThroughput,
            BreakDuration = TimeSpan.FromSeconds(resilienceSettings.CircuitBreakerBreakSeconds),
            ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                .Handle<HttpRequestException>()
                .Handle<TimeoutRejectedException>()
                .HandleResult(response =>
                    (int)response.StatusCode >= 500 ||
                    response.StatusCode == System.Net.HttpStatusCode.RequestTimeout)
        });
    });

builder.Services.AddHealthChecks()
    .AddCheck<IdempotencyStoreHealthCheck>("idempotency-store", tags: ["ready"]);

// Rate limit payment creation per API key (falling back to client IP) to blunt abuse and retry storms
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.OnRejected = async (context, cancellationToken) =>
    {
        var httpContext = context.HttpContext;

        var problem = new ProblemDetails
        {
            Type = "https://errors.paymentgateway.internal/rate-limit-exceeded",
            Title = "Too Many Requests",
            Status = StatusCodes.Status429TooManyRequests,
            Detail = "The payment rate limit has been exceeded. Retry after the current window.",
            Instance = httpContext.Request.Path
        };

        problem.Extensions["code"] = "RATE_LIMIT_EXCEEDED";
        problem.Extensions["traceId"] = httpContext.TraceIdentifier;
        problem.Extensions["timestamp"] = DateTimeOffset.UtcNow;

        httpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        httpContext.Response.ContentType = "application/problem+json";
        await httpContext.Response.WriteAsJsonAsync(problem, cancellationToken);
    };

    options.AddPolicy("payments", httpContext =>
    {
        // Read options per request so configuration overrides (and reloads) apply.
        var settings = httpContext.RequestServices.GetRequiredService<IOptions<GatewaySettings>>().Value;
        var apiKey = httpContext.Request.Headers[ApiKeyFilter.HeaderName].ToString();
        var partitionKey = !string.IsNullOrWhiteSpace(apiKey)
            ? $"key:{apiKey}"
            : $"ip:{httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";

        return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = settings.PaymentsRateLimitPermitLimit,
            Window = TimeSpan.FromSeconds(settings.PaymentsRateLimitWindowSeconds),
            QueueLimit = 0,
            AutoReplenishment = true
        });
    });
});

builder.Services.AddOpenApi();

var app = builder.Build();

// Correlation ID must wrap everything so exception handling and downstream logs inherit it
app.UseMiddleware<CorrelationIdMiddleware>();

// Global Exception Handler returning standardized RFC 7807 problem details
app.UseExceptionHandler(exceptionApp =>
{
    exceptionApp.Run(async context =>
    {
        var exceptionFeature = context.Features.Get<IExceptionHandlerFeature>();
        var exception = exceptionFeature?.Error;

        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
        logger.LogError(exception, "Unhandled exception processing request: {Path}", context.Request.Path);

        var problem = new ProblemDetails
        {
            Type = "https://errors.paymentgateway.internal/internal-server-error",
            Title = "Internal Server Error",
            Status = StatusCodes.Status500InternalServerError,
            Detail = "An unexpected error occurred while processing your request.",
            Instance = context.Request.Path
        };

        problem.Extensions["code"] = "INTERNAL_SERVER_ERROR";
        problem.Extensions["traceId"] = context.TraceIdentifier;
        problem.Extensions["timestamp"] = DateTimeOffset.UtcNow;

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsJsonAsync(problem);
    });
});

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseRateLimiter();

// Enable request body buffering before endpoints/model binders consume the stream
app.Use(async (context, next) =>
{
    context.Request.EnableBuffering();
    await next(context);
});

// Map Gateway Minimal API Endpoints
app.MapGatewayEndpoints();

// Dependency-aware health probe (cache/store reachability)
app.MapHealthChecks("/health", new HealthCheckOptions
{
    ResponseWriter = async (context, report) =>
    {
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new
        {
            status = report.Status.ToString(),
            checks = report.Entries.Select(entry => new
            {
                name = entry.Key,
                status = entry.Value.Status.ToString(),
                description = entry.Value.Description
            }),
            timestamp = DateTimeOffset.UtcNow
        });
    }
});

app.Run();

public partial class Program;
