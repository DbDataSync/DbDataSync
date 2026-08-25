
# Concepts

## Drivers & Change Readers/Writers/Caching


* Driver (driver:MSSQL, Oracle, etc...): Each driver can advertise which readers and which writers are supported

* Change Readers: each reader provides a result set of data + any relevant batch or row level change tracking markers
    * Driver Specific
        * MSSQL would support CDC, change tracking, and potentially mssql transactional replication
    * Batch (read from source where x = y)
    * Possibly other generic change reader
* Change Caching
    * Parquet
    * Target staging tables
    * Target specific staging process (eg: SQL bulk insert to a temporary or perisitent staging table)
* Change Writers: each writer can advertise which caching method is supported
    * Merge
    * Ordered Insert/Update/Delete statements
    * Batch (delete from target where x = y, load from source/cache)

* Generic Staging Provider
    * Parquet, others?
* Generic Change Application
    * Insert/Update/Delete

## Replication

* Replication
    * Connections: a connection could be configured as a source, a target, or both
    * Tasks
        * Task Name
        * Table Mapping
            * Mapping Name: based on source or target tables, or both
            * Sources:
                * Source Connection/Database/Table
                    * Source Filters
            * Targets:
                * Target Connection/Database/Table
            * Column Mappings
                * Data / Type transformations
        * Scheduling methods
            * Continuous
                * Run on a loop
                    * frequency settings
            * Periodic
                * Run on a cron schedule
        * Change Processing: configuration for how changes are read, cached, and applied
            * Change Reader
                * parallelism settings for reading source changes
                * any custom reader settings
            * Change Caching
                * Any custom caching options allowed
            * Change Writer
                * parallelism settings for applying target changes
                * any custom writer settings


## Connections & Metadata

* Connection (hostname, auth)
    * Database (name: app_1, app_2, etc...)
        * Table (schema/name, etc...)
            * Column (name, native data type, generic data type)


