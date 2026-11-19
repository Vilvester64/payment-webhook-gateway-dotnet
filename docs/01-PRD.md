# Product Requirements Document (PRD)

## Payment & Webhook Gateway Service

| Attribute | Details |
| :--- | :--- |
| **Document Version** | 1.0.0 |
| **Status** | Approved / Engineering Ready |
| **Target Framework** | .NET 10 (C# Minimal APIs) |
| **Target Audience** | Backend Engineers, Security Architects, Technical Product Managers |

---

## 1. Executive Summary & Problem Statement

Modern distributed commerce platforms face critical reliability and security challenges when interfacing with external Payment Service Providers (PSPs) and internal order-processing backends:

1. **Duplicate Payment Requests & Double Charges**: Unstable client networks and user retry behavior cause identical payment creation calls to arrive multiple times. Without distributed idempotency, systems risk double-charging users or creating orphaned payment records.
2. **Untrusted & Spoofed Webhooks**: Payment gateways (e.g., Midtrans, Xendit, Stripe) notify merchants asynchronously via HTTP callbacks. Malicious actors scan and exploit webhook endpoints with fabricated payloads if cryptographic signatures are not strictly verified.
3. **Downstream Cascading Failures**: When downstream fulfillment or order microservices experience high latency or transient outages, direct unbuffered forwarding causes connection starvation, thread exhaustion, and total gateway failure.

The **Payment & Webhook Gateway Service** provides an ultra-lightweight, resilient, and secure gateway layer built on .NET Minimal APIs. It guarantees zero duplicate processing through distributed caching, secures incoming webhooks via constant-time HMAC-SHA256 signature verification, and insulates downstream services with Polly resilience pipelines.

---

## 2. Target Personas & Stakeholders

```
+---------------------------------------------------------------------------------+
|                                 STAKEHOLDERS                                    |
+---------------------------------------------------------------------------------+
|  [Internal Clients]                  [External PSPs]          [Platform / Ops]  |
|  - Web / Mobile Frontends            - Midtrans               - SRE Engineers   |
|  - Checkout / Cart Services          - Xendit                 - Security Teams  |
|  - Order Fulfillment Service         - Stripe                 - Support Staff   |
+---------------------------------------------------------------------------------+
```

### Persona 1: Internal Commerce / Order Services
* **Role**: Initiators of payment transactions.
* **Core Need**: Reliable, deterministic payment initiation. Must guarantee that network retries never create duplicate charges.
* **Interaction**: Sends `POST /api/v1/payments` with unique `X-Idempotency-Key` headers.

### Persona 2: External Payment Service Providers (PSPs)
* **Role**: Third-party payment gateways dispatching asynchronous event notifications.
* **Core Need**: Ultra-fast acknowledgment (`200 OK` or `202 Accepted`) to prevent PSP retry storms, coupled with deterministic payload delivery.
* **Interaction**: Posts webhooks to `POST /api/v1/webhooks/{provider}` with cryptographic signatures in HTTP headers (`X-Webhook-Signature`).

### Persona 3: Platform Operations & SRE Engineers
* **Role**: Operators responsible for system uptime, telemetry, and security compliance.
* **Core Need**: Comprehensive structured logs (Serilog), correlation tracking (`X-Correlation-Id`), zero secret leakage, and flexible deployment models (in-memory for local dev, Redis for production).

---

## 3. Functional Requirements (FR)

### FR-01: Payment Intent Creation
* **Description**: The gateway must provide an endpoint to initiate payment intents with validated financial attributes (amount, currency, order reference, customer details).
* **Endpoint**: `POST /api/v1/payments`
* **Acceptance Criteria**:
  1. Rejects malformed requests (e.g., negative amount, unsupported ISO-4217 currency, missing order ID) with standard `400 Bad Request` and detailed error descriptors.
  2. Generates an internal `paymentIntentId` and returns state `Pending` or `Initiated`.
  3. Mandatory requirement of an `X-Idempotency-Key` header; missing header returns `400 Bad Request`.

### FR-02: Idempotent Request Handling
* **Description**: The gateway must intercept state-mutating requests via an endpoint filter and guarantee deterministic deduplication using an abstract distributed cache (`IDistributedCache`).
* **Cache Key Format**: `idempotency:payments:{idempotencyKey}`
* **Locking Strategy**: 
  1. On initial request, write an in-flight reservation marker (`STATE: IN_PROGRESS`) with a configurable lock TTL (e.g., 60 seconds).
  2. If an identical key arrives while `IN_PROGRESS`, return `409 Conflict` to prevent race conditions.
  3. Upon successful execution, overwrite the cache entry with the completed status code, response headers, and response body, applying a retention TTL (e.g., 24 hours).
  4. Subsequent requests with the same key must return the cached response with header `X-Idempotent-Replayed: true` and identical payload without re-executing downstream business logic.

### FR-03: Webhook Ingestion & Signature Verification
* **Description**: Ingest asynchronous payment event notifications and verify authenticity before processing.
* **Endpoint**: `POST /api/v1/webhooks/{provider}`
* **Acceptance Criteria**:
  1. Read raw request payload stream before model binding to avoid stream consumption issues (`EnableBuffering`).
  2. Compute HMAC-SHA256 digest over the raw payload bytes using a pre-configured provider secret.
  3. Verify the calculated digest against the `X-Webhook-Signature` header using fixed-time comparison (`CryptographicOperations.FixedTimeEquals`).
  4. If signature is invalid or header is missing, immediately reject with `401 Unauthorized`.
  5. Deduplicate webhooks using provider event identifiers (`eventId` or `paymentId`) to eliminate duplicate webhook callbacks.

### FR-04: Resilient Downstream Forwarding
* **Description**: Forward validated webhook events and payment processing calls to internal fulfillment or payment provider APIs via `HttpClient` guarded by Polly resilience pipelines.
* **Resilience Pipeline Configuration**:
  1. **Retry Strategy**: 3 attempts with exponential backoff (initial delay: 500ms, backoff factor: 2.0) with decorrelated jitter.
  2. **Circuit Breaker**: Break circuit when 50% of requests fail within a 30-second sampling window (minimum throughput: 10 requests); stay open for 15 seconds before transitioning to half-open.
  3. **Timeout Strategy**: Enforce a strict 5-second per-request timeout; overall pipeline timeout of 15 seconds.
  4. **Fallback / Graceful Failure**: On downstream failure after retries, return `502 Bad Gateway` or `504 Gateway Timeout` with standard error response, preserving error diagnostics.

### FR-05: Health, Telemetry & Diagnostics
* **Description**: The service must expose health endpoints and emit structured logs for all gateway interactions.
* **Acceptance Criteria**:
  1. `GET /health` returns `200 OK` with dependency health status (Cache availability, Downstream reachability).
  2. Automatically extract or generate `X-Correlation-Id` on every request and push it into Serilog `LogContext`.
  3. Mask all Sensitive Personal Information (SPI) and payment card details (PAN, CVV, customer auth tokens) from logs.

---

## 4. Non-Functional Requirements (NFR)

| ID | Category | Requirement Specification | Measurement & Target |
| :--- | :--- | :--- | :--- |
| **NFR-01** | **Latency** | Gateway overhead must be negligible for non-blocking operations. | • Cache hit replay: p95 < 15ms, p99 < 30ms.<br>• Gateway filter overhead: < 5ms (excluding downstream I/O). |
| **NFR-02** | **Consistency** | Zero double-charge or duplicate dispatch under concurrent load. | 100% deduplication of duplicate requests matching `X-Idempotency-Key` under concurrency tests (up to 50 concurrent hits/sec). |
| **NFR-03** | **Fault Tolerance** | System resilience against transient downstream outages. | Circuit breaker opens within 10 failures; fast-fails subsequent requests in < 2ms without degrading gateway memory/threads. |
| **NFR-04** | **Execution Portability** | Zero-dependency local developer inner loop. | Run directly using `dotnet run` out-of-the-box (`AddDistributedMemoryCache`) with zero required external daemons; seamless transition to Redis via single configuration toggle (`UseRedis: true`). |
| **NFR-05** | **Security & Integrity** | Constant-time validation, strict payload checks. | Zero susceptibility to timing side-channel attacks; reject payloads exceeding 1MB limit with `413 Payload Too Large`. |

---

## 5. Traceability Matrix

| Requirement | Module / Component | Verification Method |
| :--- | :--- | :--- |
| **FR-01** | `Endpoints/PaymentEndpoints.cs` | Integration Tests (`.http`, xUnit) |
| **FR-02** | `Filters/IdempotencyFilter.cs` | Concurrency Unit Tests (`xUnit`) |
| **FR-03** | `Filters/WebhookSignatureFilter.cs` | Cryptographic Unit Tests (`FixedTimeEquals`) |
| **FR-04** | `Services/DownstreamForwarder.cs` | Polly Mock Fault Injection Tests |
| **FR-05** | `Program.cs`, Serilog Config | Log Output & Trace ID Validation |

