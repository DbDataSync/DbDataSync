import type { ParameterDescriptor } from '../api/types'

const SINGLE = { min: 1, max: 1 }

/** How many values a parameter takes, with the unstated case — a single value — filled in. */
export function occurrences(parameter: ParameterDescriptor) {
  return parameter.cardinality ?? SINGLE
}

export function isVararg(parameter: ParameterDescriptor) {
  const { min, max } = occurrences(parameter)
  return min !== 1 || max !== 1
}

/**
 * Values for a vararg live under `<name>.<key>` in the same flat dictionary. The persisted shape is a
 * string dictionary and has to stay one, so that config written before any of this still loads.
 */
export function varargEntries(parameter: ParameterDescriptor, values: Record<string, string>) {
  const prefix = `${parameter.name}.`
  return Object.fromEntries(
    Object.entries(values).filter(([k]) => k.startsWith(prefix)).map(([k, v]) => [k.slice(prefix.length), v]),
  )
}

/** This parameter's values replaced wholesale, everything else left alone. Rebuilt rather than
 * merged so a removed key actually disappears. */
export function withVararg(
  parameter: ParameterDescriptor, values: Record<string, string>, next: Record<string, string>,
): Record<string, string> {
  const prefix = `${parameter.name}.`
  return {
    ...Object.fromEntries(Object.entries(values).filter(([k]) => !k.startsWith(prefix))),
    ...Object.fromEntries(Object.entries(next).map(([k, v]) => [`${prefix}${k}`, v])),
  }
}
