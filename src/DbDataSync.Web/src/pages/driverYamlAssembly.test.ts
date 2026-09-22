import { describe, expect, it } from 'vitest'
import { assembleDriverYaml, parseDriverYaml, RAW_BODY_SKELETON } from './driverYamlAssembly'

describe('assembleDriverYaml / parseDriverYaml', () => {
  it('round-trips an ADO.NET-based driver', () => {
    const form = {
      id: 'mysql.generic', displayName: 'MySQL / MariaDB', base: 'adonet' as const,
      library: 'mysql-connector', driverClass: '', driverJarPaths: [],
      readers: ['Watermark', 'BatchReload'], staging: ['StagingTable'], writers: ['DeleteInsert'],
      rawBody: RAW_BODY_SKELETON,
    }

    const yaml = assembleDriverYaml(form)
    const parsed = parseDriverYaml(yaml)

    expect(parsed.id).toBe('mysql.generic')
    expect(parsed.displayName).toBe('MySQL / MariaDB')
    expect(parsed.base).toBe('adonet')
    expect(parsed.library).toBe('mysql-connector')
    expect(parsed.readers).toEqual(['Watermark', 'BatchReload'])
    expect(parsed.staging).toEqual(['StagingTable'])
    expect(parsed.writers).toEqual(['DeleteInsert'])
    expect(parsed.rawBody).toBe(RAW_BODY_SKELETON)
  })

  it('round-trips a JDBC-based driver, including multiple jars', () => {
    const form = {
      id: 'postgres-via-jdbc', displayName: 'Postgres (via JDBC)', base: 'jdbc' as const,
      library: '', driverClass: 'org.postgresql.Driver', driverJarPaths: ['postgresql-42.7.13.jar'],
      readers: ['Watermark'], staging: [], writers: [],
      rawBody: RAW_BODY_SKELETON,
    }

    const yaml = assembleDriverYaml(form)
    const parsed = parseDriverYaml(yaml)

    expect(parsed.base).toBe('jdbc')
    expect(parsed.library).toBe('ikvm')
    expect(parsed.driverClass).toBe('org.postgresql.Driver')
    expect(parsed.driverJarPaths).toEqual(['postgresql-42.7.13.jar'])
  })

  it('parses multiple jars back out correctly', () => {
    const form = {
      id: 'oracle-via-jdbc', displayName: 'Oracle (via JDBC)', base: 'jdbc' as const,
      library: '', driverClass: 'oracle.jdbc.OracleDriver',
      driverJarPaths: ['ojdbc8.jar', 'oraclepki.jar', 'osdt_cert.jar', 'osdt_core.jar'],
      readers: [], staging: [], writers: [],
      rawBody: RAW_BODY_SKELETON,
    }

    const parsed = parseDriverYaml(assembleDriverYaml(form))

    expect(parsed.driverJarPaths).toEqual(['ojdbc8.jar', 'oraclepki.jar', 'osdt_cert.jar', 'osdt_core.jar'])
  })

  it('keeps the raw body distinct from the structured capabilities block it sits beside', () => {
    // The real risk this splitter has to get right: dialect/typeMap live between the jdbc block and
    // capabilities in the assembled document, and must not accidentally swallow (or be swallowed by)
    // either neighbor.
    const yaml = assembleDriverYaml({
      id: 'x', displayName: 'X', base: 'adonet', library: 'lib', driverClass: '', driverJarPaths: [],
      readers: ['Watermark'], staging: [], writers: [],
      rawBody: 'dialect:\n  quoteIdentifier: backtick\ntypeMap:\n  int: Int32',
    })

    const parsed = parseDriverYaml(yaml)

    expect(parsed.rawBody).toBe('dialect:\n  quoteIdentifier: backtick\ntypeMap:\n  int: Int32')
    expect(parsed.readers).toEqual(['Watermark'])
  })
})
