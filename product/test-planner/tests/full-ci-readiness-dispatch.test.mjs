import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'

const read = (relative) => readFileSync(new URL(`../../../${relative}`, import.meta.url), 'utf8')
const requester = read('.github/workflows/request-full-ci.yml')
const full = read('.github/workflows/ci.yml')

test('ready label requester is trusted-base, exact-label, same-repository, and dispatch-only', () => {
  assert.match(requester, /pull_request_target:/)
  assert.match(requester, /types: \[labeled\]/)
  assert.match(requester, /actions: write/)
  assert.match(requester, /contents: read/)
  assert.doesNotMatch(requester, /actions\/checkout|git checkout|git clone/)
  assert.match(requester, /github\.event\.label\.name == 'ready-for-full-ci'/)
  assert.match(requester, /github\.event\.pull_request\.head\.repo\.full_name == github\.repository/)
  assert.match(requester, /actions\/workflows\/ci\.yml\/dispatches/)
})

test('full workflow authenticates exact ready PR state before trusting dispatch inputs', () => {
  for (const input of ['pull_request_number', 'pull_request_base_sha', 'pull_request_head_sha']) {
    assert.match(full, new RegExp(`${input}:`))
  }
  assert.match(full, /pull-requests: read/)
  assert.match(full, /      - name: Authenticate label-dispatched pull-request context\n        if:/)
  assert.match(full, /        env:\n          GITHUB_TOKEN:/)
  assert.match(full, /REQUESTED_HEAD_SHA/)
  assert.match(full, /REQUESTED_BASE_SHA/)
  assert.match(full, /ready-for-full-ci is no longer present/)
})

test('label-dispatched Full reuses PR classification and exact indentation', () => {
  assert.match(full, /inputs\.pull_request_number \|\| github\.event\.pull_request\.number \|\| github\.ref/)
  assert.match(full, /        env:\n          EVENT_NAME: .*inputs\.pull_request_number/)
  assert.match(full, /          BASE_SHA: .*inputs\.pull_request_base_sha/)
  assert.match(full, /          HEAD_SHA: .*inputs\.pull_request_head_sha/)
})
