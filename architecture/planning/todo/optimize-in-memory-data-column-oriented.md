# Optimize the in memory layout of changes


We need to consider replacing the sequence of ChangeRow objects with either a column oriented ChangeBatch concept, or even a simple fixed object[] or object[,] instead of the dictionary we are using.

Batches with properly typed arrays containing a single column data will take less memory and are generally faster for certain types of operations, and would be faster to serialize to disk for caching / multi target distribution / offline target scenarios. Because we will mainly be using the batches to iterate over in a row by row fashion, they may not work as well for our in memory representation.

If column store will be better, we should consider using an existing dotnet library
(Apache Arrow / Parquet.NET / Microsoft.Data.Analysis.DataFrame)
or rolling our own depending on our needs and if it offers any improvements.
