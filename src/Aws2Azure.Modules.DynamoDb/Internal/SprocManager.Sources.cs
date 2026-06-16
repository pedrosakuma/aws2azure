using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Aws2Azure.Modules.DynamoDb.Operations;
using Microsoft.Extensions.Logging;

namespace Aws2Azure.Modules.DynamoDb.Internal;

internal sealed partial class SprocManager
{
    // Shared verbatim JavaScript emitted into both stored procedures. Keeping
    // the condition evaluator in one C# constant makes C#↔JS AST drift visible
    // and prevents the single-write and transact sprocs from diverging.
    private const string ConditionEvaluatorJs = """
    // Condition evaluator: interprets the AST from C# ConditionExpressionParser.
    // Shared by atomicWrite and atomicTransactWrite.
    function evaluateCondition(ast, doc) {
        if (!ast) return true;
        switch (ast.type) {
            case 'AND': return evaluateCondition(ast.left, doc) && evaluateCondition(ast.right, doc);
            case 'OR': return evaluateCondition(ast.left, doc) || evaluateCondition(ast.right, doc);
            case 'NOT': return !evaluateCondition(ast.operand, doc);
            case 'COMPARE': return evaluateCompare(ast, doc);
            case 'BETWEEN':
                var val = getAttrValue(doc, extractPath(ast.value));
                return val >= extractValue(ast.low) && val <= extractValue(ast.high);
            case 'IN':
                var v = getAttrValue(doc, extractPath(ast.attr));
                var inVals = ast.values.map(function(x) { return extractValue(x); });
                return inVals.indexOf(v) >= 0;
            case 'ATTR_EXISTS': return hasAttr(doc, extractPath(ast.attr));
            case 'ATTR_NOT_EXISTS': return !hasAttr(doc, extractPath(ast.attr));
            case 'ATTR_TYPE': return checkAttrType(doc, extractPath(ast.attr), ast.attrType);
            case 'BEGINS_WITH':
                var str = getAttrValue(doc, extractPath(ast.attr));
                return typeof str === 'string' && str.indexOf(extractValue(ast.prefix)) === 0;
            case 'CONTAINS':
                var container = getAttrValue(doc, extractPath(ast.attr));
                var containsVal = extractValue(ast.value);
                if (typeof container === 'string') return container.indexOf(containsVal) >= 0;
                if (Array.isArray(container)) return container.indexOf(containsVal) >= 0;
                return false;
            case 'SIZE':
                var size = getSize(doc, extractPath(ast.attr));
                return evaluateCompareValue(size, ast.op, extractValue(ast.sizeValue));
            default:
                return true;
        }
    }

    function evaluateCompare(ast, doc) {
        var left = extractOperandValue(doc, ast.attr);
        var right = extractOperandValue(doc, ast.value);
        switch (ast.op) {
            case '=': case 'EQ': return left === right;
            case '<>': case 'NE': return left !== right;
            case '<': case 'LT': return left < right;
            case '<=': case 'LE': return left <= right;
            case '>': case 'GT': return left > right;
            case '>=': case 'GE': return left >= right;
            default: return false;
        }
    }

    function extractPath(operand) {
        if (operand && typeof operand === 'object' && operand.path) return operand.path;
        return operand;
    }

    function extractValue(operand) {
        if (operand && typeof operand === 'object') {
            if ('path' in operand) return undefined;
            return operand;
        }
        return operand;
    }

    function extractOperandValue(doc, operand) {
        if (operand && typeof operand === 'object') {
            if (operand.path) return getAttrValue(doc, operand.path);
            if (operand.size) return getSize(doc, operand.size);
        }
        return operand;
    }

    function evaluateCompareValue(left, op, right) {
        switch (op) {
            case '=': case 'EQ': return left === right;
            case '<>': case 'NE': return left !== right;
            case '<': case 'LT': return left < right;
            case '<=': case 'LE': return left <= right;
            case '>': case 'GT': return left > right;
            case '>=': case 'GE': return left >= right;
            default: return false;
        }
    }

    function getAttrValue(doc, path) {
        if (!doc) return undefined;
        var parts = path.split('.');
        var cur = doc;
        for (var i = 0; i < parts.length; i++) {
            if (cur === null || cur === undefined) return undefined;
            cur = cur[parts[i]];
        }
        return cur;
    }

    function hasAttr(doc, path) {
        if (!doc) return false;
        var parts = path.split('.');
        var cur = doc;
        for (var i = 0; i < parts.length; i++) {
            if (cur === null || cur === undefined) return false;
            if (!cur.hasOwnProperty(parts[i])) return false;
            cur = cur[parts[i]];
        }
        return true;
    }

    function getSize(doc, path) {
        var val = getAttrValue(doc, path);
        if (typeof val === 'string') return val.length;
        if (Array.isArray(val)) return val.length;
        if (val && typeof val === 'object') return Object.keys(val).length;
        return 0;
    }

    function checkAttrType(doc, path, expectedType) {
        var val = getAttrValue(doc, path);
        switch (expectedType) {
            case 'S': return typeof val === 'string';
            case 'N': return typeof val === 'number';
            case 'B': return false;
            case 'BOOL': return typeof val === 'boolean';
            case 'NULL': return val === null;
            case 'L': return Array.isArray(val);
            case 'M': return val && typeof val === 'object' && !Array.isArray(val);
            case 'SS': case 'NS': case 'BS': return Array.isArray(val);
            default: return false;
        }
    }
""";

    /// <summary>
    /// The JavaScript stored procedure body that executes atomic conditional writes.
    /// Handles PUT, UPDATE, and DELETE operations with optional condition evaluation.
    /// </summary>
    internal static readonly string SprocBody = """
function atomicWrite(op, docId, payload, conditionAst, updateAst) {
    var ctx = getContext();
    var coll = ctx.getCollection();
    var resp = ctx.getResponse();
    var selfLink = coll.getSelfLink();

    // getSelfLink() is RID-based, so a constructed 'docs/<userId>' link is an
    // invalid mixed link that real Cosmos rejects with "Error creating request
    // message" (#202). Read by id with a partition-local query instead — the
    // sproc executes within the single logical partition of docId.
    var query = {
        query: 'SELECT * FROM c WHERE c.id = @id',
        parameters: [{ name: '@id', value: docId }]
    };
    var accepted = coll.queryDocuments(selfLink, query, {}, function(err, docs) {
        if (err) throw err;

        var existing = (docs && docs.length > 0) ? docs[0] : null;
        // Capture the document's own RID-based self link before stripping it —
        // deleteDocument requires it (a constructed id link is rejected).
        var existingSelf = existing ? existing._self : null;
        // Strip Cosmos system fields so they neither leak into ReturnValues nor
        // get re-upserted: upsertDocument rejects a body that carries stale
        // _self / _rid / _etag / _ts system properties.
        if (existing) stripSystemFields(existing);

        // Clone existing before any mutation (for ReturnValues=ALL_OLD)
        var oldItemClone = existing ? JSON.parse(JSON.stringify(existing)) : null;

        // Evaluate condition if present
        if (conditionAst !== null) {
            if (!evaluateCondition(conditionAst, existing)) {
                resp.setBody({ success: false, conditionFailed: true, oldItem: oldItemClone });
                return;
            }
        }

        // Execute operation
        switch (op) {
            case 'PUT':
                if (payload === null) throw { code: 400, body: 'Payload required for PUT' };
                // payload is already an object (not JSON string) built clean by C#
                coll.upsertDocument(selfLink, payload, function(e) { if (e) throw e; });
                resp.setBody({ success: true, operation: 'PUT', oldItem: oldItemClone });
                break;

            case 'UPDATE':
                if (updateAst === null) throw { code: 400, body: 'UpdateAst required for UPDATE' };
                var baseDoc = existing || {};
                if (payload) {
                    // payload contains the key attributes to ensure they're set (already an object)
                    for (var k in payload) baseDoc[k] = payload[k];
                }
                // updateAst is already an object (not JSON string)
                var updatedDoc = applyUpdate(baseDoc, updateAst);
                coll.upsertDocument(selfLink, updatedDoc, function(e) { if (e) throw e; });
                resp.setBody({ success: true, operation: 'UPDATE', oldItem: oldItemClone, newItem: updatedDoc });
                break;

            case 'DELETE':
                if (existingSelf) {
                    coll.deleteDocument(existingSelf, function(e) { if (e) throw e; });
                }
                resp.setBody({ success: true, operation: 'DELETE', oldItem: oldItemClone });
                break;

            default:
                throw { code: 400, body: 'Unknown operation: ' + op };
        }
    });

    if (!accepted) throw { code: 429, body: 'Request not accepted' };

    // Removes Cosmos-generated system fields from a queried document so they
    // are not re-written or surfaced as DynamoDB attributes.
    function stripSystemFields(d) {
        delete d._rid;
        delete d._self;
        delete d._etag;
        delete d._ts;
        delete d._attachments;
        delete d._lsn;
        delete d._metadata;
    }
    
""" + ConditionEvaluatorJs + """
    // Update executor: applies UpdateExpression AST to a document
    function applyUpdate(doc, updateAst) {
        if (!updateAst) return doc;
        
        // SET actions
        if (updateAst.set) {
            for (var i = 0; i < updateAst.set.length; i++) {
                var s = updateAst.set[i];
                setAttr(doc, s.path, resolveSetValue(doc, s.value));
            }
        }
        
        // REMOVE actions
        if (updateAst.remove) {
            for (var i = 0; i < updateAst.remove.length; i++) {
                removeAttr(doc, updateAst.remove[i]);
            }
        }
        
        // ADD actions (numeric increment or set add)
        if (updateAst.add) {
            for (var i = 0; i < updateAst.add.length; i++) {
                var a = updateAst.add[i];
                var cur = getAttrValue(doc, a.path);
                if (typeof cur === 'number' && typeof a.value === 'number') {
                    setAttr(doc, a.path, cur + a.value);
                } else if (Array.isArray(cur)) {
                    // Add to set (unique values)
                    if (cur.indexOf(a.value) < 0) cur.push(a.value);
                } else if (cur === undefined) {
                    setAttr(doc, a.path, a.value);
                }
            }
        }
        
        // DELETE actions (set remove)
        if (updateAst.delete) {
            for (var i = 0; i < updateAst.delete.length; i++) {
                var d = updateAst.delete[i];
                var arr = getAttrValue(doc, d.path);
                if (Array.isArray(arr)) {
                    var idx = arr.indexOf(d.value);
                    if (idx >= 0) arr.splice(idx, 1);
                }
            }
        }
        
        return doc;
    }

    // Resolves a tagged SET-value operand ($k discriminator from
    // SprocAstSerializer.WriteValueOperand) against the current document.
    function resolveSetValue(doc, v) {
        if (v === null || typeof v !== 'object' || !('$k' in v)) return v;
        switch (v.$k) {
            case 'lit':
                return v.v;
            case 'path':
                return getAttrValue(doc, v.p);
            case 'op':
                var l = resolveSetValue(doc, v.l);
                var r = resolveSetValue(doc, v.r);
                return v.o === '+' ? (l + r) : (l - r);
            case 'ifne':
                var cur = getAttrValue(doc, v.p);
                return (cur !== undefined && cur !== null) ? cur : resolveSetValue(doc, v.f);
            case 'lap':
                var ll = resolveSetValue(doc, v.l);
                if (!Array.isArray(ll)) ll = [];
                var rr = resolveSetValue(doc, v.r);
                if (!Array.isArray(rr)) rr = [];
                return ll.concat(rr);
            default:
                return v;
        }
    }

    function setAttr(doc, path, value) {
        var parts = path.split('.');
        var cur = doc;
        for (var i = 0; i < parts.length - 1; i++) {
            if (!cur[parts[i]]) cur[parts[i]] = {};
            cur = cur[parts[i]];
        }
        cur[parts[parts.length - 1]] = value;
    }
    
    function removeAttr(doc, path) {
        var parts = path.split('.');
        var cur = doc;
        for (var i = 0; i < parts.length - 1; i++) {
            if (!cur[parts[i]]) return;
            cur = cur[parts[i]];
        }
        delete cur[parts[parts.length - 1]];
    }
}
""";

    /// <summary>
    /// Multi-operation stored procedure for <c>TransactWriteItems</c>. Executes
    /// a list of PUT / DELETE / CHECK operations atomically within a single
    /// logical partition. Algorithm (rollback-safe):
    /// <list type="number">
    ///   <item>Read every target document.</item>
    ///   <item>Evaluate every operation's condition. If ANY fails, emit
    ///   <c>{success:false, reasons:[...]}</c> and perform NO writes.</item>
    ///   <item>Otherwise perform every write (PUT=upsert, DELETE=delete,
    ///   CHECK=no-op). A write error throws, aborting the whole sproc
    ///   transaction so nothing partial is committed.</item>
    /// </list>
    /// Only the condition evaluator is shared with <c>atomicWrite</c>; there is
    /// deliberately no update executor here — atomic <c>Update</c> is rejected
    /// by the handler and documented as a gap.
    /// </summary>
    internal static readonly string TransactSprocBody = """
function atomicTransactWrite(operations) {
    var ctx = getContext();
    var coll = ctx.getCollection();
    var resp = ctx.getResponse();
    var selfLink = coll.getSelfLink();
    var n = operations.length;
    var existing = new Array(n);

    readNext(0);

    function readNext(i) {
        if (i >= n) { evaluateAndWrite(); return; }
        var op = operations[i];
        // getSelfLink() is RID-based, so a constructed 'docs/<userId>' link is
        // an invalid mixed link that real Cosmos rejects with "Error creating
        // request message". Read by id with a partition-local query instead —
        // every operation shares the sproc's single logical partition.
        var query = {
            query: 'SELECT * FROM c WHERE c.id = @id',
            parameters: [{ name: '@id', value: op.id }]
        };
        var accepted = coll.queryDocuments(selfLink, query, {}, function(err, docs) {
            if (err) throw err;
            existing[i] = (docs && docs.length > 0) ? docs[0] : null;
            readNext(i + 1);
        });
        if (!accepted) throw new Error('queryDocuments not accepted at operation ' + i);
    }

    function evaluateAndWrite() {
        var reasons = new Array(n);
        var anyFail = false;
        for (var i = 0; i < n; i++) {
            var cond = operations[i].condition;
            var pass = (cond === null || cond === undefined) ? true : evaluateCondition(cond, existing[i]);
            reasons[i] = pass ? { code: 'None' } : { code: 'ConditionalCheckFailed' };
            if (!pass) anyFail = true;
        }
        if (anyFail) { resp.setBody({ success: false, reasons: reasons }); return; }
        writeNext(0);
    }

    function writeNext(i) {
        if (i >= n) { resp.setBody({ success: true }); return; }
        var op = operations[i];
        if (op.type === 'PUT') {
            var accP = coll.upsertDocument(selfLink, op.doc, function(err) {
                if (err) throw err;
                writeNext(i + 1);
            });
            if (!accP) throw new Error('upsertDocument not accepted at operation ' + i);
        } else if (op.type === 'DELETE') {
            if (existing[i]) {
                // Delete via the document's own RID-based self link (from the
                // query result) — a constructed id link would be rejected.
                var accD = coll.deleteDocument(existing[i]._self, function(err) {
                    if (err) throw err;
                    writeNext(i + 1);
                });
                if (!accD) throw new Error('deleteDocument not accepted at operation ' + i);
            } else {
                writeNext(i + 1);
            }
        } else {
            // CHECK: read-only, no write.
            writeNext(i + 1);
        }
    }

""" + ConditionEvaluatorJs + """
}
""";
}
