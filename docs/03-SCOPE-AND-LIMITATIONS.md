# Project Scope & Architectural Boundaries

## Payment & Webhook Gateway Service

| Attribute | Details |
| :--- | :--- |
| **Document Version** | 1.0.0 |
| **Status** | Approved / Engineering Ready |
| **Architectural Role** | Stateless / Cache-backed API Gateway & Ingestion Middleware |

---

## 1. Architectural Role & Boundary Overview

The **Payment & Webhook Gateway Service** is designed as a focused, lightweight, and resilient gateway layer. It mediates communications between external entities (clients, payment gateways) and internal systems (order fulfillment services, core banking adapters).

To preserve high throughput, low operational overhead, and clear separation of concerns, the service deliberately constrains its scope: it acts as a **stateless, cache-coordinated reverse proxy and cryptographic validator**, not an end-to-end commerce ERP or banking system.

---

## 2. In-Scope vs. Out-of-Scope Matrix

| Functional Area | In-Scope (Gateway Responsibility) | Out-of-Scope (External / Downstream Responsibility) |
| :--- | :--- | :--- |
| **Payment Ingestion** | • Validating request schema and required headers.<br>• Idempotency reservation and cached response replay.<br>• Synthesizing unified payment intent responses. | • Card tokenization and PCI-DSS PAN storage.<br>• Direct communication with credit card acquiring networks.<br>• Issuing multi-currency forex conversions. |
| **Webhook Processing** | • Raw byte stream buffering for cryptographic integrity.<br>• Constant-time HMAC-SHA256 signature verification.<br>• Event deduplication against duplicate provider callbacks. | • Complex business state transitions (e.g., triggering warehouse inventory allocation).<br>• Asynchronous workflow orchestration (e.g., Saga patterns). |
| **Resilience & Routing** | • Transient fault handling via Polly (exponential retry, circuit breaking, timeouts).<br>• Unified error masking conforming to RFC 7807 Problem Details. | • Long-term dead-letter queue (DLQ) storage or manual replay portals (planned for v1.1).<br>• Dynamic cross-region multi-cloud traffic failover. |
| **Financial Ledgers** | • Caching payment intent identifiers and transient processing states. | • Double-entry general ledger accounting.<br>• Holding customer escrow funds or deposit balances.<br>• Generating fiscal invoices, tax calculations, or VAT reports. |
| **Identity & Access** | • Endpoint authentication via API keys, provider secrets, and signature headers.<br>• Correlation ID tracking across requests. | • Customer user management, OAuth2 identity provider federation, or session cookies. |

---

## 3. Technical Constraints & Trade-Off Analysis

### 3.1 Memory Cache vs. Distributed Redis

| Dimension | In-Memory Cache (`AddDistributedMemoryCache`) | Distributed Redis (`StackExchange.Redis`) |
| :--- | :--- | :--- |
| **Execution Environment** | Local development, CI/CD pipelines, single-instance dev boxes. | Production multi-instance clusters (Kubernetes, ECS). |
| **Persistence** | **Volatile**: Lost immediately on process restart or crash. | **Durable**: Maintained across application restarts and rolling deployments. |
| **Multi-Node Concurrency** | **Unsupported**: Multiple instances do not share state; duplicate requests to different nodes will cause race conditions. | **Supported**: Centralized atomic operations (`SETNX`, distributed key TTLs) guarantee global idempotency across replicas. |
| **Operational Cost** | Zero external infrastructure or Docker requirements. | Requires managed Redis instance (ElastiCache, Azure Redis, or Docker daemon). |
| **Architectural Trade-Off** | Accepted for local developer productivity (`UseRedis: false`). Must be strictly prohibited in multi-node production setups. |

### 3.2 Synchronous HTTP Forwarding with Polly vs. Asynchronous Message Broker

* **Current Architecture (v1.0 MVP)**: Synchronous forwarding over HTTP via `IDownstreamForwarder` utilizing Polly resilience pipelines (Retry, Circuit Breaker, Timeout).
* **Trade-Off**:
  * *Benefits*: Minimal architectural footprint, zero broker infrastructure overhead (no Kafka/RabbitMQ brokers to provision, secure, or monitor), immediate end-to-end feedback to caller.
  * *Trade-offs*: The calling webhook provider waits on downstream HTTP completion. Under extended downstream outages where the circuit breaker trips, requests return `502/504`, relying on the payment provider's external retry schedule.
* **Evolution Target**: Decoupling via message queues (MassTransit / RabbitMQ) is scheduled for **v1.2** for asynchronous event streaming.

### 3.3 Payload Size & Memory Buffering Boundaries

* **Payload Limit**: Maximum request body size is capped at **1 MB (1,048,576 bytes)**.
* **Stream Buffering**: Cryptographic signature validation requires reading the raw request body stream before deserialization.
* **Trade-Off**: While ASP.NET Core `EnableBuffering()` handles small streams in memory and spills larger streams to temporary disk buffers, strictly enforcing a 1 MB limit guarantees memory safety and mitigates Denial of Service (DoS) attacks via memory exhaustion.

### 3.4 Idempotency TTL & Key Retention Boundaries

* **In-Progress Lock TTL**: **60 Seconds**.
  * *Rationale*: Protects against concurrent race conditions during inflight execution. If an execution exceeds 60 seconds (abandoned due to crash), the lock auto-expires to prevent permanent deadlocks.
* **Completed Response Retention TTL**: **24 Hours**.
  * *Rationale*: Covers standard client retry windows (typically seconds to minutes) while preventing indefinite memory expansion in Redis. Clients retrying after 24 hours must initiate a new transaction or query the order service directly.
