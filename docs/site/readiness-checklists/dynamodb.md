# Before you migrate DynamoDB {#before-you-migrate-dynamodb}

[← Workload compatibility](../workload-compatibility.md#dynamodb) · [Design gaps](../design-gaps.md#dynamodb)

Answer each question with **yes** or **no**.
If you answer **yes**, read the linked design gap and confirm its workaround
fits your workload before migrating.

1. **Do you require the Cosmos 99.999% read/write SLA without pre-provisioning the matching account topology?** → [99.999% availability depends on Cosmos account topology](../design-gaps/dynamodb/99999-availability-depends-on-cosmos-account-topology.md)
2. **Does your workload require DynamoDB Streams, DAX, global tables, PITR/backups, or auto-scaling control-plane features?** → [Absent DynamoDB features](../design-gaps/dynamodb/absent-dynamodb-features.md)
3. **Do you expect read-your-writes across separate proxy calls on Session or Eventual Cosmos consistency?** → [Consistency and read-your-writes](../design-gaps/dynamodb/consistency-and-read-your-writes.md)
4. **Do your clients assume LastEvaluatedKey stays small or inspect its internal structure?** → [Cross-partition continuation tokens can be large](../design-gaps/dynamodb/cross-partition-continuation-tokens-can-be-large.md)
5. **Do you need DynamoDB keys longer than the documented limit or direct portability to raw Cosmos storage?** → [Key encoding and on-disk storage format](../design-gaps/dynamodb/key-encoding-and-on-disk-storage-format.md)
6. **Do you require active/active non-idempotent writes without Last-Write-Wins conflict risk?** → [Multi-write conflict resolution is Last-Write-Wins](../design-gaps/dynamodb/multi-write-conflict-resolution-is-last-write-wins.md)
7. **Do you need DynamoDB index semantics such as native GSIs/LSIs, UTF-8 byte-order collation, or portable index metrics?** → [Secondary indexes (GSI / LSI)](../design-gaps/dynamodb/secondary-indexes--gsi---lsi.md)
8. **Do you depend on DynamoDB RCU/WCU behavior rather than Azure Cosmos RU/s throttling?** → [Throughput and throttling model](../design-gaps/dynamodb/throughput-and-throttling-model.md)
9. **Do your transactions need to stay available after the preferred writable Cosmos region is down without reconfiguration?** → [Transaction execution has one configured Cosmos authority](../design-gaps/dynamodb/transaction-execution-has-one-configured-cosmos-authority.md)
10. **Do you need ACID transactions that span multiple tables or partition-key values?** → [Transaction scope is single-partition, single-table](../design-gaps/dynamodb/transaction-scope-is-single-partition-single-table.md)
