# Payment & Webhook Gateway Service

[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![C#](https://img.shields.io/badge/C%23-13.0-239120?logo=csharp&logoColor=white)](https://learn.microsoft.com/en-us/dotnet/csharp/)
[![Polly](https://img.shields.io/badge/Resilience-Polly%20v8-blue)](https://www.pollydocs.org/)
[![Distributed Cache](https://img.shields.io/badge/Cache-Redis%20%7C%20Memory-DC382D?logo=redis&logoColor=white)](https://redis.io/)
[![Serilog](https://img.shields.io/badge/Logging-Serilog-green)](https://serilog.net/)

> High-throughput, resilient, and cryptographically verified Payment & Webhook Gateway built with .NET 10 Minimal APIs, Polly v8 resilience pipelines, and distributed idempotency.

---

## Executive Overview

In modern distributed commerce architectures, payment processing and asynchronous webhook callbacks are the most failure-prone touchpoints:
* Network blips cause clients to re-issue duplicate payment requests, leading to potential **double charges**.
* Malicious actors attempt to spoof webhook callbacks to falsely credit customer balances.
* Transient downtime in downstream fulfillment services can cascade upstream into gateway failures.

The **Payment & Webhook Gateway Service** resolves these failure modes by acting as a lightweight, secure gateway layer. It ensures strict request idempotency via distributed caching, enforces constant-time HMAC-SHA256 signature verification on incoming webhooks, and wraps downstream communication in Polly fault-tolerant resilience pipelines.

---

## Key Engineering Highlights

### 1. Distributed Idempotency (`X-Idempotency-Key`)
* Guarantees exact-once processing semantics for state-mutating requests (`POST /api/v1/payments`).
* Prevents race conditions using an atomic in-flight reservation marker (`IN_PROGRESS`). Concurrent duplicate calls immediately receive `409 Conflict`.
* Successful responses are cached and replayed for subsequent requests with header `X-Idempotent-Replayed: true` without hitting downstream processors.

### 2. Timing-Safe HMAC-SHA256 Webhook Verification
* Buffers the incoming raw request stream before model binding to preserve byte-exact integrity.
* Computes keyed HMAC-SHA256 digests matching external provider signatures (`Midtrans`, `Xendit`, `Stripe`).
* Executes signature verification using **`CryptographicOperations.FixedTimeEquals`** to eliminate timing side-channel attack vulnerabilities.

### 3. Polly Resilience Pipelines
* Outbound downstream forwarding uses `Microsoft.Extensions.Http.Resilience` (Polly v8).
* **Retry Strategy**: 3 attempts with exponential backoff and decorrelated jitter.
* **Circuit Breaker**: Breaks connection on 50% failure rate across a 30-second window, protecting downstream services from cascading collapse.
* **Timeout & Isolation**: 5-second per-request timeout with an overall 15-second pipeline timeout.

### 4. Zero-Dependency Developer Experience
* Runs instantly on local developer machines without external dependencies using `AddDistributedMemoryCache`.
* Production-ready switch to Redis via a single configuration flag (`"UseRedis": true`).


---

## Quick-Start Guide

### Prerequisites
* [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (or higher)
* Optional: [Docker](https://www.docker.com/) (only if running Redis locally)

### Option A: Zero-Dependency Local Mode (Default)
By default, the service initializes using an in-memory distributed cache (`AddDistributedMemoryCache`), requiring no external Docker containers or background services.

```bash
# Clone the repository
git clone https://github.com/your-username/PaymentWebhookGateway.git
cd PaymentWebhookGateway

# Build solution
dotnet build

# Run the API
dotnet run --project PaymentGateway.Api
```
The service starts at `https://localhost:7001` (or `http://localhost:5000`).

### Option B: Redis Distributed Cache Mode
To test with production-equivalent distributed caching:

1. Start a local Redis instance via Docker:
   ```bash
   docker run -d --name payment-redis -p 6379:6379 redis:alpine
   ```

2. Enable Redis in `PaymentGateway.Api/appsettings.Development.json`:
   ```json
   {
     "GatewayOptions": {
       "UseRedis": true,
       "RedisConnectionString": "localhost:6379"
     }
   }
   ```

3. Launch the API:
   ```bash
   dotnet run --project PaymentGateway.Api
   ```

---

## Directory Structure

```
PaymentWebhookGateway/
├── docs/                                  # Complete Engineering Specification Suite
│   ├── 01-PRD.md                          # Product Requirements Document
│   ├── 02-ARCHITECTURE-AND-DESIGN.md      # High-level architecture & sequence diagrams
│   ├── 03-SCOPE-AND-LIMITATIONS.md        # In-scope/out-of-scope boundaries & trade-offs
│   ├── 04-SECURITY.md                     # HMAC-SHA256, timing attacks & STRIDE threat model
│   ├── 05-DEVELOPMENT-ROADMAP.md          # Implementation phases & Definition of Done
│   └── 06-FUTURE-UPDATES-ROADMAP.md       # Post-MVP scale (MassTransit, Kafka, PostgreSQL)
├── PaymentGateway.Api/                    # .NET 10 Minimal APIs Gateway Service
│   ├── Configuration/                     # Options pattern & strongly-typed settings
│   ├── Endpoints/                         # Minimal API route definitions
│   ├── Filters/                           # IdempotencyFilter & WebhookSignatureFilter
│   ├── Middleware/                        # Correlation ID & Exception handling
│   ├── Models/                            # DTOs, Envelopes & Request/Response schemas
│   ├── Services/                          # IdempotencyService & DownstreamForwarder (Polly)
│   ├── Program.cs                         # Application bootstrap & DI container
│   ├── appsettings.json                   # Configuration defaults
│   └── PaymentGateway.Api.http            # Runnable HTTP integration test scenarios
├── PaymentGateway.Tests/                  # Automated Testing Suite (xUnit)
└── README.md                              # Project overview & quick-start guide
```

---

## Running Automated Tests

```bash
# Run unit and filter tests via xUnit
dotnet test

# Run manual/interactive API scenarios
# Open PaymentGateway.Api/PaymentGateway.Api.http in Visual Studio or VS Code (REST Client)
```

