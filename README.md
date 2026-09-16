# webhook-delivery-service

The infrastructure behind Stripe style webhooks: signed payloads, exponential backoff retries, a dead letter queue,
and a delivery history you can replay from.

![.NET](https://img.shields.io/badge/.NET-9.0-512BD4)
![SQL Server](https://img.shields.io/badge/SQL%20Server-2022-CC2927)
![Docker](https://img.shields.io/badge/Docker-Compose-2496ED)
![Tests](https://img.shields.io/badge/tests-96%20passing-success)
![License](https://img.shields.io/badge/license-MIT-green)

## Description

Sending a webhook is one HTTP POST. Running a webhook platform is everything around it: the customer's server will be
down, will be slow, will return 500 for an hour, will reject the payload as invalid, and will eventually ask you to
resend everything it missed.

This service handles that. An application registers an endpoint, publishes events, and the platform takes
responsibility for getting them delivered.

## Objective

Show the design decisions that separate a `HttpClient.PostAsync` from a delivery platform: what gets retried and what
does not, how payloads are signed against replay, how several workers share a queue without double delivering, and
what a customer does when deliveries have been failing for two days.

## How it works

```
  producer                  webhook-delivery-service                  customer
 application                                                          endpoint
     |                                                                    |
     |  POST /api/events                                                  |
     |  { "eventType": "order.created", "data": {...} }                   |
     |------------------------>  [ API ]                                  |
     |                              |                                     |
     |                              |  1. record the event once           |
     |                              |  2. create one delivery per         |
     |                              |     subscribed endpoint             |
     |  202 + eventId               |                                     |
     |<------------------------     |                                     |
     |                              v                                     |
     |                         [ database ]                               |
     |                              ^                                     |
     |                              |  claim a batch of due deliveries    |
     |                              |  (row locks, so N workers can poll) |
     |                         [ worker ]                                 |
     |                              |                                     |
     |                              |  POST, signed with HMAC SHA256      |
     |                              |----------------------------------->|
     |                              |                                     |
     |                              |  2xx  -> Delivered                  |
     |                              |  5xx  -> retry with backoff         |
     |                              |  4xx  -> dead letter immediately    |
     |                              |<-----------------------------------|
```

The API and the worker are separate processes sharing a database. Publishing stays fast because the API only writes
rows, and delivery throughput scales by running more workers.

## Features

- **HMAC SHA256 signatures** over `{timestamp}.{payload}`, so a captured request cannot be replayed
- **Exponential backoff with jitter**: 1m, 5m, 15m, 1h, 6h, then dead letter
- **4xx is not retried**, because sending the same invalid request again changes nothing
- **Dead letter queue** with one click replay, keeping the original attempt history
- **Full delivery history**: every attempt, its status code, its response body and how long it took
- **Idempotent publishing** through an `Idempotency-Key` header
- **Wildcard subscriptions**: `order.*` or `*`
- **Automatic endpoint disabling** after a configurable number of consecutive failures
- **Secret rotation** without re registering the endpoint
- **Multiple workers** sharing one queue safely through row level locking

## The decisions worth explaining

### Why the timestamp is inside the signature

The signed value is `{timestamp}.{payload}`, not the payload alone.

Signing only the payload means a captured request stays valid forever, so anyone who records one webhook can replay it
at will. Binding the timestamp into the signature lets the receiver reject anything outside a tolerance window.

```csharp
var signedPayload = $"{unixTimestamp}.{payload}";
return Convert.ToHexString(HMACSHA256.HashData(key, message)).ToLowerInvariant();
```

Verification uses `CryptographicOperations.FixedTimeEquals`. A plain string comparison leaks, through timing, how many
leading characters matched, which is enough to forge a signature one byte at a time.

Both a stale timestamp and one in the future are rejected, so clock skew does not open a hole.

### Why 4xx is not retried

| Response | Retried | Why |
| --- | --- | --- |
| 5xx | Yes | The server is broken, it may recover |
| 408, 429 | Yes | The server is saying "not now", not "never" |
| Other 4xx | **No** | The request itself was wrong, resending it unchanged gets the same answer |
| Network failure | Yes | DNS, TLS and connection failures are usually transient |

A 422 retried five times over seven hours wastes capacity on both sides and delays the customer discovering their
endpoint is rejecting the payload. It goes to the dead letter queue on the first attempt, with the reason recorded.

### Why the retry delays have jitter

Without jitter, an endpoint that goes down and comes back gets every pending delivery at the same instant, which is
how a recovering service is knocked over again by its own backlog.

```csharp
var offset = (random.NextDouble() * 2 - 1) * _jitterFactor;
return TimeSpan.FromTicks((long)(delay.Ticks * (1 + offset)));
```

The default spread is 20 percent, so a 5 minute delay lands somewhere between 4 and 6 minutes.

### How several workers share one queue

Two workers polling the same table would both read the same rows and deliver the same webhook twice. The claim query
takes row locks and skips rows another worker already holds:

```sql
SELECT TOP (@batchSize) Id
FROM Deliveries WITH (UPDLOCK, READPAST, ROWLOCK)
WHERE Status IN ('Pending', 'Scheduled')
  AND NextAttemptAt <= @now
ORDER BY NextAttemptAt
```

| Hint | Effect |
| --- | --- |
| `UPDLOCK` | Takes an update lock on read, so a second reader cannot race for the same row |
| `READPAST` | Skips locked rows instead of blocking behind them |
| `ROWLOCK` | Keeps granularity at the row, so one worker does not lock a page out from under the others |

PostgreSQL spells the same idea `FOR UPDATE SKIP LOCKED`.

### Why redirects are not followed

```csharp
.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    AllowAutoRedirect = false,
    // ...
});
```

Following a redirect on an outbound webhook is an SSRF vector: a customer could register a public URL that redirects
to an internal address, and the platform would happily follow it.

### Why liveness does not check the database

```csharp
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });
```

Liveness answers "is this process alive". If it checked the database, a database outage would make the orchestrator
kill and restart perfectly healthy API instances, turning a recoverable problem into a crash loop. Readiness is where
dependency checks belong.

## Technologies

| Area | Stack |
| --- | --- |
| Runtime | .NET 9, ASP.NET Core Minimal APIs, `BackgroundService` |
| Persistence | Entity Framework Core 9, SQL Server 2022 |
| Signing | `HMACSHA256`, `CryptographicOperations.FixedTimeEquals` |
| Documentation | Swagger / OpenAPI |
| Logging | Serilog |
| Testing | xUnit, FluentAssertions, `WebApplicationFactory` |
| Infrastructure | Docker, Docker Compose, GitHub Actions |

## Architecture

```
src/
  Domain/            Entities, retry policy, signature. No framework dependencies.
  Application/       Use cases, repository contracts, the delivery processor
  Infrastructure/    EF Core, repositories, the HTTP sender
  Api/               Minimal API endpoints
  Worker/            Polls for due deliveries and processes them in batches
tests/
  UnitTests/         76 tests: signing, retry policy, delivery outcomes
  IntegrationTests/  20 tests: the API end to end with a fake receiver
```

The retry policy and the signature scheme live in `Domain` because they are the product, not infrastructure. The
`IWebhookSender` abstraction is what lets the entire delivery pipeline be tested without a network.

## How to run

```bash
git clone https://github.com/lucas-goncalves-cav/webhook-delivery-service.git
cd webhook-delivery-service

cp .env.example .env
# replace every your_password_here

docker compose up -d
```

This starts SQL Server, the API, the worker, and a demo receiver standing in for a customer's server.

| Service | URL |
| --- | --- |
| API | http://localhost:8080 |
| Swagger | http://localhost:8080/swagger |
| Demo receiver | http://localhost:9090 |

### Try it

```bash
./scripts/smoke-test.sh
```

That registers an endpoint, publishes an event, waits for the worker, and asserts the delivery was recorded. It is the
same script the CI runs.

Manually:

```bash
# 1. Register an endpoint. Keep the secret, it is shown only once.
curl -X POST http://localhost:8080/api/endpoints \
  -H 'Content-Type: application/json' \
  -d '{
    "name": "My service",
    "url": "http://receiver:8080",
    "subscribedEvents": ["order.*"]
  }'

# 2. Publish an event
curl -X POST http://localhost:8080/api/events \
  -H 'Content-Type: application/json' \
  -H 'Idempotency-Key: order-1234-created' \
  -d '{
    "eventType": "order.created",
    "data": { "orderId": 1234, "total": 199.90 }
  }'

# 3. Watch it arrive
docker compose logs -f receiver

# 4. Inspect the delivery
curl "http://localhost:8080/api/deliveries?endpointId=<endpoint-id>"
```

### Tests

```bash
dotnet test
```

96 tests, no database and no network required.

## Main endpoints

### Endpoints

| Method | Route | Description |
| --- | --- | --- |
| `POST` | `/api/endpoints` | Registers a destination, returns the signing secret |
| `GET` | `/api/endpoints` | Lists endpoints |
| `GET` | `/api/endpoints/{id}` | Gets one endpoint |
| `PUT` | `/api/endpoints/{id}` | Updates name, URL or subscriptions |
| `POST` | `/api/endpoints/{id}/activate` | Re enables and resets the failure counter |
| `POST` | `/api/endpoints/{id}/deactivate` | Stops delivering |
| `POST` | `/api/endpoints/{id}/rotate-secret` | Issues a new signing secret |

### Events

| Method | Route | Description |
| --- | --- | --- |
| `POST` | `/api/events` | Publishes an event and fans it out |

Send an `Idempotency-Key` header to make publishing safe to retry. A repeat returns the original event with
`"wasDuplicate": true` and creates no new deliveries.

### Deliveries

| Method | Route | Description |
| --- | --- | --- |
| `GET` | `/api/deliveries` | Searches history, filter by `status=Failed` for the dead letter queue |
| `GET` | `/api/deliveries/{id}` | One delivery with every attempt |
| `POST` | `/api/deliveries/{id}/replay` | Requeues a failed or cancelled delivery |

### Health

| Method | Route | Description |
| --- | --- | --- |
| `GET` | `/health/live` | Liveness, no dependencies |
| `GET` | `/health/ready` | Readiness, includes SQL Server |

## Verifying a signature

This is the code a customer writes, so it is part of the product rather than an internal detail.

**C#**

```csharp
public static bool Verify(string payload, string secret, string header)
{
    var parts = header.Split(',');
    var timestamp = long.Parse(parts[0].Split('=')[1]);
    var provided = parts[1].Split('=')[1];

    var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(timestamp);

    if (Math.Abs(age.TotalMinutes) > 5)
    {
        return false;   // outside the tolerance window
    }

    var expected = Convert.ToHexString(
        HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes($"{timestamp}.{payload}"))).ToLowerInvariant();

    return CryptographicOperations.FixedTimeEquals(
        Encoding.UTF8.GetBytes(expected),
        Encoding.UTF8.GetBytes(provided));
}
```

**Node.js**

```javascript
const crypto = require('crypto');

function verify(payload, secret, header) {
  const parts = Object.fromEntries(header.split(',').map(p => p.split('=')));
  const age = Math.abs(Date.now() / 1000 - Number(parts.t));

  if (age > 300) return false;

  const expected = crypto
    .createHmac('sha256', secret)
    .update(`${parts.t}.${payload}`)
    .digest('hex');

  return crypto.timingSafeEqual(Buffer.from(expected), Buffer.from(parts.v1));
}
```

Verify against the **raw request body**, before any JSON parsing and re serialization. Reformatting the payload
changes the bytes and invalidates the signature.

### Headers sent with every delivery

| Header | Content |
| --- | --- |
| `X-Webhook-Signature` | `t={unix},v1={hex}` |
| `X-Webhook-Timestamp` | Unix seconds, also inside the signature |
| `X-Webhook-Event-Id` | The event, stable across retries and endpoints |
| `X-Webhook-Event-Type` | For example `order.created` |
| `X-Webhook-Delivery-Id` | This endpoint's delivery of that event |
| `X-Webhook-Attempt` | 1 based attempt number |

**Deliveries are at least once.** A customer's endpoint can receive the same event twice if it responds 200 after the
sender has already timed out. Receivers should deduplicate on `X-Webhook-Event-Id`.

## Configuration

| Variable | Description | Default |
| --- | --- | --- |
| `MSSQL_SA_PASSWORD` | SQL Server password | `your_password_here` |
| `API_PORT` | Host port for the API | `8080` |
| `RECEIVER_PORT` | Host port for the demo receiver | `9090` |
| `DELIVERY_BATCH_SIZE` | Deliveries claimed per poll | `50` |
| `DISABLE_ENDPOINT_AFTER_FAILURES` | Consecutive failures before auto disabling | `20` |

Application settings:

| Setting | Description |
| --- | --- |
| `Delivery:BatchSize` | How many deliveries a worker claims per poll |
| `Delivery:Sender:Timeout` | Per request timeout |
| `Worker:IdleDelay` | Poll interval when the queue is empty |
| `Worker:BusyDelay` | Poll interval after a full batch |

No real credentials are stored in this repository.

## Retry schedule

| Attempt | Delay before it | Elapsed |
| --- | --- | --- |
| 1 | immediate | 0 |
| 2 | ~1 minute | ~1m |
| 3 | ~5 minutes | ~6m |
| 4 | ~15 minutes | ~21m |
| 5 | ~1 hour | ~1h21m |
| 6 | ~6 hours | ~7h21m |
| dead letter | | |

Roughly seven hours to recover before a delivery lands in the dead letter queue, where it waits to be replayed.

## What this does not do

Being explicit, because these are the things a production version would need next:

- **No authentication on the API.** Every route is open. Real deployments need API keys or OAuth, plus multi tenancy so
  one customer cannot read another's deliveries.
- **Polling, not queueing.** The worker polls a table. That is simple, survives restarts and is easy to reason about,
  but a broker would scale further and remove the poll interval from delivery latency.
- **No outbound IP allowlist or SSRF filtering beyond disabling redirects.** A production platform blocks private
  address ranges so customers cannot point endpoints at internal services.
- **No rate limiting per endpoint.** A customer with thousands of pending deliveries can monopolise workers.
- **No payload archival.** Delivery history grows forever, and would need partitioning or a retention policy.

## Roadmap

- [ ] API key authentication and multi tenancy
- [ ] RabbitMQ as an alternative to table polling
- [ ] Per endpoint rate limiting and circuit breaking
- [ ] Bulk replay: requeue every failed delivery for an endpoint in one call
- [ ] SSRF protection through a private address blocklist
- [ ] Retention policy for delivery history

## License

Distributed under the MIT License. See [LICENSE](LICENSE) for details.
