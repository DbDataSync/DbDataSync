# During a run of `dev-harness workload --rate 1500 --duration 2m` several task runs reported errors

The errors indicated that it was not able to set certain target columns to NULL.

My theory is that the change tracking captured inserts or updates followed by deletes for the same primary key
during a single task run. This would cause the join from the change tracking to the actual row to return
null, which would cause the process to fail for an insert/update operation that requires non-null columns.

This theory may be incorrect, because I haven't found an explanation for how it would recover in that
scenario, and the `scripts/dev-harness verify` command reported that all rows were identical.