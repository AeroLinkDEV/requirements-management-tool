import assert from "node:assert/strict";
import { access, readFile } from "node:fs/promises";
import test from "node:test";

async function render() {
  const workerUrl = new URL("../dist/server/index.js", import.meta.url);
  workerUrl.searchParams.set("test", `${process.pid}-${Date.now()}`);
  const { default: worker } = await import(workerUrl.href);

  return worker.fetch(
    new Request("http://localhost/", { headers: { accept: "text/html" } }),
    { ASSETS: { fetch: async () => new Response("Not found", { status: 404 }) } },
    { waitUntil() {}, passThroughOnException() {} },
  );
}

test("server-renders the AeroLink showcase shell", async () => {
  const response = await render();
  assert.equal(response.status, 200);
  assert.match(response.headers.get("content-type") ?? "", /^text\/html\b/i);

  const html = await response.text();
  assert.match(html, /<title>AeroLink · FMS 3\.3 Showcase<\/title>/i);
  assert.match(html, /AEROLINK/);
  assert.match(html, /Release Command Center/);
  assert.match(html, /SCR-0001049\.01/);
  assert.match(html, /SCR-0001050\.01/);
  assert.match(html, /Fictional data/);
  assert.match(html, /Manager/);
  assert.match(html, /System Engineer/);
  assert.doesNotMatch(html, /codex-preview|Your site is taking shape|react-loading-skeleton/i);
});

test("keeps the prototype local, interactive and free of starter artifacts", async () => {
  const [page, layout, packageJson] = await Promise.all([
    readFile(new URL("../app/page.tsx", import.meta.url), "utf8"),
    readFile(new URL("../app/layout.tsx", import.meta.url), "utf8"),
    readFile(new URL("../package.json", import.meta.url), "utf8"),
  ]);

  assert.match(page, /"use client"/);
  assert.match(page, /setRole/);
  assert.match(page, /setView/);
  assert.match(page, /setApprovalStep/);
  assert.match(page, /setImpactResolved/);
  assert.match(page, /setCoverageResolved/);
  assert.match(page, /setTestRetested/);
  assert.match(page, /Wrong completed approver/);
  assert.match(layout, /AeroLink · FMS 3\.3 Showcase/);
  assert.match(layout, /\/og\.png/);
  assert.doesNotMatch(packageJson, /react-loading-skeleton|site-creator-vinext-starter/);
  await access(new URL("../public/og.png", import.meta.url));
  await assert.rejects(access(new URL("../app/_sites-preview", import.meta.url)));
});
