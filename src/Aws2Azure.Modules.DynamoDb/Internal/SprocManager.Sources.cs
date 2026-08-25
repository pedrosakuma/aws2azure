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

    // Strict evaluator used by atomicTransactWrite_v3/v4/v5. The frozen
    // atomicWrite_v2 body above keeps its original evaluator and hash.
    private const string TransactionConditionEvaluatorJs = """
    function evaluateCondition(ast, doc) {
        if (!ast) return true;
        switch (ast.type) {
            case 'AND':
                return evaluateCondition(ast.left, doc) && evaluateCondition(ast.right, doc);
            case 'OR':
                return evaluateCondition(ast.left, doc) || evaluateCondition(ast.right, doc);
            case 'NOT':
                return !evaluateCondition(ast.operand, doc);
            case 'COMPARE':
                return evaluateCompare(ast, doc);
            case 'BETWEEN':
                return evaluateBetween(ast, doc);
            case 'IN':
                return evaluateIn(ast, doc);
            case 'ATTR_EXISTS':
                return hasAttr(doc, extractPath(ast.attr));
            case 'ATTR_NOT_EXISTS':
                return !hasAttr(doc, extractPath(ast.attr));
            case 'ATTR_TYPE':
                return checkAttrType(doc, extractPath(ast.attr), ast.attrType);
            case 'BEGINS_WITH':
                var str = readOperand(doc, ast.attr);
                var prefix = readOperand(doc, ast.prefix);
                if (!str.exists || !prefix.exists) return false;
                if (typeof str.value !== 'string'
                    || typeof prefix.value !== 'string') {
                    validationError(
                        'Incorrect operand type for begins_with; both operands must be strings.');
                }
                return str.value.indexOf(prefix.value) === 0;
            default:
                throw new Error('Unsupported condition AST node: ' + ast.type);
        }
    }

    function evaluateCompare(ast, doc) {
        var left = readOperand(doc, ast.attr);
        var right = readOperand(doc, ast.value);
        if (!left.exists || !right.exists) {
            return false;
        }
        if (ast.op === '=' || ast.op === 'EQ' || ast.op === '<>' || ast.op === 'NE') {
            var equal = sameScalarType(left.value, right.value)
                && left.value === right.value;
            return (ast.op === '=' || ast.op === 'EQ') ? equal : !equal;
        }
        if (!sameScalarType(left.value, right.value)) {
            validationError(
                'Incorrect operand types for ordered comparison; operands must share one scalar type.');
        }
        var order = orderedCompare(left.value, right.value);
        if (order === null) {
            validationError(
                'Incorrect operand type for ordered comparison; this transaction profile supports strings only.');
        }
        switch (ast.op) {
            case '<': case 'LT': return order < 0;
            case '<=': case 'LE': return order <= 0;
            case '>': case 'GT': return order > 0;
            case '>=': case 'GE': return order >= 0;
            default: throw new Error('Unsupported comparison operator: ' + ast.op);
        }
    }

    function evaluateBetween(ast, doc) {
        var value = readOperand(doc, ast.value);
        var low = readOperand(doc, ast.low);
        var high = readOperand(doc, ast.high);
        if (!value.exists || !low.exists || !high.exists) return false;
        if (!sameScalarType(value.value, low.value)
            || !sameScalarType(value.value, high.value)) {
            validationError(
                'Incorrect operand types for BETWEEN; the value and both bounds must share one scalar type.');
        }
        var lowOrder = orderedCompare(value.value, low.value);
        var highOrder = orderedCompare(value.value, high.value);
        if (lowOrder === null || highOrder === null) {
            validationError(
                'Incorrect operand type for BETWEEN; this transaction profile supports strings only.');
        }
        return lowOrder >= 0 && highOrder <= 0;
    }

    function evaluateIn(ast, doc) {
        var value = readOperand(doc, ast.attr);
        if (!value.exists) return false;
        for (var i = 0; i < ast.values.length; i++) {
            var candidate = readOperand(doc, ast.values[i]);
            if (candidate.exists
                && sameScalarType(value.value, candidate.value)
                && value.value === candidate.value) {
                return true;
            }
        }
        return false;
    }

    function readOperand(doc, operand) {
        if (operand && typeof operand === 'object' && 'path' in operand) {
            if (!hasAttr(doc, operand.path)) return { exists: false };
            return { exists: true, value: getAttrValue(doc, operand.path) };
        }
        if (operand && typeof operand === 'object') {
            throw new Error('Unsupported condition operand');
        }
        return { exists: true, value: operand };
    }

    function extractPath(operand) {
        if (operand && typeof operand === 'object' && operand.path) return operand.path;
        return operand;
    }

    function sameScalarType(left, right) {
        if (left === null || right === null) return left === null && right === null;
        var leftType = typeof left;
        var rightType = typeof right;
        if (leftType !== rightType) return false;
        return leftType === 'string' || leftType === 'number' || leftType === 'boolean';
    }

    function validationError(message) {
        throw { a2aValidationError: true, message: message };
    }

    function orderedCompare(left, right) {
        if (typeof left !== 'string' || typeof right !== 'string') return null;
        return compareUtf8(left, right);
    }

    function compareUtf8(left, right) {
        var a = utf8Bytes(left);
        var b = utf8Bytes(right);
        var length = Math.min(a.length, b.length);
        for (var i = 0; i < length; i++) {
            if (a[i] !== b[i]) return a[i] < b[i] ? -1 : 1;
        }
        return a.length === b.length ? 0 : (a.length < b.length ? -1 : 1);
    }

    function utf8Bytes(value) {
        var bytes = [];
        for (var i = 0; i < value.length; i++) {
            var code = value.charCodeAt(i);
            if (code >= 0xD800 && code <= 0xDBFF) {
                var low = i + 1 < value.length ? value.charCodeAt(i + 1) : 0;
                if (low >= 0xDC00 && low <= 0xDFFF) {
                    code = 0x10000 + ((code - 0xD800) << 10) + (low - 0xDC00);
                    i++;
                } else {
                    code = 0xFFFD;
                }
            } else if (code >= 0xDC00 && code <= 0xDFFF) {
                code = 0xFFFD;
            }
            if (code < 0x80) {
                bytes.push(code);
            } else if (code < 0x800) {
                bytes.push(0xC0 | (code >> 6), 0x80 | (code & 0x3F));
            } else if (code < 0x10000) {
                bytes.push(0xE0 | (code >> 12),
                    0x80 | ((code >> 6) & 0x3F),
                    0x80 | (code & 0x3F));
            } else {
                bytes.push(0xF0 | (code >> 18),
                    0x80 | ((code >> 12) & 0x3F),
                    0x80 | ((code >> 6) & 0x3F),
                    0x80 | (code & 0x3F));
            }
        }
        return bytes;
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
            if (!Object.prototype.hasOwnProperty.call(cur, parts[i])) return false;
            cur = cur[parts[i]];
        }
        return true;
    }

    function checkAttrType(doc, path, expectedType) {
        if (!hasAttr(doc, path)) return false;
        var val = getAttrValue(doc, path);
        switch (expectedType) {
            case 'S': return typeof val === 'string';
            case 'BOOL': return typeof val === 'boolean';
            case 'NULL': return val === null;
            default: throw new Error('Unsupported attribute_type tag: ' + expectedType);
        }
    }
""";

    private const string SingleWriteSizeValidationJs = """
    function validationError(message) {
        throw { a2aValidationError: true, message: message };
    }

    function utf8Bytes(value) {
        var bytes = [];
        for (var i = 0; i < value.length; i++) {
            var code = value.charCodeAt(i);
            if (code >= 0xD800 && code <= 0xDBFF) {
                var low = i + 1 < value.length ? value.charCodeAt(i + 1) : 0;
                if (low >= 0xDC00 && low <= 0xDFFF) {
                    code = 0x10000 + ((code - 0xD800) << 10) + (low - 0xDC00);
                    i++;
                } else {
                    code = 0xFFFD;
                }
            } else if (code >= 0xDC00 && code <= 0xDFFF) {
                code = 0xFFFD;
            }
            if (code < 0x80) {
                bytes.push(code);
            } else if (code < 0x800) {
                bytes.push(0xC0 | (code >> 6), 0x80 | (code & 0x3F));
            } else if (code < 0x10000) {
                bytes.push(0xE0 | (code >> 12),
                    0x80 | ((code >> 6) & 0x3F),
                    0x80 | (code & 0x3F));
            } else {
                bytes.push(0xF0 | (code >> 18),
                    0x80 | ((code >> 12) & 0x3F),
                    0x80 | ((code >> 6) & 0x3F),
                    0x80 | (code & 0x3F));
            }
        }
        return bytes;
    }

    function validateDocumentSize(doc) {
        var size = 0;
        for (var name in doc) {
            if (!Object.prototype.hasOwnProperty.call(doc, name) || shouldSkipInternalField(name)) {
                continue;
            }
            size += measureStoredFieldName(name);
            size += measureStoredValue(doc[name]);
        }
        if (size > 409600) {
            validationError(
                'Item is ' + size
                + ' bytes; DynamoDB items must not exceed 409600 bytes (400 KiB).');
        }
    }

    function shouldSkipInternalField(name) {
        return name === 'id'
            || name === '_a2a'
            || name === '_a2a_pk'
            || name === 'ttl'
            || name.indexOf('_a2a$ord$') === 0;
    }

    function measureStoredValue(value) {
        if (value === null || value === undefined) return 1;
        var type = typeof value;
        if (type === 'string') return utf8Bytes(value).length;
        if (type === 'number') return measureNumberText(normalizeNumberText(value));
        if (type === 'boolean') return 1;
        if (Array.isArray(value)) {
            var listSize = 3;
            for (var i = 0; i < value.length; i++) {
                listSize += 1 + measureStoredValue(value[i]);
            }
            return listSize;
        }
        if (type === 'object') {
            var envelopeKeys = Object.keys(value);
            if (envelopeKeys.length === 1) {
                switch (envelopeKeys[0]) {
                    case '_a2a:N':
                        return measureNumberText(value['_a2a:N']);
                    case '_a2a:B':
                        return base64ByteLength(value['_a2a:B']);
                    case '_a2a:SS':
                        return measureStringSet(value['_a2a:SS']);
                    case '_a2a:NS':
                        return measureNumberSet(value['_a2a:NS']);
                    case '_a2a:BS':
                        return measureBinarySet(value['_a2a:BS']);
                }
            }

            var mapSize = 3;
            for (var name in value) {
                if (!Object.prototype.hasOwnProperty.call(value, name)) continue;
                mapSize += 1 + measureStoredFieldName(name) + measureStoredValue(value[name]);
            }
            return mapSize;
        }

        validationError('Stored procedure cannot measure item size for value type ' + type + '.');
    }

    function measureStringSet(values) {
        var size = 0;
        for (var i = 0; i < values.length; i++) size += utf8Bytes(values[i]).length;
        return size;
    }

    function measureNumberSet(values) {
        var size = 0;
        for (var i = 0; i < values.length; i++) size += measureNumberText(values[i]);
        return size;
    }

    function measureBinarySet(values) {
        var size = 0;
        for (var i = 0; i < values.length; i++) size += base64ByteLength(values[i]);
        return size;
    }

    function measureStoredFieldName(name) {
        if (name === '_a2a$id') return utf8Bytes('id').length;
        if (name === '_a2a$ttl') return utf8Bytes('ttl').length;
        return utf8Bytes(name).length;
    }

    function base64ByteLength(text) {
        var padding = 0;
        if (text.length >= 2 && text.slice(-2) === '==') padding = 2;
        else if (text.length >= 1 && text.charAt(text.length - 1) === '=') padding = 1;
        return ((text.length / 4) * 3) - padding;
    }

    function measureNumberText(text) {
        var firstSignificant = -1;
        var lastSignificant = -1;
        for (var i = 0; i < text.length; i++) {
            var ch = text.charAt(i);
            if (ch < '1' || ch > '9') continue;
            if (firstSignificant < 0) firstSignificant = i;
            lastSignificant = i;
        }

        var significantDigits = 0;
        if (firstSignificant >= 0) {
            for (var j = firstSignificant; j <= lastSignificant; j++) {
                var digit = text.charAt(j);
                if (digit >= '0' && digit <= '9') significantDigits++;
            }
        } else {
            significantDigits = 1;
        }

        return Math.floor((significantDigits + 1) / 2) + 1;
    }

    function normalizeNumberText(value) {
        if (typeof value !== 'number' || !isFinite(value)) {
            validationError('Stored procedure cannot measure a non-finite numeric value.');
        }

        if (value === 0) return '0';

        var text = value.toString();
        var expIndex = text.indexOf('e');
        if (expIndex < 0) expIndex = text.indexOf('E');
        if (expIndex < 0) {
            return trimPlainNumber(text);
        }

        var negative = text.charAt(0) === '-';
        if (negative) text = text.substring(1);
        expIndex = text.indexOf('e');
        if (expIndex < 0) expIndex = text.indexOf('E');
        var mantissa = text.substring(0, expIndex);
        var exponent = parseInt(text.substring(expIndex + 1), 10);

        var dot = mantissa.indexOf('.');
        var digits = mantissa.replace('.', '');
        var integerDigits = dot < 0 ? digits.length : dot;
        var decimalPos = integerDigits + exponent;
        var plain;
        if (decimalPos <= 0) {
            plain = '0.' + repeatChar('0', -decimalPos) + digits;
        } else if (decimalPos >= digits.length) {
            plain = digits + repeatChar('0', decimalPos - digits.length);
        } else {
            plain = digits.substring(0, decimalPos) + '.' + digits.substring(decimalPos);
        }

        plain = trimPlainNumber(plain);
        if (negative && plain !== '0') plain = '-' + plain;
        return plain;
    }

    function trimPlainNumber(text) {
        var negative = text.charAt(0) === '-';
        var body = negative ? text.substring(1) : text;
        if (body.indexOf('.') >= 0) {
            while (body.length > 0 && body.charAt(body.length - 1) === '0') {
                body = body.substring(0, body.length - 1);
            }
            if (body.length > 0 && body.charAt(body.length - 1) === '.') {
                body = body.substring(0, body.length - 1);
            }
        }
        if (body === '' || body === '0') return '0';
        return negative ? '-' + body : body;
    }

    function repeatChar(ch, count) {
        var result = '';
        for (var i = 0; i < count; i++) result += ch;
        return result;
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
        try {
            switch (op) {
                case 'PUT':
                    if (payload === null) throw { code: 400, body: 'Payload required for PUT' };
                    validateDocumentSize(payload);
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
                    validateDocumentSize(updatedDoc);
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
        } catch (err) {
            if (err
                && err.a2aValidationError === true
                && typeof err.message === 'string') {
                resp.setBody({
                    success: false,
                    validationError: {
                        code: 'ValidationException',
                        message: err.message
                    }
                });
                return;
            }
            throw err;
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
    
""" + ConditionEvaluatorJs + SingleWriteSizeValidationJs + """
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
    /// logical partition. When an idempotency descriptor is present, its
    /// internal record is committed in the same transaction as the user writes.
    /// Algorithm (rollback-safe):
    /// <list type="number">
    ///   <item>Read every target document.</item>
    ///   <item>Evaluate every operation's condition. If ANY fails, emit
    ///   <c>{success:false, reasons:[...]}</c> (attaching a stripped pre-write
    ///   snapshot under <c>item</c> for any failed operation that requested
    ///   <c>ReturnValuesOnConditionCheckFailure=ALL_OLD</c>) and perform NO
    ///   writes.</item>
    ///   <item>Otherwise perform every write (PUT=upsert, DELETE=delete,
    ///   UPDATE=apply SET/REMOVE to the read snapshot then upsert, CHECK=no-op).
    ///   A write error throws, aborting the whole sproc transaction so nothing
    ///   partial is committed.</item>
    /// </list>
    /// The condition evaluator is shared with <c>atomicWrite</c>. The update
    /// executor (<c>applyUpdate</c>/<c>resolveSetValue</c>) is a verbatim copy
    /// of the one in the frozen <c>atomicWrite_v2</c> body: only the SET/REMOVE
    /// subset the C# <see cref="Internal.SprocEligibility"/> gate admits ever
    /// reaches this sproc (no ADD/DELETE clause, top-level non-reserved paths
    /// only, native-JSON literal values only — see #798).
    /// </summary>
    internal static readonly string TransactSprocBody = """
function atomicTransactWrite(operations, idempotency) {
    var ctx = getContext();
    var coll = ctx.getCollection();
    var resp = ctx.getResponse();
    var selfLink = coll.getSelfLink();
    var n = operations.length;
    var existing = new Array(n);
    var lookupNowMs = new Date().getTime();

    if (idempotency === null || idempotency === undefined) {
        readNext(0);
    } else {
        validateIdempotencyInput();
        readIdempotencyRecord();
    }

    function validateIdempotencyInput() {
        if (typeof idempotency.id !== 'string'
            || typeof idempotency.pk !== 'string'
            || typeof idempotency.fingerprint !== 'string'
            || typeof idempotency.windowMs !== 'number'
            || idempotency.windowMs <= 0
            || typeof idempotency.cleanupTtlSeconds !== 'number'
            || idempotency.cleanupTtlSeconds <= 0) {
            throw new Error('Malformed transaction idempotency descriptor');
        }
    }

    function readIdempotencyRecord() {
        var query = {
            query: 'SELECT * FROM c WHERE c.id = @id',
            parameters: [{ name: '@id', value: idempotency.id }]
        };
        var accepted = coll.queryDocuments(selfLink, query, {}, function(err, docs) {
            if (err) throw err;
            var record = (docs && docs.length > 0) ? docs[0] : null;
            if (record && record.expiresAtMs > lookupNowMs) {
                replayOrReject(record);
                return;
            }
            cleanupExpiredRecords();
        });
        if (!accepted) throw new Error('idempotency queryDocuments not accepted');
    }

    function replayOrReject(record) {
        if (record._a2a !== 'transaction-idempotency-v1'
            || record._a2a_pk !== idempotency.pk
            || record.formatVersion !== 1
            || typeof record.fingerprint !== 'string'
            || typeof record.createdAtMs !== 'number'
            || typeof record.expiresAtMs !== 'number') {
            throw new Error('Malformed transaction idempotency record');
        }
        if (record.fingerprint !== idempotency.fingerprint) {
            resp.setBody({ success: false, idempotencyMismatch: true });
            return;
        }
        if (record.outcome === 'success') {
            resp.setBody({ success: true, replayed: true });
            return;
        }
        if (record.outcome === 'canceled'
            && validReasons(record.reasons)) {
            resp.setBody({
                success: false,
                reasons: record.reasons,
                replayed: true
            });
            return;
        }
        throw new Error('Unknown transaction idempotency outcome');
    }

    function validReasons(reasons) {
        if (!Array.isArray(reasons) || reasons.length !== n) return false;
        var failed = false;
        for (var i = 0; i < reasons.length; i++) {
            if (!reasons[i] || (reasons[i].code !== 'None'
                && reasons[i].code !== 'ConditionalCheckFailed')) {
                return false;
            }
            if (reasons[i].code === 'ConditionalCheckFailed') failed = true;
        }
        return failed;
    }

    // Token records carry native ttl for containers where TTL is armed. This
    // bounded partition-local sweep is the fallback for tables where it is not:
    // each new token removes more expired records than it creates, while an
    // inactive partition has finite retained state and no continuing growth.
    function cleanupExpiredRecords() {
        var query = {
            query: "SELECT TOP 8 * FROM c WHERE c._a2a = 'transaction-idempotency-v1' AND c.expiresAtMs <= @now",
            parameters: [{ name: '@now', value: lookupNowMs }]
        };
        var accepted = coll.queryDocuments(selfLink, query, {}, function(err, docs) {
            if (err) throw err;
            deleteExpiredRecord(docs || [], 0);
        });
        if (!accepted) throw new Error('idempotency cleanup query not accepted');
    }

    function deleteExpiredRecord(docs, i) {
        if (i >= docs.length) {
            readNext(0);
            return;
        }
        var accepted = coll.deleteDocument(docs[i]._self, function(err) {
            if (err) throw err;
            deleteExpiredRecord(docs, i + 1);
        });
        if (!accepted) throw new Error('idempotency cleanup delete not accepted');
    }

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
            var op = operations[i];
            var cond = op.condition;
            var pass;
            try {
                pass = (cond === null || cond === undefined)
                    ? true
                    : evaluateCondition(cond, existing[i]);
                // Dry-run the update executor now, against a throwaway clone,
                // while it is still safe to abort cleanly: a Cosmos stored
                // procedure commits whatever writes already ran if the script
                // returns without throwing, so a runtime type error surfacing
                // mid-way through writeNext (after earlier operations already
                // upserted/deleted) could not be rolled back by setBody alone.
                // Validating every UPDATE operand here, before any write in
                // this transaction has been issued, keeps a bad operand
                // (e.g. SET n = n + :x where the stored n is not numeric) a
                // clean ValidationException instead of a partial commit.
                if (pass && op.type === 'UPDATE') {
                    var dryBase = existing[i] ? cloneForReturn(existing[i]) : {};
                    if (op.keyDoc) {
                        for (var dk in op.keyDoc) dryBase[dk] = op.keyDoc[dk];
                    }
                    applyUpdate(dryBase, op.update);
                }
            } catch (err) {
                if (err
                    && err.a2aValidationError === true
                    && typeof err.message === 'string') {
                    resp.setBody({
                        success: false,
                        validationError: {
                            code: 'ValidationException',
                            message: 'TransactItems[' + i + '] validation failed: '
                                + err.message
                        }
                    });
                    return;
                }
                throw err;
            }
            if (pass) {
                reasons[i] = { code: 'None' };
            } else {
                reasons[i] = (op.rvoccf && existing[i])
                    ? { code: 'ConditionalCheckFailed', item: cloneForReturn(existing[i]) }
                    : { code: 'ConditionalCheckFailed' };
                anyFail = true;
            }
        }
        if (anyFail) {
            completeWithIdempotency(
                'canceled',
                reasons,
                function() {
                    resp.setBody({ success: false, reasons: reasons });
                });
            return;
        }
        writeNext(0);
    }

    function writeNext(i) {
        if (i >= n) {
            completeWithIdempotency(
                'success',
                null,
                function() { resp.setBody({ success: true }); });
            return;
        }
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
        } else if (op.type === 'UPDATE') {
            // baseDoc starts from the read snapshot (upsert semantics: an
            // absent item is created from an empty map), stripped of Cosmos
            // system fields so they are neither re-upserted nor visible to
            // the update executor. Key attributes are merged in before AND
            // re-stamped after the update executes, mirroring the
            // GET->apply->PUT fallback's ReinforceKeyAttributes behavior: a
            // SET/REMOVE that touches a key attribute is silently overwritten
            // rather than rejected.
            var baseDoc = existing[i] ? existing[i] : {};
            stripSystemFields(baseDoc);
            if (op.keyDoc) {
                for (var kk in op.keyDoc) baseDoc[kk] = op.keyDoc[kk];
            }
            var updatedDoc = applyUpdate(baseDoc, op.update);
            if (op.keyDoc) {
                for (var kk2 in op.keyDoc) updatedDoc[kk2] = op.keyDoc[kk2];
            }
            var accU = coll.upsertDocument(selfLink, updatedDoc, function(err) {
                if (err) throw err;
                writeNext(i + 1);
            });
            if (!accU) throw new Error('upsertDocument not accepted at operation ' + i);
        } else {
            // CHECK: read-only, no write.
            writeNext(i + 1);
        }
    }

    function completeWithIdempotency(outcome, reasons, complete) {
        if (idempotency === null || idempotency === undefined) {
            complete();
            return;
        }
        var completionNowMs = new Date().getTime();
        var record = {
            id: idempotency.id,
            _a2a_pk: idempotency.pk,
            _a2a: 'transaction-idempotency-v1',
            formatVersion: 1,
            fingerprint: idempotency.fingerprint,
            createdAtMs: completionNowMs,
            expiresAtMs: completionNowMs + idempotency.windowMs,
            ttl: idempotency.cleanupTtlSeconds,
            outcome: outcome
        };
        if (reasons !== null) record.reasons = reasons;
        var accepted = coll.upsertDocument(selfLink, record, function(err) {
            if (err) throw err;
            complete();
        });
        if (!accepted) throw new Error('idempotency upsertDocument not accepted');
    }

    // Removes Cosmos-generated system fields from a queried document so they
    // are not re-written or surfaced as DynamoDB attributes. Mutates in place
    // (mirrors atomicWrite_v2's stripSystemFields).
    function stripSystemFields(d) {
        delete d._rid;
        delete d._self;
        delete d._etag;
        delete d._ts;
        delete d._attachments;
        delete d._lsn;
        delete d._metadata;
    }

    // Deep-clones a read snapshot for ReturnValuesOnConditionCheckFailure=
    // ALL_OLD, stripping Cosmos system fields, WITHOUT mutating the original
    // (which a later operation in the same batch may still need, e.g. its own
    // DELETE self-link).
    function cloneForReturn(doc) {
        var clone = JSON.parse(JSON.stringify(doc));
        stripSystemFields(clone);
        return clone;
    }

    // Update executor: applies the UpdateExpression AST (SET/REMOVE only —
    // SprocEligibility.IsUpdateEligible rejects ADD/DELETE and any path/value
    // shape this cannot faithfully execute) to a document. Verbatim copy of
    // atomicWrite_v2's applyUpdate/resolveSetValue/setAttr/removeAttr.
    function applyUpdate(doc, updateAst) {
        if (!updateAst) return doc;

        if (updateAst.set) {
            for (var i = 0; i < updateAst.set.length; i++) {
                var s = updateAst.set[i];
                setAttr(doc, s.path, resolveSetValue(doc, s.value));
            }
        }

        if (updateAst.remove) {
            for (var i = 0; i < updateAst.remove.length; i++) {
                removeAttr(doc, updateAst.remove[i]);
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
                if (typeof l !== 'number' || typeof r !== 'number') {
                    validationError(
                        'Incorrect operand type for update arithmetic; both operands must be numbers.');
                }
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
                validationError('Unsupported update value operand: ' + v.$k);
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

""" + TransactionConditionEvaluatorJs + """
}
""";

    /// <summary>
    /// Read-only single-partition snapshot used by <c>TransactGetItems</c>.
    /// Every query executes inside one Cosmos stored-procedure transaction, so
    /// the returned positions observe one coherent committed snapshot.
    /// </summary>
    internal static readonly string TransactGetSprocBody = """
function atomicTransactGet(documentIds) {
    var ctx = getContext();
    var coll = ctx.getCollection();
    var resp = ctx.getResponse();
    var selfLink = coll.getSelfLink();
    var items = new Array(documentIds.length);

    readNext(0);

    function readNext(i) {
        if (i >= documentIds.length) {
            resp.setBody({ success: true, items: items });
            return;
        }

        var query = {
            query: 'SELECT * FROM c WHERE c.id = @id',
            parameters: [{ name: '@id', value: documentIds[i] }]
        };
        var accepted = coll.queryDocuments(selfLink, query, {}, function(err, docs) {
            if (err) throw err;
            var item = (docs && docs.length > 0) ? docs[0] : null;
            if (item) stripSystemFields(item);
            items[i] = item;
            readNext(i + 1);
        });
        if (!accepted) throw new Error('queryDocuments not accepted at position ' + i);
    }

    function stripSystemFields(doc) {
        delete doc._rid;
        delete doc._self;
        delete doc._etag;
        delete doc._ts;
        delete doc._attachments;
        delete doc._lsn;
        delete doc._metadata;
    }
}
""";
}
