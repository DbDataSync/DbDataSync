import { describe, expect, it } from 'vitest'
import { assembleDriverYaml, parseDriverYaml, roundTripsCleanly, RAW_BODY_SKELETON } from './driverYamlAssembly'

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

  it('round-trips a structured urlTemplate/connectionStringKeys (phase 179N)', () => {
    const form = {
      id: 'postgres-via-jdbc', displayName: 'Postgres (via JDBC)', base: 'jdbc' as const,
      library: '', driverClass: 'org.postgresql.Driver', driverJarPaths: ['postgresql-42.7.13.jar'],
      readers: ['Watermark'], staging: [], writers: [],
      rawBody: RAW_BODY_SKELETON,
      urlTemplate: 'jdbc:postgresql://{host}:{port}/{database}',
      connectionStringKeys: { host: '', port: '', database: '', username: 'pguser', password: '', connectTimeout: '' },
    }

    const parsed = parseDriverYaml(assembleDriverYaml(form))

    expect(parsed.urlTemplate).toBe('jdbc:postgresql://{host}:{port}/{database}')
    expect(parsed.connectionStringKeys.username).toBe('pguser')
    // A blank field never becomes a real, different value ("" is not "unset").
    expect(parsed.connectionStringKeys.host).toBe('')
  })

  it('omits urlTemplate/connectionStringKeys entirely when unset, not as blank lines', () => {
    const form = {
      id: 'x', displayName: 'X', base: 'jdbc' as const,
      library: '', driverClass: 'org.postgresql.Driver', driverJarPaths: ['a.jar'],
      readers: [], staging: [], writers: [],
      rawBody: RAW_BODY_SKELETON,
      urlTemplate: '',
      connectionStringKeys: { host: '', port: '', database: '', username: '', password: '', connectTimeout: '' },
    }

    const yaml = assembleDriverYaml(form)

    expect(yaml).not.toContain('urlTemplate')
    expect(yaml).not.toContain('connectionStringKeys')
  })

  it('a structured connectionStringKeys value and a hand-authored one coexist under a single header', () => {
    // The real risk this design has to get right: username (structured) and integratedSecurity (no
    // field for it) both set at once must merge into ONE connectionStringKeys: block, not two — two
    // would be a duplicate YAML key.
    const loaded = parseDriverYaml([
      'id: x', 'displayName: X', 'library: ikvm',
      'base: DbDataSync.Drivers.Jdbc.JdbcGenericDriver, DbDataSync.Drivers.Jdbc',
      'jdbc:',
      '  driverClass: org.postgresql.Driver',
      '  driverJarPaths: [a.jar]',
      '  urlTemplate: "jdbc:postgresql://{host}:{port}/{database}"',
      '  connectionStringKeys:',
      '    username: pguser',
      '    integratedSecurity: ssl',
      'capabilities:',
      '  readers: []', '  staging: []', '  writers: []',
    ].join('\n'))

    expect(loaded.urlTemplate).toBe('jdbc:postgresql://{host}:{port}/{database}')
    expect(loaded.connectionStringKeys.username).toBe('pguser')
    // No structured field for integratedSecurity — it survives only via connectionStringKeysExtra.
    expect(loaded.connectionStringKeysExtra).toContain('integratedSecurity: ssl')
    expect(loaded.jdbcExtra).not.toContain('integratedSecurity')
    expect(loaded.jdbcExtra).not.toContain('connectionStringKeys')

    const reassembled = assembleDriverYaml(loaded)
    expect(reassembled).toContain('urlTemplate: "jdbc:postgresql://{host}:{port}/{database}"')
    expect(reassembled).toContain('username: pguser')
    expect(reassembled).toContain('integratedSecurity: ssl')
    // Exactly one connectionStringKeys: header even though both sources contributed to it.
    expect(reassembled.match(/connectionStringKeys:/g)?.length).toBe(1)

    // And it still round-trips cleanly a second time.
    const reparsed = parseDriverYaml(reassembled)
    expect(reparsed.connectionStringKeys.username).toBe('pguser')
    expect(reparsed.connectionStringKeysExtra).toContain('integratedSecurity: ssl')
  })

  it('preserves an edit to an unrelated field without disturbing urlTemplate/connectionStringKeys (phase 178N/179N)', () => {
    // Before 178N, editing any other field on this driver and saving would silently strip both —
    // see follow-up-jdbc-url-template-unreachable-from-driver-yaml.md part 2. Now that 179N gives both
    // real structured fields, this proves the same guarantee holds through the structured path, not
    // just jdbcExtra.
    const loaded = parseDriverYaml([
      'id: postgres-via-jdbc',
      'displayName: Postgres (via JDBC)',
      'library: ikvm',
      'base: DbDataSync.Drivers.Jdbc.JdbcGenericDriver, DbDataSync.Drivers.Jdbc',
      'jdbc:',
      '  driverClass: org.postgresql.Driver',
      '  driverJarPaths: [postgresql-42.7.13.jar]',
      '  urlTemplate: "jdbc:postgresql://{host}:{port}/{database}"',
      '  connectionStringKeys:',
      '    username: user',
      'dialect:',
      '  quoteIdentifier: doubleQuote',
      'capabilities:',
      '  readers: [Watermark]',
      '  staging: []',
      '  writers: []',
    ].join('\n'))

    expect(loaded.urlTemplate).toBe('jdbc:postgresql://{host}:{port}/{database}')
    expect(loaded.connectionStringKeys.username).toBe('user')

    // Edit an unrelated field, matching what an operator renaming the display name would do.
    const edited = { ...loaded, displayName: 'Postgres (renamed)' }
    const reparsed = parseDriverYaml(assembleDriverYaml(edited))

    expect(reparsed.displayName).toBe('Postgres (renamed)')
    expect(reparsed.urlTemplate).toBe('jdbc:postgresql://{host}:{port}/{database}')
    expect(reparsed.connectionStringKeys.username).toBe('user')
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

  it('roundTripsCleanly (phase 180N): true for anything this app itself would generate', () => {
    const yaml = assembleDriverYaml({
      id: 'postgres-via-jdbc', displayName: 'Postgres (via JDBC)', base: 'jdbc',
      library: '', driverClass: 'org.postgresql.Driver', driverJarPaths: ['a.jar', 'b.jar'],
      readers: ['Watermark'], staging: [], writers: ['DeleteInsert'],
      rawBody: RAW_BODY_SKELETON,
      urlTemplate: 'jdbc:postgresql://{host}:{port}/{database}',
      connectionStringKeys: { host: '', port: '', database: '', username: 'pguser', password: '', connectTimeout: '' },
    })

    expect(roundTripsCleanly(yaml)).toBe(true)
  })

  it('roundTripsCleanly: false for a hand-authored file the splitter cannot model', () => {
    // A top-level key whose value starts on the same line with unusual (non-generated) spacing —
    // parseDriverYaml's own doc comment names exactly this as outside what it reliably splits.
    const handAuthored = [
      'id:      x   # id and value crammed together with a trailing comment, unlike this app\'s own writer',
      'displayName: X',
      'library: lib',
      'dialect:',
      '  quoteIdentifier: doubleQuote',
      '  parameterPrefix: "@"',
      '  rowLimit: limitOffset',
      'capabilities:',
      '  readers: []',
      '  staging: []',
      '  writers: []',
    ].join('\n')

    expect(roundTripsCleanly(handAuthored)).toBe(false)
  })
})
