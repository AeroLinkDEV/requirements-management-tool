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
GET /api/v1/requirements?projectId=<guid>&pageSize=50&cursor=SYSR-000100
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

## Lifecycle event catalog

Authoritative lifecycle mutations and their integration-event records are committed in the same database save. A subscriber can select individual event types or use `*`:

- `aerolink.change-request.changed`
- `aerolink.baseline.changed`
- `aerolink.requirement.revision-created`
- `aerolink.release-campaign.changed`
- `aerolink.software-build.recorded` and `aerolink.software-build.changed`
- `aerolink.test-execution.recorded`

Each event names the Project and aggregate, identifies the actor, records occurrence time, and enters the signed webhook delivery pipeline without a second non-transactional publication step.

## ReqIF 1.2 exchange

The **ReqIF Exchange Center** exports a `.reqifz` package containing the ReqIF document, an integrity manifest, and controlled attachment binaries. AeroLink's governed round-trip profile preserves stable requirement and revision identifiers, hierarchy, trace relations, statements, rationale, verification methods, the rich-text source, schema attributes, tags, and attachment metadata/hashes.

ReqIF 1.2 reuses the normative `20110401/reqif.xsd` schema, whose `REQ-IF-VERSION` element is fixed to `1.0`; AeroLink therefore identifies the product profile as ReqIF 1.2 while emitting the schema-required XML header value `1.0`.

Inbound `.reqif` and `.reqifz` files pass a preview and reconciliation boundary before they can affect controlled work. The parser prohibits DTDs and external entities, limits package and expanded size, rejects unsafe archive paths, checks duplicate and existing identifiers, and retains the immutable source package. A successful commit creates a Draft change request; it never creates approved requirements or bypasses review and baseline controls.

The round-trip guarantee applies to the documented AeroLink profile. Vendor-specific extensions outside that profile remain preserved in the immutable source package but require an explicit mapping decision before they become controlled AeroLink fields.

## Compatibility

Breaking changes require a new path version. Additive fields may appear within v1, so consumers must ignore unknown JSON properties. Stable machine-readable error codes accompany security and idempotency failures.
