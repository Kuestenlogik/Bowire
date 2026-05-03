    // ---- Test Assertions ----
    // Newman-style assertions stored per (service, method) in localStorage. Each
    // assertion has: { id, path, op, expected }. Run automatically after every
    // successful response. Results live in lastAssertionResults so the UI can
    // render pass/fail without re-running.
    const TESTS_KEY = 'bowire_tests';

    function loadAllTests() {
        try { return JSON.parse(localStorage.getItem(TESTS_KEY) || '{}') || {}; }
        catch { return {}; }
    }

    function saveAllTests(map) {
        try { localStorage.setItem(TESTS_KEY, JSON.stringify(map)); } catch {}
    }

    function testKey(service, method) { return service + '::' + method; }

    function getTestsFor(service, method) {
        var all = loadAllTests();
        return all[testKey(service, method)] || [];
    }

    function setTestsFor(service, method, tests) {
        var all = loadAllTests();
        all[testKey(service, method)] = tests;
        saveAllTests(all);
    }

    function nextTestId() { return 't_' + Date.now().toString(36) + Math.random().toString(36).slice(2, 6); }

    /**
     * Available operators for assertions:
     *   eq    — equality (deep, JSON-stringified compare)
     *   ne    — not equal
     *   gt    — numeric greater than
     *   gte   — numeric greater than or equal
     *   lt    — numeric less than
     *   lte   — numeric less than or equal
     *   contains   — substring in string OR membership in array
     *   matches    — regex match against the string form
     *   exists     — path resolves to a value (any non-undefined)
     *   notexists  — path does not resolve
     *   type       — typeof check (string|number|boolean|object|array|null)
     */
    var ASSERT_OPERATORS = ['eq','ne','gt','gte','lt','lte','contains','matches','exists','notexists','type'];

    /** State: results from the most recent run, indexed by assertion id. */
    var lastAssertionResults = {};

    function runAssertions(service, method, statusName, parsedResponse) {
        var tests = getTestsFor(service, method);
        var results = {};
        for (var i = 0; i < tests.length; i++) {
            results[tests[i].id] = evaluateAssertion(tests[i], statusName, parsedResponse);
        }
        lastAssertionResults = results;
        return results;
    }

    function evaluateAssertion(test, statusName, parsedResponse) {
        var actual;
        var path = String(test.path || '');
        // Special path "status" reads the gRPC status name; everything else is a
        // response-body JSON path.
        if (path === 'status') {
            actual = statusName;
        } else if (path === '' || path === 'response') {
            actual = parsedResponse;
        } else if (path.indexOf('response.') === 0) {
            actual = walkJsonPath(parsedResponse, path.substring('response.'.length));
        } else {
            actual = walkJsonPath(parsedResponse, path);
        }

        var op = test.op || 'eq';
        var expected = test.expected;
        var pass = false;
        var message = null;

        try {
            switch (op) {
                case 'eq':       pass = deepEqualsLoose(actual, expected); break;
                case 'ne':       pass = !deepEqualsLoose(actual, expected); break;
                case 'gt':       pass = Number(actual) >  Number(expected); break;
                case 'gte':      pass = Number(actual) >= Number(expected); break;
                case 'lt':       pass = Number(actual) <  Number(expected); break;
                case 'lte':      pass = Number(actual) <= Number(expected); break;
                case 'contains':
                    if (Array.isArray(actual)) {
                        pass = actual.some(function (e) { return deepEqualsLoose(e, expected); });
                    } else if (typeof actual === 'string') {
                        pass = actual.indexOf(String(expected)) >= 0;
                    } else {
                        pass = false;
                        message = 'contains requires string or array';
                    }
                    break;
                case 'matches':
                    if (actual == null) { pass = false; break; }
                    try { pass = new RegExp(String(expected)).test(String(actual)); }
                    catch (e) { pass = false; message = 'invalid regex: ' + e.message; }
                    break;
                case 'exists':    pass = actual !== undefined && actual !== null; break;
                case 'notexists': pass = actual === undefined || actual === null; break;
                case 'type':      pass = typeOf(actual) === String(expected); break;
                default:          pass = false; message = 'unknown operator: ' + op;
            }
        } catch (e) {
            pass = false;
            message = e.message;
        }

        return {
            pass: pass,
            actual: actual,
            expected: expected,
            op: op,
            path: test.path,
            message: message
        };
    }

    function deepEqualsLoose(a, b) {
        // Loose equality with type coercion for the typical "expected" string
        // coming from a text input vs the actual value parsed from JSON.
        if (a === b) return true;
        if (a == null || b == null) return a == b;
        if (typeof a === 'number' || typeof b === 'number') {
            var na = Number(a), nb = Number(b);
            if (!Number.isNaN(na) && !Number.isNaN(nb)) return na === nb;
        }
        if (typeof a === 'boolean' || typeof b === 'boolean') {
            return String(a) === String(b);
        }
        if (typeof a === 'object' && typeof b === 'object') {
            try { return JSON.stringify(a) === JSON.stringify(b); }
            catch { return false; }
        }
        return String(a) === String(b);
    }

    function typeOf(v) {
        if (v === null) return 'null';
        if (Array.isArray(v)) return 'array';
        return typeof v;
    }

    /**
     * Builds a portable Test Collection from the assertions configured for a
     * single method. The format mirrors what the CLI runner expects:
     *
     *   {
     *     "name": "...",
     *     "serverUrl": "...",
     *     "protocol": "...",
     *     "tests": [
     *       {
     *         "name": "...",
     *         "service": "...",
     *         "method": "...",
     *         "messages": [...],
     *         "metadata": {...},
     *         "assert": [{ path, op, expected }, ...]
     *       }
     *     ]
     *   }
     */
    function buildTestCollection(service, method, tests) {
        var primaryUrl = (service && service.originUrl) || getPrimaryServerUrl() || '';
        // Snapshot the current request body so the exported collection is runnable
        var bodyTemplate;
        if (requestInputMode === 'form' && method && method.inputType) {
            try { syncFormToJson(); } catch {}
            bodyTemplate = requestMessages[0] || '{}';
        } else {
            var editor = document.querySelector('.bowire-editor') || document.querySelector('.bowire-message-editor');
            bodyTemplate = editor ? editor.value || '{}' : '{}';
        }
        return {
            name: service.name + '/' + method.name + ' tests',
            serverUrl: primaryUrl,
            protocol: service.source || selectedProtocol || undefined,
            tests: [
                {
                    name: method.name,
                    service: service.name,
                    method: method.name,
                    messages: [bodyTemplate],
                    metadata: {},
                    assert: tests.map(function (t) {
                        return { path: t.path, op: t.op, expected: t.expected };
                    })
                }
            ]
        };
    }

    /**
     * Loads assertions from a previously exported collection. Imports the
     * assertions for the FIRST test entry — multi-test collections are
     * supported by the CLI runner but the UI is single-method-scoped.
     */
    function importTestCollection(data, service, method) {
        if (!data || !Array.isArray(data.tests) || data.tests.length === 0) {
            throw new Error('Collection has no tests');
        }
        var firstTest = data.tests[0];
        if (!Array.isArray(firstTest.assert)) {
            throw new Error('First test has no assertions');
        }
        var imported = firstTest.assert.map(function (a) {
            return {
                id: nextTestId(),
                path: a.path || '',
                op: a.op || 'eq',
                expected: a.expected == null ? '' : String(a.expected)
            };
        });
        setTestsFor(service.name, method.name, imported);
        lastAssertionResults = {};
    }

    function getAssertionSummary(service, method) {
        var tests = getTestsFor(service, method);
        if (tests.length === 0) return null;
        var passed = 0, failed = 0, untested = 0;
        for (var i = 0; i < tests.length; i++) {
            var r = lastAssertionResults[tests[i].id];
            if (r === undefined) untested++;
            else if (r.pass) passed++;
            else failed++;
        }
        return { total: tests.length, passed: passed, failed: failed, untested: untested };
    }

