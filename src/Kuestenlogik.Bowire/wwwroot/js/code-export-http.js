    // ---- Code export: the HTTP family and SSE (#677) ----
    // The HTTPSnippet set, hand-written over one request description
    // (httpRequestOf in code-export.js), so every target sends the same
    // verb, URL, headers and body. Registered at load; code-export.js owns
    // the registry.
    //
    // i18n-exempt-file: every literal here is source code of a generated snippet (curl, Python, Go, …), not Bowire prose; the snippets and their comments stay in English

    function _hdrKeys(req) { return Object.keys(req.headers); }
    function _hasBody(req) { return req.body !== null; }

    registerCodeExportTarget({
        id: 'curl', label: 'curl', protocols: HTTP_FAMILY, comment: '#', rank: 10,  // i18n-exempt: the name of the tool the snippet is generated for
        generate: function (ctx) {
            var req = httpRequestOf(ctx);
            var lines = ['curl -X ' + req.verb + ' ' + codeLiteral('sh', req.url)];
            _hdrKeys(req).forEach(function (k) { lines.push('  -H ' + codeLiteral('sh', k + ': ' + req.headers[k])); });
            if (_hasBody(req)) lines.push('  -d ' + codeLiteral('sh', req.body));
            return lines.join(' \\\n');
        }
    });

    registerCodeExportTarget({
        id: 'fetch', label: 'JS fetch', protocols: HTTP_FAMILY, comment: '//', rank: 20,  // i18n-exempt: the name of the API the snippet is generated for
        generate: function (ctx) {
            var req = httpRequestOf(ctx);
            var opts = ['  method: ' + codeLiteral('js', req.verb) + ','];
            opts.push('  headers: ' + indentLines(JSON.stringify(req.headers, null, 2), '  ').trimStart() + ',');
            if (_hasBody(req)) opts.push('  body: ' + codeLiteral('js', req.body) + ',');
            return [
                'const response = await fetch(' + codeLiteral('js', req.url) + ', {',
                opts.join('\n'),
                '});',
                'console.log(response.status, await response.text());'
            ].join('\n');
        }
    });

    registerCodeExportTarget({
        id: 'axios', label: 'JS axios', protocols: HTTP_FAMILY, comment: '//', rank: 21,  // i18n-exempt: the name of the library the snippet is generated for
        generate: function (ctx) {
            var req = httpRequestOf(ctx);
            var lines = [
                "import axios from 'axios';",
                '',
                'const response = await axios.request({',
                '  method: ' + codeLiteral('js', req.verb) + ',',
                '  url: ' + codeLiteral('js', req.url) + ',',
                '  headers: ' + indentLines(JSON.stringify(req.headers, null, 2), '  ').trimStart() + ','
            ];
            if (_hasBody(req)) lines.push('  data: ' + codeLiteral('js', req.body) + ',');
            lines.push('  responseType: \'text\',');
            lines.push('});');
            lines.push('console.log(response.status, response.data);');
            return lines.join('\n');
        }
    });

    function _pyHeaders(req) {
        var keys = _hdrKeys(req);
        if (keys.length === 0) return '{}';
        return '{\n' + keys.map(function (k) {
            return '    ' + codeLiteral('py', k) + ': ' + codeLiteral('py', req.headers[k]) + ',';
        }).join('\n') + '\n}';
    }

    registerCodeExportTarget({
        id: 'python', label: 'Python (requests)', protocols: HTTP_FAMILY, comment: '#', rank: 30,  // i18n-exempt: the name of the library the snippet is generated for
        generate: function (ctx) {
            var req = httpRequestOf(ctx);
            var lines = ['import requests', '', 'url = ' + codeLiteral('py', req.url), 'headers = ' + _pyHeaders(req)];
            if (_hasBody(req)) lines.push('payload = ' + codeLiteral('py', req.body));
            lines.push('');
            lines.push('response = requests.request(' + codeLiteral('py', req.verb) + ', url, headers=headers'
                + (_hasBody(req) ? ', data=payload' : '') + ')');
            lines.push('print(response.status_code, response.text)');
            return lines.join('\n');
        }
    });

    registerCodeExportTarget({
        id: 'python-httpx', label: 'Python (httpx)', protocols: HTTP_FAMILY, comment: '#', rank: 31,  // i18n-exempt: the name of the library the snippet is generated for
        generate: function (ctx) {
            var req = httpRequestOf(ctx);
            var lines = ['import httpx', '', 'url = ' + codeLiteral('py', req.url), 'headers = ' + _pyHeaders(req)];
            if (_hasBody(req)) lines.push('payload = ' + codeLiteral('py', req.body));
            lines.push('');
            lines.push('response = httpx.request(' + codeLiteral('py', req.verb) + ', url, headers=headers'
                + (_hasBody(req) ? ', content=payload' : '') + ')');
            lines.push('print(response.status_code, response.text)');
            return lines.join('\n');
        }
    });

    function _csVerb(verb) {
        var known = { GET: 'Get', POST: 'Post', PUT: 'Put', DELETE: 'Delete', PATCH: 'Patch', HEAD: 'Head', OPTIONS: 'Options' };
        return known[verb] ? 'HttpMethod.' + known[verb] : 'new HttpMethod(' + codeLiteral('cs', verb) + ')';
    }

    registerCodeExportTarget({
        id: 'csharp-http', label: 'C# (HttpClient)', protocols: HTTP_FAMILY, comment: '//', rank: 40,  // i18n-exempt: the name of the library the snippet is generated for
        generate: function (ctx) {
            var req = httpRequestOf(ctx);
            var lines = [
                'using System.Text;',
                '',
                'using var client = new HttpClient();',
                'using var request = new HttpRequestMessage(' + _csVerb(req.verb) + ', ' + codeLiteral('cs', req.url) + ');'
            ];
            _hdrKeys(req).forEach(function (k) {
                if (k.toLowerCase() === 'content-type') return;
                lines.push('request.Headers.TryAddWithoutValidation(' + codeLiteral('cs', k) + ', ' + codeLiteral('cs', req.headers[k]) + ');');
            });
            if (_hasBody(req)) {
                lines.push('request.Content = new StringContent(' + codeLiteral('cs', req.body) + ', Encoding.UTF8, "application/json");');
            }
            lines.push('using var response = await client.SendAsync(request);');
            lines.push('Console.WriteLine($"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");');
            return lines.join('\n');
        }
    });

    registerCodeExportTarget({
        id: 'csharp-restsharp', label: 'C# (RestSharp)', protocols: HTTP_FAMILY, comment: '//', rank: 41,  // i18n-exempt: the name of the library the snippet is generated for
        generate: function (ctx) {
            var req = httpRequestOf(ctx);
            var known = { GET: 'Get', POST: 'Post', PUT: 'Put', DELETE: 'Delete', PATCH: 'Patch', HEAD: 'Head', OPTIONS: 'Options' };
            var method = known[req.verb] || 'Post';
            if (!known[req.verb]) ctx.notes.push('RestSharp has no ' + req.verb + ' method; the snippet sends POST.');
            var lines = [
                'using RestSharp;',
                '',
                'var client = new RestClient();',
                'var request = new RestRequest(' + codeLiteral('cs', req.url) + ', Method.' + method + ');'
            ];
            _hdrKeys(req).forEach(function (k) {
                if (k.toLowerCase() === 'content-type') return;
                lines.push('request.AddHeader(' + codeLiteral('cs', k) + ', ' + codeLiteral('cs', req.headers[k]) + ');');
            });
            if (_hasBody(req)) lines.push('request.AddStringBody(' + codeLiteral('cs', req.body) + ', "application/json");');
            lines.push('var response = await client.ExecuteAsync(request);');
            lines.push('Console.WriteLine($"{(int)response.StatusCode} {response.Content}");');
            return lines.join('\n');
        }
    });

    registerCodeExportTarget({
        id: 'go', label: 'Go (net/http)', protocols: HTTP_FAMILY, comment: '//', rank: 50,  // i18n-exempt: the name of the library the snippet is generated for
        generate: function (ctx) {
            var req = httpRequestOf(ctx);
            var imports = ['"fmt"', '"io"', '"net/http"'];
            if (_hasBody(req)) imports.push('"strings"');
            var lines = ['package main', '', 'import ('];
            imports.forEach(function (i) { lines.push('\t' + i); });
            lines.push(')', '', 'func main() {');
            lines.push('\treq, err := http.NewRequest(' + codeLiteral('go', req.verb) + ', ' + codeLiteral('go', req.url) + ', '
                + (_hasBody(req) ? 'strings.NewReader(' + codeLiteral('go', req.body) + ')' : 'nil') + ')');
            lines.push('\tif err != nil {', '\t\tpanic(err)', '\t}');
            _hdrKeys(req).forEach(function (k) {
                lines.push('\treq.Header.Set(' + codeLiteral('go', k) + ', ' + codeLiteral('go', req.headers[k]) + ')');
            });
            lines.push('\tresp, err := http.DefaultClient.Do(req)');
            lines.push('\tif err != nil {', '\t\tpanic(err)', '\t}');
            lines.push('\tdefer resp.Body.Close()');
            lines.push('\tbody, _ := io.ReadAll(resp.Body)');
            lines.push('\tfmt.Println(resp.Status, string(body))');
            lines.push('}');
            return lines.join('\n');
        }
    });

    registerCodeExportTarget({
        id: 'java-http', label: 'Java (HttpClient)', protocols: HTTP_FAMILY, comment: '//', rank: 60,  // i18n-exempt: the name of the library the snippet is generated for
        generate: function (ctx) {
            var req = httpRequestOf(ctx);
            var lines = [
                'import java.net.URI;',
                'import java.net.http.HttpClient;',
                'import java.net.http.HttpRequest;',
                'import java.net.http.HttpResponse;',
                '',
                'public class Main {',
                '    public static void main(String[] args) throws Exception {',
                '        HttpClient client = HttpClient.newHttpClient();',
                '        HttpRequest request = HttpRequest.newBuilder()',
                '            .uri(URI.create(' + codeLiteral('java', req.url) + '))'
            ];
            _hdrKeys(req).forEach(function (k) {
                lines.push('            .header(' + codeLiteral('java', k) + ', ' + codeLiteral('java', req.headers[k]) + ')');
            });
            lines.push('            .method(' + codeLiteral('java', req.verb) + ', '
                + (_hasBody(req) ? 'HttpRequest.BodyPublishers.ofString(' + codeLiteral('java', req.body) + ')' : 'HttpRequest.BodyPublishers.noBody()') + ')');
            lines.push('            .build();');
            lines.push('        HttpResponse<String> response = client.send(request, HttpResponse.BodyHandlers.ofString());');
            lines.push('        System.out.println(response.statusCode() + " " + response.body());');
            lines.push('    }', '}');
            return lines.join('\n');
        }
    });

    registerCodeExportTarget({
        id: 'java-okhttp', label: 'Java (OkHttp)', protocols: HTTP_FAMILY, comment: '//', rank: 61,  // i18n-exempt: the name of the library the snippet is generated for
        generate: function (ctx) {
            var req = httpRequestOf(ctx);
            var lines = [
                'import okhttp3.MediaType;',
                'import okhttp3.OkHttpClient;',
                'import okhttp3.Request;',
                'import okhttp3.RequestBody;',
                'import okhttp3.Response;',
                '',
                'public class Main {',
                '    public static void main(String[] args) throws Exception {',
                '        OkHttpClient client = new OkHttpClient();'
            ];
            if (_hasBody(req)) {
                lines.push('        RequestBody body = RequestBody.create(' + codeLiteral('java', req.body) + ', MediaType.parse("application/json"));');
            }
            lines.push('        Request request = new Request.Builder()');
            lines.push('            .url(' + codeLiteral('java', req.url) + ')');
            lines.push('            .method(' + codeLiteral('java', req.verb) + ', ' + (_hasBody(req) ? 'body' : 'null') + ')');
            _hdrKeys(req).forEach(function (k) {
                if (k.toLowerCase() === 'content-type') return;
                lines.push('            .addHeader(' + codeLiteral('java', k) + ', ' + codeLiteral('java', req.headers[k]) + ')');
            });
            lines.push('            .build();');
            lines.push('        try (Response response = client.newCall(request).execute()) {');
            lines.push('            System.out.println(response.code() + " " + response.body().string());');
            lines.push('        }', '    }', '}');
            return lines.join('\n');
        }
    });

    registerCodeExportTarget({
        id: 'kotlin-okhttp', label: 'Kotlin (OkHttp)', protocols: HTTP_FAMILY, comment: '//', rank: 62,  // i18n-exempt: the name of the library the snippet is generated for
        generate: function (ctx) {
            var req = httpRequestOf(ctx);
            var lines = [
                'import okhttp3.MediaType.Companion.toMediaType',
                'import okhttp3.OkHttpClient',
                'import okhttp3.Request',
                'import okhttp3.RequestBody.Companion.toRequestBody',
                '',
                'fun main() {',
                '    val client = OkHttpClient()'
            ];
            if (_hasBody(req)) {
                lines.push('    val body = ' + codeLiteral('kotlin', req.body) + '.toRequestBody("application/json".toMediaType())');
            }
            lines.push('    val request = Request.Builder()');
            lines.push('        .url(' + codeLiteral('kotlin', req.url) + ')');
            lines.push('        .method(' + codeLiteral('kotlin', req.verb) + ', ' + (_hasBody(req) ? 'body' : 'null') + ')');
            _hdrKeys(req).forEach(function (k) {
                if (k.toLowerCase() === 'content-type') return;
                lines.push('        .addHeader(' + codeLiteral('kotlin', k) + ', ' + codeLiteral('kotlin', req.headers[k]) + ')');
            });
            lines.push('        .build()');
            lines.push('    client.newCall(request).execute().use { response ->');
            lines.push('        println("${response.code} ${response.body?.string()}")');
            lines.push('    }', '}');
            return lines.join('\n');
        }
    });

    registerCodeExportTarget({
        id: 'php-curl', label: 'PHP (cURL)', protocols: HTTP_FAMILY, comment: '//', rank: 70,  // i18n-exempt: the name of the library the snippet is generated for
        generate: function (ctx) {
            var req = httpRequestOf(ctx);
            var lines = [
                '<?php',
                '$ch = curl_init(' + codeLiteral('php', req.url) + ');',
                'curl_setopt_array($ch, [',
                '    CURLOPT_CUSTOMREQUEST => ' + codeLiteral('php', req.verb) + ',',
                '    CURLOPT_RETURNTRANSFER => true,',
                '    CURLOPT_HTTPHEADER => ['
            ];
            _hdrKeys(req).forEach(function (k) { lines.push('        ' + codeLiteral('php', k + ': ' + req.headers[k]) + ','); });
            lines.push('    ],');
            if (_hasBody(req)) lines.push('    CURLOPT_POSTFIELDS => ' + codeLiteral('php', req.body) + ',');
            lines.push(']);');
            lines.push('$response = curl_exec($ch);');
            lines.push("echo curl_getinfo($ch, CURLINFO_HTTP_CODE) . ' ' . $response . PHP_EOL;");
            lines.push('curl_close($ch);');
            return lines.join('\n');
        }
    });

    registerCodeExportTarget({
        id: 'ruby', label: 'Ruby (net/http)', protocols: HTTP_FAMILY, comment: '#', rank: 71,  // i18n-exempt: the name of the library the snippet is generated for
        generate: function (ctx) {
            var req = httpRequestOf(ctx);
            var classes = { GET: 'Get', POST: 'Post', PUT: 'Put', DELETE: 'Delete', PATCH: 'Patch', HEAD: 'Head', OPTIONS: 'Options' };
            var lines = [
                "require 'net/http'",
                "require 'uri'",
                '',
                'uri = URI(' + codeLiteral('ruby', req.url) + ')',
                'http = Net::HTTP.new(uri.host, uri.port)',
                "http.use_ssl = uri.scheme == 'https'",
                classes[req.verb]
                    ? 'request = Net::HTTP::' + classes[req.verb] + '.new(uri)'
                    : 'request = Net::HTTPGenericRequest.new(' + codeLiteral('ruby', req.verb) + ', ' + (_hasBody(req) ? 'true' : 'false') + ', true, uri)'
            ];
            _hdrKeys(req).forEach(function (k) { lines.push('request[' + codeLiteral('ruby', k) + '] = ' + codeLiteral('ruby', req.headers[k])); });
            if (_hasBody(req)) lines.push('request.body = ' + codeLiteral('ruby', req.body));
            lines.push('response = http.request(request)');
            lines.push('puts "#{response.code} #{response.body}"');
            return lines.join('\n');
        }
    });

    registerCodeExportTarget({
        id: 'rust-reqwest', label: 'Rust (reqwest)', protocols: HTTP_FAMILY, comment: '//', rank: 72,  // i18n-exempt: the name of the library the snippet is generated for
        generate: function (ctx) {
            var req = httpRequestOf(ctx);
            var known = ['GET', 'POST', 'PUT', 'DELETE', 'PATCH', 'HEAD', 'OPTIONS'];
            var method = known.indexOf(req.verb) >= 0
                ? 'reqwest::Method::' + req.verb
                : 'reqwest::Method::from_bytes(b' + codeLiteral('rust', req.verb) + ')?';
            var lines = [
                '// Cargo.toml: reqwest = { version = "0.12", features = ["blocking"] }',
                'fn main() -> Result<(), Box<dyn std::error::Error>> {',
                '    let client = reqwest::blocking::Client::new();',
                '    let response = client',
                '        .request(' + method + ', ' + codeLiteral('rust', req.url) + ')'
            ];
            _hdrKeys(req).forEach(function (k) { lines.push('        .header(' + codeLiteral('rust', k) + ', ' + codeLiteral('rust', req.headers[k]) + ')'); });
            if (_hasBody(req)) lines.push('        .body(' + codeLiteral('rust', req.body) + ')');
            lines.push('        .send()?;');
            lines.push('    println!("{} {}", response.status(), response.text()?);');
            lines.push('    Ok(())', '}');
            return lines.join('\n');
        }
    });

    registerCodeExportTarget({
        id: 'swift', label: 'Swift (URLSession)', protocols: HTTP_FAMILY, comment: '//', rank: 73,  // i18n-exempt: the name of the library the snippet is generated for
        generate: function (ctx) {
            var req = httpRequestOf(ctx);
            var lines = [
                'import Foundation',
                '',
                'var request = URLRequest(url: URL(string: ' + codeLiteral('swift', req.url) + ')!)',
                'request.httpMethod = ' + codeLiteral('swift', req.verb)
            ];
            _hdrKeys(req).forEach(function (k) {
                lines.push('request.setValue(' + codeLiteral('swift', req.headers[k]) + ', forHTTPHeaderField: ' + codeLiteral('swift', k) + ')');
            });
            if (_hasBody(req)) lines.push('request.httpBody = ' + codeLiteral('swift', req.body) + '.data(using: .utf8)');
            lines.push('');
            lines.push('let (data, response) = try await URLSession.shared.data(for: request)');
            lines.push('print((response as? HTTPURLResponse)?.statusCode ?? 0, String(decoding: data, as: UTF8.self))');
            return lines.join('\n');
        }
    });

    registerCodeExportTarget({
        id: 'powershell', label: 'PowerShell', protocols: HTTP_FAMILY, comment: '#', rank: 80,  // i18n-exempt: the name of the shell the snippet is generated for
        generate: function (ctx) {
            var req = httpRequestOf(ctx);
            var keys = _hdrKeys(req).filter(function (k) { return k.toLowerCase() !== 'content-type'; });
            var lines = ['$headers = @{'];
            keys.forEach(function (k) { lines.push('    ' + codeLiteral('ps', k) + ' = ' + codeLiteral('ps', req.headers[k])); });
            lines.push('}');
            var call = '$response = Invoke-WebRequest -Method ' + codeLiteral('ps', req.verb) + ' -Uri ' + codeLiteral('ps', req.url) + ' -Headers $headers';
            if (_hasBody(req)) call += " -ContentType 'application/json' -Body " + codeLiteral('ps', req.body);
            lines.push(call);
            lines.push('"$($response.StatusCode) $($response.Content)"');
            return lines.join('\n');
        }
    });

    registerCodeExportTarget({
        id: 'httpie', label: 'HTTPie', protocols: HTTP_FAMILY, comment: '#', rank: 81,  // i18n-exempt: the name of the tool the snippet is generated for
        generate: function (ctx) {
            var req = httpRequestOf(ctx);
            var first = 'http' + (_hasBody(req) ? ' --raw ' + codeLiteral('sh', req.body) : '') + ' ' + req.verb + ' ' + codeLiteral('sh', req.url);
            var lines = [first];
            _hdrKeys(req).forEach(function (k) { lines.push('  ' + codeLiteral('sh', k + ':' + req.headers[k])); });
            return lines.join(' \\\n');
        }
    });

    registerCodeExportTarget({
        id: 'wget', label: 'wget', protocols: HTTP_FAMILY, comment: '#', rank: 82,  // i18n-exempt: the name of the tool the snippet is generated for
        generate: function (ctx) {
            var req = httpRequestOf(ctx);
            var lines = ['wget --quiet --output-document=- --content-on-error', '  --method=' + req.verb];
            _hdrKeys(req).forEach(function (k) { lines.push('  --header=' + codeLiteral('sh', k + ': ' + req.headers[k])); });
            if (_hasBody(req)) lines.push('  --body-data=' + codeLiteral('sh', req.body));
            lines.push('  ' + codeLiteral('sh', req.url));
            return lines.join(' \\\n');
        }
    });

    // ---- SSE ----
    registerCodeExportTarget({
        id: 'curl-sse', label: 'curl', protocols: ['sse'], comment: '#', rank: 10,  // i18n-exempt: the name of the tool the snippet is generated for
        generate: function (ctx) {
            var lines = ['curl -N ' + codeLiteral('sh', buildHttpUrl(ctx)), "  -H 'Accept: text/event-stream'"];
            Object.keys(ctx.metadata).forEach(function (k) { lines.push('  -H ' + codeLiteral('sh', k + ': ' + ctx.metadata[k])); });
            return lines.join(' \\\n');
        }
    });

    registerCodeExportTarget({
        id: 'js-eventsource', label: 'JS EventSource', protocols: ['sse'], comment: '//', rank: 20,  // i18n-exempt: the name of the API the snippet is generated for
        generate: function (ctx) {
            if (Object.keys(ctx.metadata).length > 0) {
                ctx.notes.push('EventSource cannot send headers; use fetch with a stream reader, or the Python or C# snippet, to send them.');
            }
            return [
                'const source = new EventSource(' + codeLiteral('js', buildHttpUrl(ctx)) + ');',
                'source.onmessage = (event) => {',
                '  console.log(event.data);',
                '};',
                'source.onerror = (err) => {',
                '  console.error(err);',
                '  source.close();',
                '};'
            ].join('\n');
        }
    });

    registerCodeExportTarget({
        id: 'python-sse', label: 'Python (httpx)', protocols: ['sse'], comment: '#', rank: 30,  // i18n-exempt: the name of the library the snippet is generated for
        generate: function (ctx) {
            var headers = Object.assign({ Accept: 'text/event-stream' }, ctx.metadata);
            return [
                'import httpx',
                '',
                'headers = ' + _pyHeaders({ headers: headers }),
                'with httpx.stream("GET", ' + codeLiteral('py', buildHttpUrl(ctx)) + ', headers=headers, timeout=None) as response:',
                '    for line in response.iter_lines():',
                '        if line.startswith("data:"):',
                '            print(line[5:].strip())'
            ].join('\n');
        }
    });

    registerCodeExportTarget({
        id: 'csharp-sse', label: 'C# (HttpClient)', protocols: ['sse'], comment: '//', rank: 40,  // i18n-exempt: the name of the library the snippet is generated for
        generate: function (ctx) {
            var lines = [
                'using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };',
                'using var request = new HttpRequestMessage(HttpMethod.Get, ' + codeLiteral('cs', buildHttpUrl(ctx)) + ');',
                'request.Headers.Accept.ParseAdd("text/event-stream");'
            ];
            Object.keys(ctx.metadata).forEach(function (k) {
                lines.push('request.Headers.TryAddWithoutValidation(' + codeLiteral('cs', k) + ', ' + codeLiteral('cs', ctx.metadata[k]) + ');');
            });
            lines.push('using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);');
            lines.push('using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());');
            lines.push('while (await reader.ReadLineAsync() is { } line)');
            lines.push('{');
            lines.push('    if (line.StartsWith("data:", StringComparison.Ordinal)) Console.WriteLine(line[5..].Trim());');
            lines.push('}');
            return lines.join('\n');
        }
    });

    // ---- .http and the Bowire CLI ----
    // Both are pure functions in their own fragments (http-file-export.js,
    // cli-export.js) with their own notes; guarded, because the request
    // pane calls generators outside any try/catch and a ReferenceError there
    // blanks the workbench.
    registerCodeExportTarget({
        id: 'http-file', label: '.http', protocols: ['rest', 'odata', 'graphql', 'jsonrpc'], comment: '#', rank: 900, ownNotes: true, rawContext: true,  // i18n-exempt: the name of the file format
        generate: function (ctx) {
            return (typeof generateHttpFileSnippet === 'function')
                ? generateHttpFileSnippet(ctx, { keepVars: (typeof httpExportKeepVars === 'undefined') || !!httpExportKeepVars })
                : '# .http export is unavailable in this build.';
        }
    });

    registerCodeExportTarget({
        id: 'bowire-cli', label: 'Bowire CLI', protocols: '*', comment: '#', rank: 1000, ownNotes: true, rawContext: true,  // i18n-exempt: the name of the tool
        generate: function (ctx) {
            return (typeof generateCliCommand === 'function')
                ? generateCliCommand(ctx)
                : '# Bowire CLI export is unavailable in this build.';
        }
    });
