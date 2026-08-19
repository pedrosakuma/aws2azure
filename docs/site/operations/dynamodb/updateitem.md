# dynamodb / UpdateItem {#operation-dynamodb-updateitem}

[← dynamodb operation index](../../dynamodb.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:dynamodb:updateitem`
- **Status:** 🟡 partial
- **Disposition:** 🔵 by design
- **Azure equivalent:** `Azure Cosmos DB (Core SQL API)`
- **Real-Azure verified:** ✅ 2026-07-16 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261)

## Sub-features

### UpdateExpression grammar (SET / REMOVE / ADD / DELETE) {#sub-feature-updateexpression-grammar--set---remove---add---delete}

- **Capability ID:** `sub-feature:dynamodb:updateitem:updateexpression-grammar--set---remove---add---delete`
- **Status:** ✅ implemented

Hand-rolled lexer + parser shared with the future Condition/Filter slice.

### SET arithmetic (`a = a + :i`, `a = :x - :y`) {#sub-feature-set-arithmetic--a--a--i-a--x---y}

- **Capability ID:** `sub-feature:dynamodb:updateitem:set-arithmetic--a--a--i-a--x---y`
- **Status:** ✅ implemented

Decimal arithmetic preserves up to 28-29 significant digits; DynamoDB allows 38. Overflow surfaces as ValidationException.

### SET functions `if_not_exists(path, fallback)` and `list_append(l1, l2)` {#sub-feature-set-functions-ifnotexists-path-fallback--and-listappend-l1-l2}

- **Capability ID:** `sub-feature:dynamodb:updateitem:set-functions-ifnotexists-path-fallback--and-listappend-l1-l2`
- **Status:** ✅ implemented

### SET on nested paths (`addr.zip`, `items[0].name`) {#sub-feature-set-on-nested-paths--addrzip-items0name}

- **Capability ID:** `sub-feature:dynamodb:updateitem:set-on-nested-paths--addrzip-items0name`
- **Status:** ✅ implemented

Parent path must already exist as a map/list, matching DynamoDB. Creating a deeply-nested fresh structure requires top-level SET.

### REMOVE on nested paths and missing attributes {#sub-feature-remove-on-nested-paths-and-missing-attributes}

- **Capability ID:** `sub-feature:dynamodb:updateitem:remove-on-nested-paths-and-missing-attributes`
- **Status:** ✅ implemented

REMOVE on a missing path is a no-op.

### ADD on numeric attribute (create-if-missing + addition) {#sub-feature-add-on-numeric-attribute--create-if-missing--addition}

- **Capability ID:** `sub-feature:dynamodb:updateitem:add-on-numeric-attribute--create-if-missing--addition`
- **Status:** ✅ implemented

### ADD / DELETE on string/number/binary sets (union / subtract) {#sub-feature-add---delete-on-string-number-binary-sets--union---subtract}

- **Capability ID:** `sub-feature:dynamodb:updateitem:add---delete-on-string-number-binary-sets--union---subtract`
- **Status:** ✅ implemented

Empty result set causes the attribute to be removed entirely, matching DynamoDB.

### AttributeUpdates (legacy) PUT / DELETE / ADD {#sub-feature-attributeupdates--legacy--put---delete---add}

- **Capability ID:** `sub-feature:dynamodb:updateitem:attributeupdates--legacy--put---delete---add`
- **Status:** ✅ implemented

Normalised internally into the same UpdateExpression AST.

### ExpressionAttributeNames / ExpressionAttributeValues (`#name`, `:value`) {#sub-feature-expressionattributenames---expressionattributevalues--name-value}

- **Capability ID:** `sub-feature:dynamodb:updateitem:expressionattributenames---expressionattributevalues--name-value`
- **Status:** ✅ implemented

### Path overlap detection {#sub-feature-path-overlap-detection}

- **Capability ID:** `sub-feature:dynamodb:updateitem:path-overlap-detection`
- **Status:** ✅ implemented

Two paths in the same expression where one is a prefix of the other are rejected with ValidationException.

### ReturnValues (NONE / ALL_OLD / UPDATED_OLD / ALL_NEW / UPDATED_NEW) {#sub-feature-returnvalues--none---allold---updatedold---allnew---updatednew}

- **Capability ID:** `sub-feature:dynamodb:updateitem:returnvalues--none---allold---updatedold---allnew---updatednew`
- **Status:** ✅ implemented

UPDATED_OLD/UPDATED_NEW project only the top-level attributes touched by the expression, matching AWS.

### Create-if-missing (upsert) semantics {#sub-feature-create-if-missing--upsert--semantics}

- **Capability ID:** `sub-feature:dynamodb:updateitem:create-if-missing--upsert--semantics`
- **Status:** ✅ implemented

Atomic create with `If-None-Match: *` when the target item does not exist; concurrent create races surface as Cosmos 409 and are replayed by the optimistic-retry loop against the winner's state.

### ConditionExpression / Expected / ConditionalOperator {#sub-feature-conditionexpression---expected---conditionaloperator}

- **Capability ID:** `sub-feature:dynamodb:updateitem:conditionexpression---expected---conditionaloperator`
- **Status:** ✅ implemented

Modern ConditionExpression and legacy Expected + ConditionalOperator both supported; mutual exclusion enforced with ValidationException. Evaluator covers comparisons, AND/OR/NOT, BETWEEN, IN, attribute_exists/not_exists/type, begins_with, contains, size(). Failure returns HTTP 400 ConditionalCheckFailedException; ReturnValuesOnConditionCheckFailure=ALL_OLD includes the prior item.

### ReturnConsumedCapacity / ReturnItemCollectionMetrics {#sub-feature-returnconsumedcapacity---returnitemcollectionmetrics}

- **Capability ID:** `sub-feature:dynamodb:updateitem:returnconsumedcapacity---returnitemcollectionmetrics`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

Silently ignored; response omits ConsumedCapacity / ItemCollectionMetrics.

## Behaviour differences

- Cosmos storage-metadata system fields (`_rid`/`_self`/`_etag`/`_ts`/`_attachments`/`_lsn`/`_metadata`) are stripped from response items and never surface as DynamoDB attributes (#203). Caveat: a user attribute literally named identically is also stripped on read; the durable fix is attribute namespacing.
- Key attribute values (S/B) are hex-encoded into the internal Cosmos `id`/partition-key (S → hex(UTF-8 bytes), B → hex(raw bytes), N → order-preserving numeric digit string), accepting Cosmos-forbidden characters (`/`, `\`, `?`, `#`) and fixing B byte-ordering. Effective raw key limit ~127 bytes; over-limit keys are rejected with ValidationException. **On-disk-format breaking change** vs earlier builds. See PutItem for the full rationale.
- Atomicity is implemented as a GET → modify → PUT(If-Match) (or atomic-create with If-None-Match) loop with up to 4 retries on Cosmos 412/409. Sustained contention surfaces as InternalServerError after the retry budget is exhausted.
- Numeric arithmetic is performed with System.Decimal (28-29 significant digits) rather than DynamoDB's 38-digit precision. Operands exceeding the proxy's precision are rejected up front with ValidationException to avoid silent rounding; overflow also throws ValidationException.
- Key attributes referenced by the request are always reinforced into the resulting item — a REMOVE targeting the partition or sort key never deletes them in the stored doc.
- Cosmos 429 (throttled) is surfaced to clients as DynamoDB ProvisionedThroughputExceededException.
- Conditional / ReturnValues=NONE updates use the single-item `atomicWrite_v2` Cosmos stored procedure when stored procedures are enabled AND the expression is within the sproc's supported subset: the condition is evaluated and the UpdateExpression applied server-side in one atomic round-trip. **Validated against real Azure Cosmos DB** (Strong consistency). The v2 body fixes two defects the emulator could never catch (it does not run sprocs): (1) the read link is a partition-local `SELECT * FROM c WHERE c.id = @id` query rather than an invalid `getSelfLink() + 'docs/' + id` mixed link; (2) SET-value operands are serialised as `$k`-tagged envelopes (`lit`/`path`/`op`/`ifne`/`lap`) so the sproc resolves arithmetic (`a = a + :i`), attribute copies, `if_not_exists` and `list_append` server-side instead of storing the unresolved AST.
- The sproc executes only the slice of the expression surface it can reproduce faithfully: SET (scalar/native-map/list literals, `+`/`-` arithmetic, path copy, `if_not_exists`, `list_append`) and REMOVE, with scalar conditions (comparisons, AND/OR/NOT, `attribute_exists`/`attribute_not_exists`, `attribute_type` of S/N/BOOL/NULL/L/M, `begins_with`, BETWEEN, IN). Anything outside it — `ADD`/`DELETE` clauses, string/number/binary **sets**, **binary** values, **high-precision numbers** that do not round-trip through a double, **list-index paths** (`a[0]`), and the `size()` / `contains()` condition forms — is routed away from the sproc: under stored-procedure mode `Preferred` it falls back to the non-atomic GET → modify → PUT loop; under `Required` it fails loud rather than run divergent server-side JS. Atomic counters are still served atomically via `SET c = c + :n`.
- Smoke-verified against the Cosmos DB Linux emulator (vNext preview) via Testcontainers; the GET → modify → PUT fallback path is emulator-covered, the `atomicWrite_v2` sproc path is validated against real Azure Cosmos DB.
- When the existing item must be materialized for condition evaluation or ReturnValues (the GET → modify → PUT loop) and Cosmos returns a CosmosBinary body (opt-in `DynamoDb.CosmosBinaryResponses`), the AttributeValue map is built straight off the binary body via `CosmosBinaryReader` (no binary→text decode + JsonDocument DOM). A marker the streaming reader does not fast-path falls back to the decode-to-text path; a text body uses it directly. The chosen path is observable on `aws2azure_dynamodb_read_decode_path_total{op="update",path=binary|fallback|text}`. The emulator never emits CosmosBinary, so the binary-direct path is exercised against real Azure only.
- The standalone document write body (the create-with-If-None-Match / replace-with-If-Match in the GET → modify → PUT loop) is sent as CosmosBinary (the `0x80` format) when the opt-in `DynamoDb.CosmosBinaryRequests` is enabled (default off), encoded single-pass straight to the wire; the gateway auto-detects the marker so no negotiation header or special Content-Type is used. The sproc-embedded atomic path (`atomicWrite_v2`) keeps JSON text, since the document is embedded as a value inside the sproc parameter array. The chosen format is observable on `aws2azure_dynamodb_write_body_total{format=binary|text}`. The Cosmos DB Linux emulator neither emits nor reliably accepts CosmosBinary, so the binary write path is validated against real Azure only — confirmed parsed + indexed (text read-back + indexed query) by the nightly acceptance test.
- https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/Expressions.UpdateExpressions.html

