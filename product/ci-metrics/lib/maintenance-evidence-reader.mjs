import { MAINTENANCE_REPOSITORY, MAINTENANCE_RULESET_ID } from './maintenance-preflight.mjs'

export const MAINTENANCE_RULESET_PATH = '/repos/' + MAINTENANCE_REPOSITORY + '/rulesets/' + MAINTENANCE_RULESET_ID
const INSTALLATION_REPOSITORIES_PATH = '/installation/repositories?per_page=100'
const API_VERSION = '2022-11-28'
const positive = value => Number.isSafeInteger(value) && value > 0

function requireConfiguredId(value, label) {
  if (!positive(value)) throw new Error(label + ' is not configured.')
  return value
}

function requireActionBinding({ expectedAppSlug, expectedInstallationId, actionAppSlug, actionInstallationId }) {
  if (typeof actionAppSlug !== 'string' || actionAppSlug !== expectedAppSlug ||
      !positive(actionInstallationId) || actionInstallationId !== expectedInstallationId) {
    throw new Error('Maintenance evidence token action identity does not match the pinned App installation.')
  }
}

function requireToken(token) {
  if (typeof token !== 'string' || token.length === 0) throw new Error('Maintenance evidence token is missing.')
  return token
}

function requireApiUrl(apiUrl) {
  if (apiUrl !== 'https://api.github.com') throw new Error('Maintenance evidence API origin is invalid.')
  return apiUrl
}

async function fixedGet({ token, path, apiUrl, fetchImpl }) {
  try {
    const response = await fetchImpl(apiUrl + path, {
      method: 'GET',
      redirect: 'error',
      headers: {
        Authorization: 'Bearer ' + token,
        Accept: 'application/vnd.github+json',
        'X-GitHub-Api-Version': API_VERSION,
      },
    })
    if (response.redirected) throw new Error('redirect')
    if (!response.ok) throw new Error('status:' + response.status)
    return response.status === 204 ? null : await response.json()
  } catch (error) {
    const message = String(error?.message ?? '')
    if (message.startsWith('status:')) throw new Error('Maintenance evidence GET ' + path + ' returned HTTP ' + message.slice(7) + '.')
    throw new Error('Maintenance evidence GET ' + path + ' failed closed.')
  }
}

function validateRepositorySelection({ repositories, expectedAppId, expectedInstallationId, expectedAppSlug }) {
  if (!repositories || typeof repositories !== 'object' || Array.isArray(repositories) ||
      repositories.total_count !== 1 || !Array.isArray(repositories.repositories) ||
      repositories.repositories.length !== 1 || repositories.repositories[0]?.full_name !== MAINTENANCE_REPOSITORY) {
    throw new Error('Maintenance evidence installation is not selected for exactly the target repository.')
  }
  return Object.freeze({
    appId: expectedAppId,
    installationId: expectedInstallationId,
    appSlug: expectedAppSlug,
    repository: MAINTENANCE_REPOSITORY,
    repositorySelection: 'selected',
    identitySource: 'pinned-action-and-owner-jwt-audit',
    // The installation token cannot introspect its own App ID or permission grant.
    // These values are bound by the protected create-github-app-token configuration
    // and independently verified by the owner/JWT setup audit.
    permissions: Object.freeze({ administration: 'write', metadata: 'read' }),
  })
}

/**
 * Return the only privileged operation exposed by this module: one authenticated GET of the
 * exact ruleset endpoint after fresh repository-scope verification. There is no caller-supplied
 * path, method, body, GraphQL, write, redirect-following or generic request API. Installation
 * identity, App permissions, and selected-installation status are bound by the protected
 * create-github-app-token configuration and a separate owner/JWT setup audit: an installation
 * token cannot call the JWT-only installation-detail endpoint to prove those facts itself.
 */
export function createMaintenanceRulesetReader({
  token,
  expectedAppId,
  expectedInstallationId,
  expectedAppSlug = 'aerolink-maintenance-evidence',
  actionAppSlug,
  actionInstallationId,
  apiUrl = 'https://api.github.com',
  fetchImpl = fetch,
} = {}) {
  const appId = requireConfiguredId(expectedAppId, 'Maintenance evidence App ID')
  const installationId = requireConfiguredId(expectedInstallationId, 'Maintenance evidence installation ID')
  if (typeof expectedAppSlug !== 'string' || !/^[a-z0-9-]+$/.test(expectedAppSlug)) {
    throw new Error('Maintenance evidence App slug is not configured.')
  }
  requireActionBinding({ expectedAppSlug, expectedInstallationId: installationId, actionAppSlug, actionInstallationId })
  const evidenceToken = requireToken(token)
  const origin = requireApiUrl(apiUrl)
  return async function readRuleset() {
    const repositories = await fixedGet({ token: evidenceToken, path: INSTALLATION_REPOSITORIES_PATH, apiUrl: origin, fetchImpl })
    const identity = validateRepositorySelection({ repositories, expectedAppId: appId, expectedInstallationId: installationId, expectedAppSlug })
    const ruleset = await fixedGet({ token: evidenceToken, path: MAINTENANCE_RULESET_PATH, apiUrl: origin, fetchImpl })
    if (!ruleset || typeof ruleset !== 'object' || Array.isArray(ruleset) || ruleset.id !== MAINTENANCE_RULESET_ID) {
      throw new Error('Maintenance evidence ruleset identity is unverified.')
    }
    return { ruleset, identity }
  }
}

/** Route the exact ruleset read to the privileged reader; every other collector read stays on GITHUB_TOKEN. */
export function routeMaintenanceRead({ read, rulesetReader } = {}) {
  if (typeof read !== 'function' || typeof rulesetReader !== 'function') throw new Error('Maintenance read routing requires both ordinary and privileged readers.')
  return async (path, options = {}) => {
    if (path === MAINTENANCE_RULESET_PATH) {
      const method = options?.method ?? 'GET'
      if (method !== 'GET' || Object.prototype.hasOwnProperty.call(options, 'body')) {
        throw new Error('The privileged maintenance reader permits only the exact ruleset GET.')
      }
      const result = await rulesetReader()
      return result.ruleset
    }
    return read(path, options)
  }
}
