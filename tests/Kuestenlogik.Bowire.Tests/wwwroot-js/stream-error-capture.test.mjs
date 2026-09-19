// #712 step 3 — what the recorder keeps when a stream was cut short.
//
// These are source-level assertions, and the reason is the same one
// streaming-resolution.test.mjs gives for protocols.js: api.js's
// `invokeStream` reads EventSource, config, the active tab state and the
// discovery selection out of its enclosing scope. Standing that up to drive
// a real stream would be a harness bigger than the thing under test, and
// one whose failures would be about the harness.
//
// So what is pinned here is the wiring, not the behaviour: that the recorder
// hook reads the filtered frame list rather than the raw one, and that it
// carries the run's real status rather than a literal. Both were wrong
// before, and both are the kind of wrong that a passing test suite would
// never have noticed — a recording claiming OK reads exactly like a
// recording that is OK.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const SRC = readFileSync(
    resolve(__dirname, '../../../src/Kuestenlogik.Bowire/wwwroot/js/api.js'),
    'utf8'
);

/**
 * The bowireCaptureStep({...}) call in the stream `done` handler.
 *
 * api.js has several such calls — the unary path captures too. The
 * streaming one is the only one that records a frame sequence, so
 * `receivedMessages` is what identifies it.
 */
function captureCall() {
    for (let start = SRC.indexOf('bowireCaptureStep({'); start >= 0;
         start = SRC.indexOf('bowireCaptureStep({', start + 1)) {
        let depth = 0;
        for (let i = SRC.indexOf('{', start); i < SRC.length; i++) {
            if (SRC[i] === '{') depth++;
            else if (SRC[i] === '}') {
                depth--;
                if (depth === 0) {
                    const call = SRC.slice(start, i + 1);
                    if (call.includes('receivedMessages')) return call;
                    break;
                }
            }
        }
    }
    assert.fail('no streaming bowireCaptureStep call found');
}

test('the recorded step carries the run status, not a hard-coded OK', () => {
    // `status: 'OK'` survived the first half of #712: the console entry,
    // the history row and the status bar were all corrected and this was
    // not. A recording outlives every one of them.
    const call = captureCall();
    assert.match(call, /status:\s*statusText/,
        'bowireCaptureStep must record the run\'s actual status');
    assert.doesNotMatch(call, /status:\s*'OK'/,
        'a stream that was cut short must not be recorded as OK');
});

test('the recorded step carries why the stream stopped', () => {
    const call = captureCall();
    assert.match(call, /streamError:\s*streamError\s*\|\|\s*null/,
        'the reason belongs on the step');
});

test('recorded frames come from the filtered list, not the raw one', () => {
    // The decision this guards: a frame is what the server sent. The error
    // frame is Bowire's own marker, and a mock replaying it would stage a
    // sentence no server said.
    const call = captureCall();
    assert.match(call, /receivedMessages:\s*dataFrames\.map/,
        'receivedMessages must be built from dataFrames');
    assert.doesNotMatch(call, /receivedMessages:\s*S\.streamMessages/,
        'the raw list still contains the error frame');
});

test('the step response is the last server frame, not the error marker', () => {
    // Without this the "response" of a failed stream would be the envelope
    // — which reads, to anything downstream, as the last thing the server
    // said.
    const call = captureCall();
    assert.match(call, /response:\s*dataFrames\.length\s*>\s*0/,
        'the response must come from the filtered list too');
});

test('dataFrames excludes exactly the frames carrying an error message', () => {
    // A frame with an `error` object but no message is not an error -- the
    // C# side takes the same view, so the two must not drift.
    const at = SRC.indexOf('var dataFrames = S.streamMessages.filter(');
    assert.ok(at >= 0, 'dataFrames filter not found');
    const body = SRC.slice(at, at + 200);
    assert.match(body, /!\(m && m\.error && m\.error\.message\)/,
        'the predicate must require a message, matching BowireStreamErrorEnvelope.TryRead');
});
