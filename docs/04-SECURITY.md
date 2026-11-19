# Security Architecture & Threat Model

## Payment & Webhook Gateway Service

| Attribute | Details |
| :--- | :--- |
| **Document Version** | 1.0.0 |
| **Status** | Approved / Engineering Ready |
| **Classification** | Public / Open-Source Architecture |

---

## 1. Security Architecture Principles

The Payment & Webhook Gateway operates in a zero-trust network environment where external incoming traffic must be verified cryptographically before invoking internal compute or downstream resources.

The security architecture enforces five core tenets:
1. **Verifiable Authenticity**: Every webhook must prove origin and data integrity via symmetric cryptographic digests.
2. **Side-Channel Invariance**: All cryptographic comparison operations must execute in constant time to prevent timing side-channel attacks.
3. **Replay Immunity**: Both client-initiated payments and provider-dispatched webhooks are guarded by unique nonce/event keys and timestamp boundaries.
4. **Defense in Depth & Least Privilege**: Secrets are separated from source code; error responses strictly redact internal stack traces and topologies.
5. **Data Minimization & SPI Sanitization**: No raw credit card Primary Account Numbers (PANs), CVVs, or secret tokens are persisted or emitted to logs.

---

## 2. Webhook Cryptographic Verification (HMAC-SHA256)

### 2.1 The Signature Scheme
Payment providers calculate an HMAC-SHA256 digest over the raw request body using a shared secret key. The resulting hash is transmitted in an HTTP header:
```http
X-Webhook-Signature: sha256=d3b07384d113edec49eaa6238ad5ff00 ...
```

### 2.2 Raw Stream Buffering & Hashing Flow
Standard ASP.NET Core JSON deserializers consume the request body stream once. To verify the signature without breaking model binding:
1. Enable stream buffering via `HttpRequest.EnableBuffering()`.
2. Read the raw request stream from position 0 into a byte buffer.
3. Compute the keyed HMAC-SHA256 digest over the raw byte buffer.
4. Reset `HttpRequest.Body.Position = 0` so downstream endpoint handlers can parse the JSON payload.

### 2.3 Timing-Attack Mitigation (`FixedTimeEquals`)
Standard string equality (`==` or `.Equals()`) terminates early upon finding the first non-matching character. Attackers can measure response time variances at nanosecond resolutions to brute-force valid signatures byte by byte.

To prevent this, the gateway uses **`CryptographicOperations.FixedTimeEquals`**:

```csharp
public static bool VerifyHmacSha256(byte[] payloadBytes, string providedHexSignature, string secretKey)
{
    byte[] keyBytes = Encoding.UTF8.GetBytes(secretKey);
    using var hmac = new HMACSHA256(keyBytes);
    byte[] computedHash = hmac.ComputeHash(payloadBytes);
    
    // Normalize signature (strip 'sha256=' prefix if present)
    string normalizedHex = providedHexSignature.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase)
        ? providedHexSignature[7..]
        : providedHexSignature;

    byte[] providedBytes = Convert.FromHexString(normalizedHex);
    
    // Constant-time execution prevents timing side-channel leaks
    return CryptographicOperations.FixedTimeEquals(computedHash, providedBytes);
}
```

---

## 3. Replay-Attack Prevention

The gateway mitigates replay attacks using a two-tier defense:

```
[Incoming Request]
        │
        ▼
[Check Timestamp Drift] ───(Drift > 300s)───► [401 Unauthorized: Expired Request]
        │ (Within 5-minute window)
        ▼
[Check Event Cache]      ───(Event Found)────► [200 OK: Replayed / Deduplicated]
        │ (Cache Miss)
        ▼
[Store Event in Cache]   ───(TTL: 48h)───────► [Execute Downstream Forwarding]
```

1. **Timestamp Freshness Window**: If the provider supplies a timestamp header (e.g., `X-Webhook-Timestamp`), requests with a drift greater than **300 seconds (5 minutes)** are rejected to stop stale replayed requests.
2. **Event Nonce Deduplication**: Webhook event IDs (e.g., `evt_...`) and payment idempotency keys are recorded in `IDistributedCache`. Repeated transmissions are safely replayed or acknowledged without re-triggering business processes.


---

## 4. Secret Management Guidelines

| Environment | Secret Storage Mechanism | Committal Policy |
| :--- | :--- | :--- |
| **Local Development** | .NET Secret Manager (`dotnet user-secrets`) | **NEVER COMMITTED**. Stored outside project tree in user profile directory. |
| **CI / CD Pipelines** | GitHub Actions Secrets / Azure DevOps Protected Variables | Injected into environment variables at build/test execution. |
| **Production Staging/Prod**| Cloud Secret Vaults (AWS Secrets Manager, Azure Key Vault, HashiCorp Vault) | Mounted as runtime environment variables or loaded via configuration providers. |

### Configuration Safety Rules:
* `appsettings.json` must contain only schema placeholders or empty strings for secrets (`"WebhookSecret": ""`).
* Repository `.gitignore` must enforce exclusion of `*.user.json`, `appsettings.Production.json`, and `.env` files.
* CI checks fail automatically if regex detection identifies API secret keys committed to Git history.

---

## 5. Input Validation & Standardized Error Masking

1. **Payload Size Capping**: Rejects any request exceeding 1 MB with `413 Payload Too Large` before buffering into memory.
2. **Standardized Error Masking (`ApiErrorResponse`)**: 
   * Internal unhandled exceptions (`500 Internal Server Error`) are caught by global middleware.
   * Stack traces, database connection strings, and internal server IPs are **never** rendered in client responses.
   * Responses return a safe RFC 7807 Problem Details object containing only a correlation ID (`X-Correlation-Id`).
3. **Structured Log Sanitization**: Serilog enrichers explicitly exclude headers containing auth tokens or signature digests, and redact card numbers using regex pattern masks.

---

## 6. STRIDE Threat Model Summary

| Threat Category | Potential Attack Vector | Gateway Mitigation |
| :--- | :--- | :--- |
| **Spoofing** | Attacker impersonates Midtrans/Xendit to send fake payment success notifications. | Mandatory HMAC-SHA256 signature verification over raw request bytes using `FixedTimeEquals`. |
| **Tampering** | Man-in-the-Middle modifies payment amount or order reference in transit. | Digest mismatch causes HMAC validation failure; mandatory TLS 1.3 encryption in transit. |
| **Repudiation** | Client claims payment request was submitted multiple times due to gateway error. | Immutable `X-Idempotency-Key` logs with exact timestamp and cached response replay proofs. |
| **Information Disclosure** | Downstream failure leaks database stack traces or internal microservice endpoints. | Centralized RFC 7807 error masking middleware returns sanitized problem details envelopes. |
| **Denial of Service** | Flooding gateway with huge payloads or triggering slow downstream timeouts. | 1 MB request size limit; Polly circuit breaker and 5-second per-request timeouts protect threads. |
| **Elevation of Privilege**| Unauthorized manipulation of system health or administration routes. | Health checks expose diagnostic status only; no administrative state manipulation endpoints exist. |

