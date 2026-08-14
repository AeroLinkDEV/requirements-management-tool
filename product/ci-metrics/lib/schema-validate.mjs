// A small recursive validator driven by the checked-in JSON Schema.
//
// It implements the subset of JSON Schema the fragment schema uses (type, enum, const, required, properties,
// additionalProperties, items, min/max, maxLength, maxItems, pattern). Using the actual schema file keeps the
// validation and the documented contract from drifting apart.

import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { dirname, join } from 'node:path'

const schema = JSON.parse(readFileSync(join(dirname(fileURLToPath(import.meta.url)), '..', 'schema', 'v1-fragment.json'), 'utf8'))

function typeMatches(value, type) {
  switch (type) {
    case 'object': return value !== null && typeof value === 'object' && !Array.isArray(value)
    case 'array': return Array.isArray(value)
    case 'string': return typeof value === 'string'
    case 'integer': return Number.isInteger(value)
    case 'number': return typeof value === 'number'
    case 'boolean': return typeof value === 'boolean'
    case 'null': return value === null
    default: return true
  }
}

export function validateAgainstSchema(value, node = schema, path = '$') {
  const errors = []
  if (node.type !== undefined) {
    const types = Array.isArray(node.type) ? node.type : [node.type]
    if (!types.some((type) => typeMatches(value, type))) {
      errors.push(`${path}: expected type ${types.join(' or ')}.`)
      return errors
    }
  }
  if (node.const !== undefined && value !== node.const) {
    errors.push(`${path}: expected constant ${JSON.stringify(node.const)}.`)
  }
  if (node.enum !== undefined && !node.enum.some((entry) => JSON.stringify(entry) === JSON.stringify(value))) {
    errors.push(`${path}: value is not one of the allowed enum entries.`)
  }
  if (typeof value === 'string') {
    if (node.maxLength !== undefined && value.length > node.maxLength) errors.push(`${path}: exceeds maxLength ${node.maxLength}.`)
    if (node.pattern !== undefined && !new RegExp(node.pattern).test(value)) errors.push(`${path}: does not match required pattern.`)
  }
  if (typeof value === 'number') {
    if (node.minimum !== undefined && value < node.minimum) errors.push(`${path}: below minimum ${node.minimum}.`)
  }
  if (Array.isArray(value)) {
    if (node.maxItems !== undefined && value.length > node.maxItems) errors.push(`${path}: exceeds maxItems ${node.maxItems}.`)
    if (node.items !== undefined) {
      value.forEach((entry, index) => errors.push(...validateAgainstSchema(entry, node.items, `${path}[${index}]`)))
    }
  }
  if (value !== null && typeof value === 'object' && !Array.isArray(value)) {
    if (node.required !== undefined) {
      for (const key of node.required) {
        if (!(key in value)) errors.push(`${path}: missing required property "${key}".`)
      }
    }
    if (node.properties !== undefined) {
      for (const [key, childSchema] of Object.entries(node.properties)) {
        if (key in value) errors.push(...validateAgainstSchema(value[key], childSchema, `${path}.${key}`))
      }
    }
    if (node.additionalProperties !== undefined) {
      const known = new Set(Object.keys(node.properties ?? {}))
      for (const key of Object.keys(value)) {
        if (known.has(key)) continue
        if (node.additionalProperties === false) {
          errors.push(`${path}: unexpected property "${key}".`)
        } else if (typeof node.additionalProperties === 'object') {
          errors.push(...validateAgainstSchema(value[key], node.additionalProperties, `${path}.${key}`))
        }
      }
    }
  }
  return errors
}

export { schema }
