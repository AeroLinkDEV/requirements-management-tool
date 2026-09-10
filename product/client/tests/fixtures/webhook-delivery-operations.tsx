import { createRoot } from "react-dom/client";
import IntegrationCommandCenter from "../../src/IntegrationCommandCenter";
import "../../src/index.css";
import "../../src/IntegrationCommandCenter.css";

const projectId = "00000000-0000-0000-0000-000000000001";
const releaseId = "00000000-0000-0000-0000-000000000002";
const oldExpiredId = "00000000-0000-0000-0000-000000000101";
const liveClaimId = "00000000-0000-0000-0000-000000000102";
const deadLetterId = "00000000-0000-0000-0000-000000000103";
const now = new Date();
const minutesFromNow = (minutes: number) => new Date(now.getTime() + minutes * 60_000).toISOString();

const state = {
  generatedAt: now.toISOString(),
  api: { version: "v1", basePath: "/api/v1", status: "Operational", capabilities: [] },
  metrics: {
    activeIdentities: 0,
    enabledWebhooks: 1,
    events24h: 3,
    deliverySuccess: 50,
    deadLetters: 1,
    expiredClaims: 1,
  },
  identities: [],
  webhooks: [{
    id: "00000000-0000-0000-0000-000000000201",
    name: "Supplier gateway",
    endpointUrl: "https://supplier.example.test/aerolink",
    eventTypes: ["aerolink.integration.test"],
    isEnabled: true,
    createdAt: now.toISOString(),
    updatedAt: now.toISOString(),
  }],
  events: [
    { id: "00000000-0000-0000-0000-000000000301", eventType: "Expired requirement event", aggregateType: "Requirement", actor: "test.setup", occurredAt: now.toISOString(), state: "Pending", deliveryCount: 1 },
    { id: "00000000-0000-0000-0000-000000000302", eventType: "Live requirement event", aggregateType: "Requirement", actor: "test.setup", occurredAt: now.toISOString(), state: "Pending", deliveryCount: 1 },
    { id: "00000000-0000-0000-0000-000000000303", eventType: "Dead-letter requirement event", aggregateType: "Requirement", actor: "test.setup", occurredAt: now.toISOString(), state: "Pending", deliveryCount: 1 },
  ],
  deliveries: [
    {
      id: oldExpiredId,
      integrationEventId: "00000000-0000-0000-0000-000000000301",
      subscriptionId: "00000000-0000-0000-0000-000000000201",
      state: "Delivering",
      attemptCount: 1,
      nextAttemptAt: now.toISOString(),
      createdAt: now.toISOString(),
      claimedBy: "stale-worker",
      claimedAt: new Date(now.getTime() - 180_000).toISOString(),
      claimExpiresAt: new Date(now.getTime() - 60_000).toISOString(),
      needsAttention: true,
      attemptHistory: [{ attempt: 1, outcome: "Delivering", error: undefined }],
    },
    {
      id: liveClaimId,
      integrationEventId: "00000000-0000-0000-0000-000000000302",
      subscriptionId: "00000000-0000-0000-0000-000000000201",
      state: "Delivering",
      attemptCount: 1,
      nextAttemptAt: now.toISOString(),
      createdAt: now.toISOString(),
      claimedBy: "live-worker",
      claimedAt: now.toISOString(),
      claimExpiresAt: minutesFromNow(20),
      needsAttention: false,
      attemptHistory: [{ attempt: 1, outcome: "Delivering", error: undefined }],
    },
    {
      id: deadLetterId,
      integrationEventId: "00000000-0000-0000-0000-000000000303",
      subscriptionId: "00000000-0000-0000-0000-000000000201",
      state: "DeadLettered",
      attemptCount: 5,
      nextAttemptAt: now.toISOString(),
      createdAt: now.toISOString(),
      lastError: "Receiver unavailable",
      needsAttention: false,
      attemptHistory: [1, 2, 3, 4, 5].map(attempt => ({ attempt, outcome: "DeadLettered", error: "Receiver unavailable" })),
    },
  ],
  interchange: [],
};

const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), {
  status,
  headers: { "Content-Type": "application/json" },
});

window.fetch = async (input, init) => {
  const url = typeof input === "string" ? input : input instanceof URL ? input.toString() : input.url;
  const path = new URL(url, location.href).pathname;
  if (path === "/api/integrations/overview") return json(state);
  if (path === "/api/jira/connection") return json({ configured: false });
  if (path === "/api/jira/links") return json([]);
  if (path === "/api/reqif/overview") {
    return json({
      standard: "ReqIF 1.2",
      profile: "AeroLink lossless",
      coverage: [],
      metrics: { exchanges: 0, exports: 0, imports: 0, ready: 0, attention: 0 },
      jobs: [],
    });
  }
  if (path === `/api/integrations/deliveries/${oldExpiredId}/replay` && init?.method === "POST") {
    const item = state.deliveries.find(delivery => delivery.id === oldExpiredId);
    if (item) {
      item.state = "Pending";
      item.needsAttention = false;
      item.claimedBy = undefined;
      item.claimExpiresAt = undefined;
      item.attemptCount = 0;
    }
    return new Response(null, { status: 202 });
  }
  return json({}, 404);
};

createRoot(document.getElementById("root")!).render(
  <IntegrationCommandCenter api="" projectId={projectId} releaseId={releaseId} onBack={() => undefined} />,
);
