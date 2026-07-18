# Open Digital Thread API

The AeroLink 2.0 connected foundation exposes a deliberately small, governed public contract. It is not a mirror of the internal database.

## Authentication

Create a machine identity in **Administration → Integration Center**, select only the required scopes, and copy the returned `alk_...` key into the calling system's secret manager. Send it as:

```http
Authorization: Bearer alk_<client>.<secret>
```

Keys belong to one Project, are hashed at rest, cannot be recovered, and can be revoked immediately. Current scopes are `requirements:read`, `events:write`, and `integrations:read`.

## Requirement reads

```http
GET /api/v1/requirements?projectId=<guid>&pageSize=50&cursor=SYSR-00000100
Authorization: Bearer <api-key>
```

The response contains `items`, `nextCursor`, and `pageSize`. Follow `nextCursor` until it is `null`. Page size is limited to 200. Requirement collection and single-resource responses include an `ETag` for cache validation and future conditional operations.

## External event ingestion

```http
POST /api/v1/events
Authorization: Bearer <api-key>
Idempotency-Key: supplier-build-2026-07-17-001
Content-Type: application/json

{
  "eventType": "external.build.completed",
  "aggregateType": "Build",
  "aggregateId": "11111111-2222-3333-4444-555555555555",
  "data": { "buildNumber": "FMS-2.0.17" }
}
```

The same Project and `Idempotency-Key` return the original event rather than creating a duplicate. Keys must contain 8–160 characters.

## Webhook verification

For each delivery, AeroLink serializes the envelope once, then computes:

```text
hex(HMAC-SHA256(signing-secret, timestamp + "." + raw-request-body))
```

The result is sent as `X-AeroLink-Signature: v1=<hex>`. Verify the signature using constant-time comparison before parsing the body, reject stale `X-AeroLink-Timestamp` values, and retain `X-AeroLink-Delivery` for support correlation. Failed deliveries use exponential backoff, stop after five attempts, and can be replayed from the Integration Command Center.

## Compatibility

Breaking changes require a new path version. Additive fields may appear within v1, so consumers must ignore unknown JSON properties. Stable machine-readable error codes accompany security and idempotency failures.
