using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using PaymentGateway.Api.Configuration;
using PaymentGateway.Api.Endpoints;
using PaymentGateway.Api.Models;
using PaymentGateway.Api.Services;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;
using Serilog;

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

var gatewaySettings = builder.Configuration
    .GetSection(GatewaySettings.SectionName)
    .Get<GatewaySettings>() ?? new GatewaySettings();

// Configure Distributed Cache (Redis or In-Memory fallback)
if (gatewaySettings.UseRedis)
{
    builder.Services.AddStackExchangeRedisCache(options =>
    {
        options.Configuration = gatewaySettings.RedisConnectionString;
        options.InstanceName = "PaymentGateway:";
    });
}
else
{
    builder.Services.AddDistributedMemoryCache();
}

// Register Domain & Application Services
builder.Services.AddScoped<IPaymentService, PaymentService>();

// Register Resilient Downstream Notification Client with Polly Pipeline
builder.Services.AddHttpClient<IDownstreamNotificationService, DownstreamNotificationService>()
    .AddResilienceHandler("downstream-pipeline", pipelineBuilder =>
    {
        // 1. Total request timeout: 10 seconds
        pipelineBuilder.AddTimeout(TimeSpan.FromSeconds(10));

        // 2. Retry: 3 attempts with exponential backoff + jitter (handling transient errors & 429/5xx)
        pipelineBuilder.AddRetry(new HttpRetryStrategyOptions
        {
            MaxRetryAttempts = 3,
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
            Delay = TimeSpan.FromMilliseconds(500),
            ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                .Handle<HttpRequestException>()
                .Handle<TimeoutRejectedException>()
                .HandleResult(response =>
                    (int)response.StatusCode >= 500 ||
                    response.StatusCode == System.Net.HttpStatusCode.RequestTimeout ||
                    response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        });

        // 3. Circuit Breaker: break for 30s if failure ratio > 50% over 10s sampling window
        pipelineBuilder.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
        {
            FailureRatio = 0.5,
            SamplingDuration = TimeSpan.FromSeconds(10),
            MinimumThroughput = 4,
            BreakDuration = TimeSpan.FromSeconds(30),
            ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                .Handle<HttpRequestException>()
                .Handle<TimeoutRejectedException>()
                .HandleResult(response =>
                    (int)response.StatusCode >= 500 ||
                    response.StatusCode == System.Net.HttpStatusCode.RequestTimeout)
        });
    });

builder.Services.AddOpenApi();

var app = builder.Build();

// Global Exception Handler returning standardized ApiErrorResponse
app.UseExceptionHandler(exceptionApp =>
{
    exceptionApp.Run(async context =>
    {
        var exceptionFeature = context.Features.Get<IExceptionHandlerFeature>();
        var exception = exceptionFeature?.Error;

        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
        logger.LogError(exception, "Unhandled exception processing request: {Path}", context.Request.Path);

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/json";

        var traceId = Activity.Current?.Id ?? context.TraceIdentifier;
        var errorResponse = new ApiErrorResponse(
            Code: "INTERNAL_SERVER_ERROR",
            Message: "An unexpected error occurred while processing your request.",
            TraceId: traceId
        );

        await context.Response.WriteAsJsonAsync(errorResponse);
    });
});

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

// Map Gateway Minimal API Endpoints
app.MapGatewayEndpoints();

// Root Health Check alias
app.MapGet("/health", () => Results.Ok(new
{
    status = "Healthy",
    timestamp = DateTimeOffset.UtcNow
}))
.WithName("HealthCheck")
.WithSummary("Root health probe endpoint.");

app.Run();


