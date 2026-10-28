    // ---- JSONPath (#676) ----
    //
    // The browser half of Bowire's one path resolver. The C# half is
    // Kuestenlogik.Bowire.Paths.BowireJsonPath; both are held to
    // tests/Kuestenlogik.Bowire.Tests/Paths/json-path-golden.json —
    // wwwroot-js/json-path.test.mjs replays it here, BowireJsonPathGoldenTests
    // there — so a path that matches in the Tests tab matches under
    // `bowire test`. Change one side alone and its test goes red.
    //
    // Two spellings:
    //   * the short form, as before: items.0.tags.2 (a numeric step indexes
    //     an array and names a property of an object);
    //   * JSONPath from `$` (RFC 9535 subset): .name ['name'] [0] [-1] [*] .*
    //     ..name [start:end:step] [0,2] and filters [?(@.done == true)] with
    //     == != < <= > >=, && || !, parentheses and existence tests.
    //
    // Pure functions: no DOM, no module state beyond the compile cache.
    // A path that does not parse throws an Error with `.position`.
    //
    // One known difference: a JS object lists integer-like keys first, so a
    // wildcard over {"b":…, "1":…} visits "1" first here and "b" first in C#.

    var _bowireJsonPathCache = new Map();

    function bowireJsonPathError(text, pos) {
        var err = new Error('Invalid JSONPath at position ' + pos + ': ' + text);
        err.position = pos;
        return err;
    }

    /** Every node `path` selects in `root`: [{ value, parent, key }] in document order. */
    function bowireJsonPathSelect(root, path) {
        var segments = bowireJsonPathCompile(path);
        var current = [{ value: root, parent: null, key: null }];
        for (var i = 0; i < segments.length; i++) {
            var next = [];
            for (var j = 0; j < current.length; j++) segments[i].apply(current[j], root, next);
            current = next;
        }
        return current;
    }

    /**
     * The value an assertion or a chaining placeholder sees:
     * { found: false } when nothing matches, the node for one match, an
     * array of the matches when a wildcard / filter / slice / union /
     * descent can select several.
     */
    function bowireJsonPathResolve(root, path) {
        var matches = bowireJsonPathSelect(root, path);
        if (matches.length === 0) return { found: false, value: undefined };
        if (matches.length === 1 && !bowireJsonPathIsMultiple(path)) return { found: true, value: matches[0].value };
        return { found: true, value: matches.map(function (m) { return m.value; }) };
    }

    function bowireJsonPathIsMultiple(path) {
        return bowireJsonPathCompile(path).some(function (s) { return s.multiple; });
    }

    function bowireJsonPathCompile(path) {
        if (_bowireJsonPathCache.has(path)) return _bowireJsonPathCache.get(path);
        var segments = new BowireJsonPathParser(String(path)).parsePath();
        if (_bowireJsonPathCache.size < 512) _bowireJsonPathCache.set(path, segments);
        return segments;
    }

    // ---- segments ----

    function _jpIsObject(v) { return v !== null && typeof v === 'object' && !Array.isArray(v); }

    function _jpChildren(match, out) {
        var v = match.value;
        if (Array.isArray(v)) {
            for (var i = 0; i < v.length; i++) out.push({ value: v[i], parent: v, key: i });
        } else if (_jpIsObject(v)) {
            var keys = Object.keys(v);
            for (var k = 0; k < keys.length; k++) out.push({ value: v[keys[k]], parent: v, key: keys[k] });
        }
    }

    function _jpName(name) {
        return {
            kind: 'name', name: name, multiple: false,
            apply: function (m, root, out) {
                if (_jpIsObject(m.value) && Object.prototype.hasOwnProperty.call(m.value, name)) {
                    out.push({ value: m.value[name], parent: m.value, key: name });
                }
            }
        };
    }

    function _jpIndex(index) {
        return {
            kind: 'index', index: index, multiple: false,
            apply: function (m, root, out) {
                if (!Array.isArray(m.value)) return;
                var i = index < 0 ? m.value.length + index : index;
                if (i >= 0 && i < m.value.length) out.push({ value: m.value[i], parent: m.value, key: i });
            }
        };
    }

    // The short form's numeric step: an index into an array, a name on an object.
    function _jpNameOrIndex(name) {
        return {
            kind: 'nameOrIndex', name: name, multiple: false,
            apply: function (m, root, out) {
                if (Array.isArray(m.value)) {
                    var i = parseInt(name, 10);
                    if (i < m.value.length) out.push({ value: m.value[i], parent: m.value, key: i });
                } else if (_jpIsObject(m.value) && Object.prototype.hasOwnProperty.call(m.value, name)) {
                    out.push({ value: m.value[name], parent: m.value, key: name });
                }
            }
        };
    }

    function _jpWildcard() {
        return { kind: 'wildcard', multiple: true, apply: function (m, root, out) { _jpChildren(m, out); } };
    }

    function _jpSlice(start, end, step) {
        return {
            kind: 'slice', multiple: true,
            apply: function (m, root, out) {
                var a = m.value;
                if (!Array.isArray(a) || step === 0) return;
                var len = a.length;
                function norm(v) { return v >= 0 ? v : len + v; }
                var i, lower, upper;
                if (step > 0) {
                    lower = Math.min(Math.max(norm(start === null ? 0 : start), 0), len);
                    upper = Math.min(Math.max(norm(end === null ? len : end), 0), len);
                    for (i = lower; i < upper; i += step) out.push({ value: a[i], parent: a, key: i });
                } else {
                    upper = Math.min(Math.max(norm(start === null ? len - 1 : start), -1), len - 1);
                    lower = Math.min(Math.max(norm(end === null ? -len - 1 : end), -1), len - 1);
                    for (i = upper; i > lower; i += step) out.push({ value: a[i], parent: a, key: i });
                }
            }
        };
    }

    function _jpUnion(parts) {
        return {
            kind: 'union', multiple: true,
            apply: function (m, root, out) { for (var i = 0; i < parts.length; i++) parts[i].apply(m, root, out); }
        };
    }

    function _jpFilter(expr) {
        return {
            kind: 'filter', multiple: true,
            apply: function (m, root, out) {
                var children = [];
                _jpChildren(m, children);
                for (var i = 0; i < children.length; i++) {
                    if (expr.test(children[i].value, root)) out.push(children[i]);
                }
            }
        };
    }

    // `..x`: the inner segment on the node and every descendant, in document order.
    function _jpDescendant(inner) {
        var seg = {
            kind: 'descendant', multiple: true,
            apply: function (m, root, out) {
                inner.apply(m, root, out);
                var children = [];
                _jpChildren(m, children);
                for (var i = 0; i < children.length; i++) {
                    var v = children[i].value;
                    if (v !== null && typeof v === 'object') seg.apply(children[i], root, out);
                }
            }
        };
        return seg;
    }

    // ---- filter values ----

    // Nothing (a path that selected nothing) is { nothing: true }.
    function _jpEqual(a, b) {
        if (a.nothing || b.nothing) return a.nothing && b.nothing;
        return _jpDeepEqual(a.value, b.value);
    }

    function _jpDeepEqual(x, y) {
        if (x === null || y === null) return x === y;
        if (typeof x === 'number' && typeof y === 'number') return x === y;
        if (Array.isArray(x)) {
            if (!Array.isArray(y) || x.length !== y.length) return false;
            for (var i = 0; i < x.length; i++) if (!_jpDeepEqual(x[i], y[i])) return false;
            return true;
        }
        if (_jpIsObject(x)) {
            if (!_jpIsObject(y)) return false;
            var kx = Object.keys(x), ky = Object.keys(y);
            if (kx.length !== ky.length) return false;
            for (var k = 0; k < kx.length; k++) {
                if (!Object.prototype.hasOwnProperty.call(y, kx[k]) || !_jpDeepEqual(x[kx[k]], y[kx[k]])) return false;
            }
            return true;
        }
        return x === y;
    }

    // Only numbers with numbers and strings with strings are ordered.
    function _jpLess(a, b) {
        if (a.nothing || b.nothing || a.value === null || b.value === null) return false;
        if (typeof a.value === 'number' && typeof b.value === 'number') return a.value < b.value;
        if (typeof a.value === 'string' && typeof b.value === 'string') return a.value < b.value;
        return false;
    }

    // ---- parser ----

    function BowireJsonPathParser(text) {
        this.text = text;
        this.pos = 0;
    }

    BowireJsonPathParser.prototype.parsePath = function () {
        var t = this.text;
        if (t.trim().length === 0) return [];
        if (t.replace(/^\s+/, '').charAt(0) === '$') {
            this.pos = t.indexOf('$') + 1;
            var segments = this.parseSegments();
            this.skipSpace();
            if (this.pos < t.length) throw this.error('unexpected character');
            return segments;
        }
        return this.parseShort();
    };

    // `items.0.tags` and `items[0]['name']`: the form assertions always took.
    BowireJsonPathParser.prototype.parseShort = function () {
        var t = this.text, segments = [], name = '';
        function flush() {
            if (name.length === 0) return;
            segments.push(/^[0-9]+$/.test(name) ? _jpNameOrIndex(name) : _jpName(name));
            name = '';
        }
        while (this.pos < t.length) {
            var c = t.charAt(this.pos);
            if (c === '.') { flush(); this.pos++; }
            else if (c === '[') { flush(); segments.push(this.parseBracket()); }
            else { name += c; this.pos++; }
        }
        flush();
        return segments;
    };

    BowireJsonPathParser.prototype.parseSegments = function () {
        var t = this.text, segments = [];
        while (this.pos < t.length) {
            if (this.peek('..')) {
                this.pos += 2;
                if (t.charAt(this.pos) === '[') segments.push(_jpDescendant(this.parseBracket()));
                else if (t.charAt(this.pos) === '*') { this.pos++; segments.push(_jpDescendant(_jpWildcard())); }
                else segments.push(_jpDescendant(_jpName(this.parseName())));
            } else if (t.charAt(this.pos) === '.') {
                this.pos++;
                if (t.charAt(this.pos) === '*') { this.pos++; segments.push(_jpWildcard()); }
                else {
                    // `$.tags.0` is not JSONPath: refused loudly rather than read as a
                    // property "0" that silently matches nothing on an array.
                    var start = this.pos;
                    var name = this.parseName();
                    if (/^[0-9]+$/.test(name)) {
                        throw bowireJsonPathError("a number after '.' is not a name; write [" + name + "], or use the short form without '$'", start);
                    }
                    segments.push(_jpName(name));
                }
            } else if (t.charAt(this.pos) === '[') {
                segments.push(this.parseBracket());
            } else {
                break;
            }
        }
        return segments;
    };

    function _jpIsNameChar(c) {
        return /[A-Za-z0-9_\-$]/.test(c) || c.charCodeAt(0) > 127;
    }

    BowireJsonPathParser.prototype.parseName = function () {
        var start = this.pos;
        while (this.pos < this.text.length && _jpIsNameChar(this.text.charAt(this.pos))) this.pos++;
        if (this.pos === start) throw this.error('expected a name');
        return this.text.substring(start, this.pos);
    };

    BowireJsonPathParser.prototype.parseBracket = function () {
        this.expect('[');
        this.skipSpace();
        if (this.peek('?')) {
            this.pos++;
            this.skipSpace();
            var expr = this.parseOr();
            this.skipSpace();
            this.expect(']');
            return _jpFilter(expr);
        }
        var parts = [];
        for (;;) {
            this.skipSpace();
            parts.push(this.parseSelector());
            this.skipSpace();
            if (this.text.charAt(this.pos) === ',') { this.pos++; continue; }
            break;
        }
        this.expect(']');
        return parts.length === 1 ? parts[0] : _jpUnion(parts);
    };

    function _jpIsIntStart(c) { return c === '-' || (c >= '0' && c <= '9' && c !== ''); }

    BowireJsonPathParser.prototype.parseSelector = function () {
        var t = this.text;
        if (this.pos >= t.length) throw this.error('expected a selector');
        var c = t.charAt(this.pos);
        if (c === '*') { this.pos++; return _jpWildcard(); }
        if (c === '\'' || c === '"') return _jpName(this.parseString());
        if (c === ':' || _jpIsIntStart(c)) {
            var start = c === ':' ? null : this.parseInt();
            this.skipSpace();
            if (t.charAt(this.pos) === ':') {
                this.pos++;
                this.skipSpace();
                var end = _jpIsIntStart(t.charAt(this.pos)) ? this.parseInt() : null;
                this.skipSpace();
                var step = 1;
                if (t.charAt(this.pos) === ':') {
                    this.pos++;
                    this.skipSpace();
                    if (_jpIsIntStart(t.charAt(this.pos))) step = this.parseInt();
                }
                return _jpSlice(start, end, step);
            }
            return _jpIndex(start === null ? 0 : start);
        }
        throw this.error("expected a name in quotes, an index, a slice, '*' or a filter '?'");
    };

    BowireJsonPathParser.prototype.parseOr = function () {
        var left = this.parseAnd();
        for (;;) {
            this.skipSpace();
            if (!this.peek('||')) return left;
            this.pos += 2;
            left = (function (l, r) { return { test: function (c, root) { return l.test(c, root) || r.test(c, root); } }; })(left, this.parseAnd());
        }
    };

    BowireJsonPathParser.prototype.parseAnd = function () {
        var left = this.parseUnary();
        for (;;) {
            this.skipSpace();
            if (!this.peek('&&')) return left;
            this.pos += 2;
            left = (function (l, r) { return { test: function (c, root) { return l.test(c, root) && r.test(c, root); } }; })(left, this.parseUnary());
        }
    };

    BowireJsonPathParser.prototype.parseUnary = function () {
        this.skipSpace();
        if (this.peek('!') && !this.peek('!=')) {
            this.pos++;
            var inner = this.parseUnary();
            return { test: function (c, root) { return !inner.test(c, root); } };
        }
        if (this.peek('(')) {
            this.pos++;
            var grouped = this.parseOr();
            this.skipSpace();
            this.expect(')');
            return grouped;
        }
        var operandStart = this.pos;
        var left = this.parseOperand();
        this.skipSpace();
        var ops = ['==', '!=', '<=', '>=', '<', '>'];
        for (var i = 0; i < ops.length; i++) {
            if (!this.peek(ops[i])) continue;
            var op = ops[i];
            this.pos += op.length;
            this.skipSpace();
            var right = this.parseOperand();
            return (function (l, o, r) {
                return {
                    test: function (c, root) {
                        var a = l.evaluate(c, root), b = r.evaluate(c, root);
                        switch (o) {
                            case '==': return _jpEqual(a, b);
                            case '!=': return !_jpEqual(a, b);
                            case '<': return _jpLess(a, b);
                            case '<=': return _jpLess(a, b) || _jpEqual(a, b);
                            case '>': return _jpLess(b, a);
                            case '>=': return _jpLess(b, a) || _jpEqual(a, b);
                        }
                        return false;
                    }
                };
            })(left, op, right);
        }
        if (left.query) {
            return { test: function (c, root) { return left.matches(c, root).length > 0; } };
        }
        var err = new Error('Invalid JSONPath at position ' + operandStart + ': a literal on its own is not a test; compare it with == or !=');
        err.position = operandStart;
        throw err;
    };

    BowireJsonPathParser.prototype.parseOperand = function () {
        this.skipSpace();
        var t = this.text;
        if (this.pos >= t.length) throw this.error('expected a value or a path');
        var c = t.charAt(this.pos);
        if (c === '@' || c === '$') {
            this.pos++;
            var relative = c === '@';
            var segments = this.parseSegments();
            var matches = function (current, root) {
                var nodes = [{ value: relative ? current : root, parent: null, key: null }];
                for (var i = 0; i < segments.length; i++) {
                    var next = [];
                    for (var j = 0; j < nodes.length; j++) segments[i].apply(nodes[j], root, next);
                    nodes = next;
                }
                return nodes;
            };
            return {
                query: true,
                matches: matches,
                // A comparison takes a single node; none or several compare as Nothing.
                evaluate: function (current, root) {
                    var m = matches(current, root);
                    return m.length === 1 ? { nothing: false, value: m[0].value } : { nothing: true };
                }
            };
        }
        var literal;
        if (c === '\'' || c === '"') literal = this.parseString();
        else if (_jpIsIntStart(c)) literal = this.parseNumber();
        else if (this.peek('true')) { this.pos += 4; literal = true; }
        else if (this.peek('false')) { this.pos += 5; literal = false; }
        else if (this.peek('null')) { this.pos += 4; literal = null; }
        else throw this.error('expected a value or a path');
        return { query: false, evaluate: function () { return { nothing: false, value: literal }; } };
    };

    BowireJsonPathParser.prototype.parseNumber = function () {
        var t = this.text, start = this.pos;
        if (t.charAt(this.pos) === '-') this.pos++;
        while (this.pos < t.length && /[0-9.eE+\-]/.test(t.charAt(this.pos))) this.pos++;
        var raw = t.substring(start, this.pos);
        var n = Number(raw);
        if (raw === '' || raw === '-' || isNaN(n)) {
            var err = new Error('Invalid JSONPath at position ' + start + ": '" + raw + "' is not a number");
            err.position = start;
            throw err;
        }
        return n;
    };

    BowireJsonPathParser.prototype.parseInt = function () {
        var t = this.text, start = this.pos;
        if (t.charAt(this.pos) === '-') this.pos++;
        while (this.pos < t.length && t.charAt(this.pos) >= '0' && t.charAt(this.pos) <= '9') this.pos++;
        var raw = t.substring(start, this.pos);
        if (!/^-?[0-9]+$/.test(raw)) {
            var err = new Error('Invalid JSONPath at position ' + start + ': expected an integer');
            err.position = start;
            throw err;
        }
        return parseInt(raw, 10);
    };

    BowireJsonPathParser.prototype.parseString = function () {
        var t = this.text, quote = t.charAt(this.pos), start = this.pos, out = '';
        this.pos++;
        while (this.pos < t.length && t.charAt(this.pos) !== quote) {
            if (t.charAt(this.pos) === '\\' && this.pos + 1 < t.length) {
                this.pos++;
                var e = t.charAt(this.pos);
                out += e === 'n' ? '\n' : e === 't' ? '\t' : e === 'r' ? '\r' : e;
            } else {
                out += t.charAt(this.pos);
            }
            this.pos++;
        }
        if (this.pos >= t.length) {
            var err = new Error('Invalid JSONPath at position ' + start + ': unterminated string');
            err.position = start;
            throw err;
        }
        this.pos++;
        return out;
    };

    BowireJsonPathParser.prototype.skipSpace = function () {
        while (this.pos < this.text.length && /\s/.test(this.text.charAt(this.pos))) this.pos++;
    };

    BowireJsonPathParser.prototype.peek = function (s) {
        return this.text.substr(this.pos, s.length) === s;
    };

    BowireJsonPathParser.prototype.expect = function (c) {
        if (this.text.charAt(this.pos) === c) { this.pos++; return; }
        throw this.error("expected '" + c + "'");
    };

    BowireJsonPathParser.prototype.error = function (what) {
        return bowireJsonPathError(what, this.pos);
    };
