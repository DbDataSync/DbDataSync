# DataSync


## Goal: data replication between different databases

We are building an open source, cross platform, cross database engine, data replication tool.

The initial plan is to support MSSQL to MSSQL, using native MSSQL CDC or change tracking features to find changed data.

## The process

Every configured replication will have source databases, source tables, target databases, and target tables.

There may also need to be configuration for staging captured changes before applying them to the target.

The application will need to track state for what data has been extracted, and what data has been applied.

### Source: Change tracking and extracting source database changes

Each database engine that can be used as a source will potentially need to support multiple methods of change tracking, each will usually optimized or designed solely for a particular database engines.

### Staging: Storing a change set before applying to a target

There may need to be multiple methods of storing changes before they are applied.  We could have a generic storage method as parquet files, or something similar, but we may also want a target specific method of staging data in temp tables/generic tables.

### Target: Applying changes to a target

Each target may have special methods for applying changes (SQL server bulk inserts), but generating properly ordered insert/update/delete statements should also work.


## Prior Art

* https://github.com/osalvador/ReplicaDB
* https://github.com/mganss/SyncChanges
* https://debezium.io/