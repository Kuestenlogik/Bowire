    // ---- Code export: gRPC, WebSocket, SignalR and the brokers (#677) ----
    // No snippet library covers these, and they are the requests a user can
    // least work out by hand: channel and credentials, metadata, the topic
    // or subject an operation names, the QoS a publish carries. Each target
    // names its client library; generated-code targets (gRPC) say what they
    // expect from the user's .proto.
    //
    // i18n-exempt-file: every literal here is source code of a generated snippet (gRPC, MQTT, NATS clients, …), not Bowire prose; the snippets and their comments stay in English

    function _grpcTarget(ctx) {
        var url = String(ctx.serverUrl || '');
        var tls = /^https:/i.test(url) || /^grpcs:/i.test(url);
        var hostPort = stripScheme(url).split('/')[0];
        if (hostPort && hostPort.indexOf(':') < 0) hostPort += tls ? ':443' : ':80';
        return { hostPort: hostPort || 'localhost:50051', tls: tls };
    }

    function _grpcShape(ctx) {
        if (ctx.clientStreaming && ctx.serverStreaming) return 'duplex';
        if (ctx.clientStreaming) return 'client';
        if (ctx.serverStreaming) return 'server';
        return 'unary';
    }

    function _lowerFirst(s) { s = String(s || ''); return s.charAt(0).toLowerCase() + s.slice(1); }
    function _snake(s) { return String(s || '').replace(/([a-z0-9])([A-Z])/g, '$1_$2').toLowerCase(); }

    // ---- gRPC ----
    registerCodeExportTarget({
        id: 'grpcurl', label: 'grpcurl', protocols: ['grpc'], comment: '#', rank: 10,  // i18n-exempt: the name of the tool the snippet is generated for
        generate: function (ctx) {
            var target = _grpcTarget(ctx);
            var lines = ['grpcurl' + (target.tls ? '' : ' -plaintext')];
            Object.keys(ctx.metadata).forEach(function (k) { lines.push('  -H ' + codeLiteral('sh', k + ': ' + ctx.metadata[k])); });
            if (!isEmptyBody(ctx.bodyJson)) lines.push('  -d ' + codeLiteral('sh', compactJson(ctx.bodyJson)));
            lines.push('  ' + target.hostPort);
            lines.push('  ' + ctx.service + '/' + ctx.method);
            if (ctx.clientStreaming) ctx.notes.push('Client-streaming: grpcurl sends each JSON object in -d as one message.');
            return lines.join(' \\\n');
        }
    });

    registerCodeExportTarget({
        id: 'csharp-grpc', label: 'C# (Grpc.Net.Client)', protocols: ['grpc'], comment: '//', rank: 20,  // i18n-exempt: the name of the library the snippet is generated for
        generate: function (ctx) {
            var svc = lastSegment(ctx.service);
            var input = ctx.inputTypeName || (ctx.method + 'Request');
            var shape = _grpcShape(ctx);
            ctx.notes.push('Uses the client generated from your .proto (Grpc.Tools); ' + svc + 'Client and ' + input + ' come from there.');
            var lines = [
                'using Google.Protobuf;',
                'using Grpc.Core;',
                'using Grpc.Net.Client;',
                '',
                'using var channel = GrpcChannel.ForAddress(' + codeLiteral('cs', ctx.serverUrl) + ');',
                'var client = new ' + svc + '.' + svc + 'Client(channel);',
                'var headers = new Metadata();'
            ];
            Object.keys(ctx.metadata).forEach(function (k) {
                lines.push('headers.Add(' + codeLiteral('cs', k.toLowerCase()) + ', ' + codeLiteral('cs', ctx.metadata[k]) + ');');
            });
            lines.push('var request = JsonParser.Default.Parse<' + input + '>(' + codeLiteral('cs', compactJson(ctx.bodyJson)) + ');');
            if (shape === 'unary') {
                lines.push('var response = await client.' + ctx.method + 'Async(request, headers, deadline: DateTime.UtcNow.AddSeconds(30));');
                lines.push('Console.WriteLine(response);');
            } else if (shape === 'server') {
                lines.push('using var call = client.' + ctx.method + '(request, headers);');
                lines.push('await foreach (var message in call.ResponseStream.ReadAllAsync())');
                lines.push('{', '    Console.WriteLine(message);', '}');
            } else {
                lines.push('using var call = client.' + ctx.method + '(headers);');
                lines.push('await call.RequestStream.WriteAsync(request);');
                lines.push('await call.RequestStream.CompleteAsync();');
                if (shape === 'duplex') {
                    lines.push('await foreach (var message in call.ResponseStream.ReadAllAsync())');
                    lines.push('{', '    Console.WriteLine(message);', '}');
                } else {
                    lines.push('Console.WriteLine(await call);');
                }
            }
            return lines.join('\n');
        }
    });

    registerCodeExportTarget({
        id: 'go-grpc', label: 'Go (grpc-go)', protocols: ['grpc'], comment: '//', rank: 30,  // i18n-exempt: the name of the library the snippet is generated for
        generate: function (ctx) {
            var svc = lastSegment(ctx.service);
            var input = ctx.inputTypeName || (ctx.method + 'Request');
            var target = _grpcTarget(ctx);
            var shape = _grpcShape(ctx);
            ctx.notes.push('Uses the client generated from your .proto (protoc-gen-go-grpc); set the pb import to its package.');
            var lines = ['package main', '', 'import (', '\t"context"', '\t"fmt"'];
            if (shape === 'server' || shape === 'duplex') lines.push('\t"io"');
            lines.push('\t"time"', '', '\t"google.golang.org/grpc"');
            lines.push(target.tls ? '\t"google.golang.org/grpc/credentials"' : '\t"google.golang.org/grpc/credentials/insecure"');
            lines.push('\t"google.golang.org/grpc/metadata"', '\t"google.golang.org/protobuf/encoding/protojson"', '');
            lines.push('\tpb "example.com/your/module/gen" // generated from your .proto', ')', '', 'func main() {');
            lines.push('\tconn, err := grpc.NewClient(' + codeLiteral('go', target.hostPort) + ', grpc.WithTransportCredentials('
                + (target.tls ? 'credentials.NewClientTLSFromCert(nil, "")' : 'insecure.NewCredentials()') + '))');
            lines.push('\tif err != nil {', '\t\tpanic(err)', '\t}', '\tdefer conn.Close()');
            lines.push('\tclient := pb.New' + svc + 'Client(conn)', '');
            lines.push('\tctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)', '\tdefer cancel()');
            Object.keys(ctx.metadata).forEach(function (k) {
                lines.push('\tctx = metadata.AppendToOutgoingContext(ctx, ' + codeLiteral('go', k.toLowerCase()) + ', ' + codeLiteral('go', ctx.metadata[k]) + ')');
            });
            lines.push('\treq := &pb.' + input + '{}');
            lines.push('\tif err := protojson.Unmarshal([]byte(' + codeLiteral('go', compactJson(ctx.bodyJson)) + '), req); err != nil {', '\t\tpanic(err)', '\t}');
            if (shape === 'unary') {
                lines.push('\tresp, err := client.' + ctx.method + '(ctx, req)');
                lines.push('\tif err != nil {', '\t\tpanic(err)', '\t}');
                lines.push('\tfmt.Println(protojson.Format(resp))');
            } else if (shape === 'server') {
                lines.push('\tstream, err := client.' + ctx.method + '(ctx, req)');
                lines.push('\tif err != nil {', '\t\tpanic(err)', '\t}');
                lines.push('\tfor {', '\t\tmsg, err := stream.Recv()', '\t\tif err == io.EOF {', '\t\t\tbreak', '\t\t}',
                    '\t\tif err != nil {', '\t\t\tpanic(err)', '\t\t}', '\t\tfmt.Println(protojson.Format(msg))', '\t}');
            } else {
                lines.push('\tstream, err := client.' + ctx.method + '(ctx)');
                lines.push('\tif err != nil {', '\t\tpanic(err)', '\t}');
                lines.push('\tif err := stream.Send(req); err != nil {', '\t\tpanic(err)', '\t}');
                if (shape === 'duplex') {
                    lines.push('\tstream.CloseSend()');
                    lines.push('\tfor {', '\t\tmsg, err := stream.Recv()', '\t\tif err == io.EOF {', '\t\t\tbreak', '\t\t}',
                        '\t\tif err != nil {', '\t\t\tpanic(err)', '\t\t}', '\t\tfmt.Println(protojson.Format(msg))', '\t}');
                } else {
                    lines.push('\tresp, err := stream.CloseAndRecv()');
                    lines.push('\tif err != nil {', '\t\tpanic(err)', '\t}');
                    lines.push('\tfmt.Println(protojson.Format(resp))');
                }
            }
            lines.push('}');
            return lines.join('\n');
        }
    });

    registerCodeExportTarget({
        id: 'python-grpc', label: 'Python (grpcio)', protocols: ['grpc'], comment: '#', rank: 40,  // i18n-exempt: the name of the library the snippet is generated for
        generate: function (ctx) {
            var svc = lastSegment(ctx.service);
            var input = ctx.inputTypeName || (ctx.method + 'Request');
            var module = _snake(svc);
            var target = _grpcTarget(ctx);
            var shape = _grpcShape(ctx);
            ctx.notes.push('Uses the modules grpc_tools.protoc generates from your .proto; they are named after the .proto file, '
                + 'shown here as ' + module + '_pb2 / ' + module + '_pb2_grpc.');
            var meta = Object.keys(ctx.metadata).map(function (k) {
                return '(' + codeLiteral('py', k.toLowerCase()) + ', ' + codeLiteral('py', ctx.metadata[k]) + ')';
            });
            var lines = [
                'import grpc',
                'from google.protobuf import json_format',
                '',
                'import ' + module + '_pb2',
                'import ' + module + '_pb2_grpc',
                '',
                target.tls
                    ? 'channel = grpc.secure_channel(' + codeLiteral('py', target.hostPort) + ', grpc.ssl_channel_credentials())'
                    : 'channel = grpc.insecure_channel(' + codeLiteral('py', target.hostPort) + ')',
                'stub = ' + module + '_pb2_grpc.' + svc + 'Stub(channel)',
                'request = json_format.Parse(' + codeLiteral('py', compactJson(ctx.bodyJson)) + ', ' + module + '_pb2.' + input + '())',
                'metadata = [' + meta.join(', ') + ']',
                ''
            ];
            var arg = (shape === 'client' || shape === 'duplex') ? 'iter([request])' : 'request';
            if (shape === 'server' || shape === 'duplex') {
                lines.push('for response in stub.' + ctx.method + '(' + arg + ', metadata=metadata):');
                lines.push('    print(json_format.MessageToJson(response))');
            } else {
                lines.push('response = stub.' + ctx.method + '(' + arg + ', metadata=metadata, timeout=30)');
                lines.push('print(json_format.MessageToJson(response))');
            }
            return lines.join('\n');
        }
    });

    registerCodeExportTarget({
        id: 'node-grpc', label: 'Node (@grpc/grpc-js)', protocols: ['grpc'], comment: '//', rank: 50,  // i18n-exempt: the name of the library the snippet is generated for
        generate: function (ctx) {
            var target = _grpcTarget(ctx);
            var shape = _grpcShape(ctx);
            ctx.notes.push('Loads your .proto at run time with @grpc/proto-loader; set the path to it.');
            var body = parsedBody(ctx);
            var lines = [
                "import grpc from '@grpc/grpc-js';",
                "import protoLoader from '@grpc/proto-loader';",
                '',
                "const definition = protoLoader.loadSync('service.proto', { keepCase: true, longs: String, enums: String, defaults: true, oneofs: true });",
                'const proto = grpc.loadPackageDefinition(definition);',
                'const Service = ' + ['proto'].concat(String(ctx.service).split('.')).join('.') + ';',
                'const client = new Service(' + codeLiteral('js', target.hostPort) + ', '
                    + (target.tls ? 'grpc.credentials.createSsl()' : 'grpc.credentials.createInsecure()') + ');',
                '',
                'const metadata = new grpc.Metadata();'
            ];
            Object.keys(ctx.metadata).forEach(function (k) {
                lines.push('metadata.add(' + codeLiteral('js', k.toLowerCase()) + ', ' + codeLiteral('js', ctx.metadata[k]) + ');');
            });
            lines.push('const request = ' + JSON.stringify(body === undefined ? {} : body) + ';', '');
            if (shape === 'unary') {
                lines.push('client.' + ctx.method + '(request, metadata, (err, response) => {');
                lines.push('  if (err) throw err;', '  console.log(response);', '});');
            } else if (shape === 'server') {
                lines.push('const call = client.' + ctx.method + '(request, metadata);');
                lines.push("call.on('data', (message) => console.log(message));");
                lines.push("call.on('error', (err) => console.error(err));");
            } else if (shape === 'client') {
                lines.push('const call = client.' + ctx.method + '(metadata, (err, response) => {');
                lines.push('  if (err) throw err;', '  console.log(response);', '});');
                lines.push('call.write(request);', 'call.end();');
            } else {
                lines.push('const call = client.' + ctx.method + '(metadata);');
                lines.push("call.on('data', (message) => console.log(message));");
                lines.push('call.write(request);', 'call.end();');
            }
            return lines.join('\n');
        }
    });

    registerCodeExportTarget({
        id: 'java-grpc', label: 'Java (grpc-java)', protocols: ['grpc'], comment: '//', rank: 60,  // i18n-exempt: the name of the library the snippet is generated for
        generate: function (ctx) {
            var svc = lastSegment(ctx.service);
            var input = ctx.inputTypeName || (ctx.method + 'Request');
            var output = ctx.outputTypeName || (ctx.method + 'Response');
            var target = _grpcTarget(ctx);
            var shape = _grpcShape(ctx);
            ctx.notes.push('Uses the classes protoc generates from your .proto (' + svc + 'Grpc, ' + input + ').');
            if (shape === 'client' || shape === 'duplex') {
                ctx.notes.push('Client-streaming calls need the async stub and a StreamObserver; this snippet shows the channel, metadata and message.');
            }
            var lines = [
                'import com.google.protobuf.util.JsonFormat;',
                'import io.grpc.ManagedChannel;',
                'import io.grpc.ManagedChannelBuilder;',
                'import io.grpc.Metadata;',
                'import io.grpc.stub.MetadataUtils;',
                '',
                'public class Main {',
                '    public static void main(String[] args) throws Exception {',
                '        ManagedChannel channel = ManagedChannelBuilder.forTarget(' + codeLiteral('java', target.hostPort) + ')'
                    + (target.tls ? '.useTransportSecurity()' : '.usePlaintext()') + '.build();',
                '        Metadata headers = new Metadata();'
            ];
            Object.keys(ctx.metadata).forEach(function (k) {
                lines.push('        headers.put(Metadata.Key.of(' + codeLiteral('java', k.toLowerCase()) + ', Metadata.ASCII_STRING_MARSHALLER), '
                    + codeLiteral('java', ctx.metadata[k]) + ');');
            });
            lines.push('        ' + input + '.Builder request = ' + input + '.newBuilder();');
            lines.push('        JsonFormat.parser().merge(' + codeLiteral('java', compactJson(ctx.bodyJson)) + ', request);');
            lines.push('        ' + svc + 'Grpc.' + svc + 'BlockingStub stub = ' + svc + 'Grpc.newBlockingStub(channel)');
            lines.push('            .withInterceptors(MetadataUtils.newAttachHeadersInterceptor(headers));');
            if (shape === 'server') {
                lines.push('        stub.' + _lowerFirst(ctx.method) + '(request.build()).forEachRemaining(message -> {');
                lines.push('            try {', '                System.out.println(JsonFormat.printer().print(message));',
                    '            } catch (Exception e) {', '                throw new RuntimeException(e);', '            }', '        });');
            } else if (shape === 'unary') {
                lines.push('        ' + output + ' response = stub.' + _lowerFirst(ctx.method) + '(request.build());');
                lines.push('        System.out.println(JsonFormat.printer().print(response));');
            }
            lines.push('        channel.shutdown();', '    }', '}');
            return lines.join('\n');
        }
    });

    // ---- WebSocket ----
    function _wsPayload(ctx) {
        return isEmptyBody(ctx.bodyJson) ? 'hello' : compactJson(ctx.bodyJson);
    }

    registerCodeExportTarget({
        id: 'wscat', label: 'wscat', protocols: ['websocket'], comment: '#', rank: 10,  // i18n-exempt: the name of the tool the snippet is generated for
        generate: function (ctx) {
            var lines = ['# Install once: npm install -g wscat', 'wscat -c ' + codeLiteral('sh', wsUrl(ctx))];
            Object.keys(ctx.metadata).forEach(function (k) {
                lines[lines.length - 1] += ' \\\n  -H ' + codeLiteral('sh', k + ': ' + ctx.metadata[k]);
            });
            lines.push('# Then type your text frame and press Enter to send, e.g.:');
            lines.push('# > ' + _wsPayload(ctx));
            return lines.join('\n');
        }
    });

    registerCodeExportTarget({
        id: 'js-ws', label: 'JS WebSocket', protocols: ['websocket'], comment: '//', rank: 20,  // i18n-exempt: the name of the API the snippet is generated for
        generate: function (ctx) {
            if (Object.keys(ctx.metadata).length > 0) {
                ctx.notes.push('A browser WebSocket cannot send headers; the Python and C# snippets can.');
            }
            return [
                'const ws = new WebSocket(' + codeLiteral('js', wsUrl(ctx)) + ');',
                '',
                "ws.addEventListener('open', () => {",
                '  ws.send(' + codeLiteral('js', _wsPayload(ctx)) + ');',
                '});',
                '',
                "ws.addEventListener('message', (event) => {",
                '  console.log(event.data);',
                '});',
                '',
                "ws.addEventListener('close', () => console.log('disconnected'));"
            ].join('\n');
        }
    });

    registerCodeExportTarget({
        id: 'python-websockets', label: 'Python (websockets)', protocols: ['websocket'], comment: '#', rank: 30,  // i18n-exempt: the name of the library the snippet is generated for
        generate: function (ctx) {
            var headers = Object.keys(ctx.metadata).map(function (k) {
                return codeLiteral('py', k) + ': ' + codeLiteral('py', ctx.metadata[k]);
            });
            return [
                'import asyncio',
                '',
                'import websockets',
                '',
                '',
                'async def main():',
                '    async with websockets.connect(' + codeLiteral('py', wsUrl(ctx)) + ', additional_headers={' + headers.join(', ') + '}) as ws:',
                '        await ws.send(' + codeLiteral('py', _wsPayload(ctx)) + ')',
                '        async for message in ws:',
                '            print(message)',
                '',
                '',
                'asyncio.run(main())'
            ].join('\n');
        }
    });

    registerCodeExportTarget({
        id: 'csharp-ws', label: 'C# (ClientWebSocket)', protocols: ['websocket'], comment: '//', rank: 40,  // i18n-exempt: the name of the library the snippet is generated for
        generate: function (ctx) {
            var lines = ['using System.Net.WebSockets;', 'using System.Text;', '', 'using var ws = new ClientWebSocket();'];
            Object.keys(ctx.metadata).forEach(function (k) {
                lines.push('ws.Options.SetRequestHeader(' + codeLiteral('cs', k) + ', ' + codeLiteral('cs', ctx.metadata[k]) + ');');
            });
            lines.push('await ws.ConnectAsync(new Uri(' + codeLiteral('cs', wsUrl(ctx)) + '), CancellationToken.None);');
            lines.push('await ws.SendAsync(Encoding.UTF8.GetBytes(' + codeLiteral('cs', _wsPayload(ctx)) + '), WebSocketMessageType.Text, true, CancellationToken.None);');
            lines.push('var buffer = new byte[64 * 1024];');
            lines.push('while (ws.State == WebSocketState.Open)');
            lines.push('{');
            lines.push('    var result = await ws.ReceiveAsync(buffer, CancellationToken.None);');
            lines.push('    if (result.MessageType == WebSocketMessageType.Close) break;');
            lines.push('    Console.WriteLine(Encoding.UTF8.GetString(buffer, 0, result.Count));');
            lines.push('}');
            return lines.join('\n');
        }
    });

    // ---- SignalR ----
    // The plugin unfolds a body whose properties are the hub method's
    // parameters into positional arguments; anything else is one argument.
    function _signalrArgs(ctx) {
        if (isEmptyBody(ctx.bodyJson)) return [];
        var body = parsedBody(ctx);
        if (body === undefined) return [ctx.bodyJson];
        var names = ctx.inputFieldNames || [];
        if (body && typeof body === 'object' && !Array.isArray(body)) {
            var keys = Object.keys(body);
            if (names.length > 0 && keys.length === names.length && keys.every(function (k) { return names.indexOf(k) >= 0; })) {
                return names.map(function (n) { return body[n]; });
            }
        }
        return [body];
    }

    registerCodeExportTarget({
        id: 'csharp-signalr', label: 'C# (HubConnection)', protocols: ['signalr'], comment: '//', rank: 10,  // i18n-exempt: the name of the library the snippet is generated for
        generate: function (ctx) {
            var args = _signalrArgs(ctx).map(function (a) {
                return 'System.Text.Json.JsonDocument.Parse(' + codeLiteral('cs', JSON.stringify(a)) + ').RootElement';
            });
            var lines = ['using Microsoft.AspNetCore.SignalR.Client;', '', 'var connection = new HubConnectionBuilder()'];
            var headerKeys = Object.keys(ctx.metadata);
            if (headerKeys.length > 0) {
                lines.push('    .WithUrl(' + codeLiteral('cs', ctx.serverUrl) + ', options =>');
                lines.push('    {');
                headerKeys.forEach(function (k) { lines.push('        options.Headers[' + codeLiteral('cs', k) + '] = ' + codeLiteral('cs', ctx.metadata[k]) + ';'); });
                lines.push('    })');
            } else {
                lines.push('    .WithUrl(' + codeLiteral('cs', ctx.serverUrl) + ')');
            }
            lines.push('    .Build();', '', 'await connection.StartAsync();');
            if (ctx.serverStreaming) {
                lines.push('await foreach (var item in connection.StreamAsync<object>(' + codeLiteral('cs', ctx.method) + (args.length ? ', ' + args.join(', ') : '') + '))');
                lines.push('{', '    Console.WriteLine(item);', '}');
            } else {
                lines.push('var result = await connection.InvokeCoreAsync<object>(' + codeLiteral('cs', ctx.method) + ', new object[] { ' + args.join(', ') + ' });');
                lines.push('Console.WriteLine(result);');
            }
            return lines.join('\n');
        }
    });

    registerCodeExportTarget({
        id: 'js-signalr', label: 'JS (@microsoft/signalr)', protocols: ['signalr'], comment: '//', rank: 20,  // i18n-exempt: the name of the library the snippet is generated for
        generate: function (ctx) {
            var args = _signalrArgs(ctx).map(function (a) { return JSON.stringify(a); });
            var headerKeys = Object.keys(ctx.metadata);
            var lines = ["import * as signalR from '@microsoft/signalr';", '', 'const connection = new signalR.HubConnectionBuilder()'];
            if (headerKeys.length > 0) {
                lines.push('  .withUrl(' + codeLiteral('js', ctx.serverUrl) + ', { headers: ' + JSON.stringify(ctx.metadata) + ' })');
            } else {
                lines.push('  .withUrl(' + codeLiteral('js', ctx.serverUrl) + ')');
            }
            lines.push('  .build();', '', 'await connection.start();');
            var call = codeLiteral('js', ctx.method) + (args.length ? ', ' + args.join(', ') : '');
            if (ctx.serverStreaming) {
                lines.push('connection.stream(' + call + ').subscribe({');
                lines.push('  next: (item) => console.log(item),', '  error: (err) => console.error(err),', '  complete: () => connection.stop(),', '});');
            } else {
                lines.push('const result = await connection.invoke(' + call + ');');
                lines.push('console.log(result);');
            }
            return lines.join('\n');
        }
    });

    registerCodeExportTarget({
        id: 'java-signalr', label: 'Java (signalr)', protocols: ['signalr'], comment: '//', rank: 30,  // i18n-exempt: the name of the library the snippet is generated for
        generate: function (ctx) {
            var args = _signalrArgs(ctx).map(function (a) {
                return 'com.google.gson.JsonParser.parseString(' + codeLiteral('java', JSON.stringify(a)) + ')';
            });
            var lines = [
                'import com.microsoft.signalr.HubConnection;',
                'import com.microsoft.signalr.HubConnectionBuilder;',
                '',
                'public class Main {',
                '    public static void main(String[] args) {',
                '        HubConnection connection = HubConnectionBuilder.create(' + codeLiteral('java', ctx.serverUrl) + ')'
            ];
            Object.keys(ctx.metadata).forEach(function (k) {
                lines.push('            .withHeader(' + codeLiteral('java', k) + ', ' + codeLiteral('java', ctx.metadata[k]) + ')');
            });
            lines.push('            .build();');
            lines.push('        connection.start().blockingAwait();');
            var call = codeLiteral('java', ctx.method) + (args.length ? ', ' + args.join(', ') : '');
            if (ctx.serverStreaming) {
                lines.push('        connection.stream(Object.class, ' + call + ').blockingForEach(System.out::println);');
            } else {
                lines.push('        Object result = connection.invoke(Object.class, ' + call + ').blockingGet();');
                lines.push('        System.out.println(result);');
            }
            lines.push('        connection.stop().blockingAwait();', '    }', '}');
            return lines.join('\n');
        }
    });

    // ---- MQTT ----
    // The method names a topic (#664: mqtt/<topic>/publish|subscribe); the
    // publish body carries payload, qos and retain.
    function _mqttOp(ctx) {
        var full = ctx.methodFullName || '';
        var topic = ctx.method || '';
        var m = /^mqtt\/(.+)\/(publish|subscribe)$/.exec(full) || /^mqtt\/(.+)\/(publish|subscribe)$/.exec(topic);
        var op = m ? m[2] : (ctx.serverStreaming ? 'subscribe' : 'publish');
        if (m) topic = m[1];
        var body = parsedBody(ctx) || {};
        if (typeof body.topic === 'string' && body.topic) topic = body.topic;
        var payload = body.payload === undefined ? '' : (typeof body.payload === 'string' ? body.payload : JSON.stringify(body.payload));
        var qos = Number(body.qos) || 0;
        return { op: op, topic: topic, payload: payload, qos: qos, retain: !!body.retain, broker: brokerHostPort(ctx.serverUrl, 1883) };
    }

    registerCodeExportTarget({
        id: 'mosquitto', label: 'mosquitto_pub / _sub', protocols: ['mqtt'], comment: '#', rank: 10,  // i18n-exempt: the name of the tool the snippet is generated for
        generate: function (ctx) {
            var o = _mqttOp(ctx);
            var common = '-h ' + codeLiteral('sh', o.broker.host) + ' -p ' + o.broker.port + ' -t ' + codeLiteral('sh', o.topic) + ' -q ' + o.qos;
            if (o.op === 'subscribe') return 'mosquitto_sub ' + common + ' -v';
            return 'mosquitto_pub ' + common + (o.retain ? ' -r' : '') + ' -m ' + codeLiteral('sh', o.payload);
        }
    });

    registerCodeExportTarget({
        id: 'python-paho', label: 'Python (paho-mqtt)', protocols: ['mqtt'], comment: '#', rank: 20,  // i18n-exempt: the name of the library the snippet is generated for
        generate: function (ctx) {
            var o = _mqttOp(ctx);
            var lines = ['import paho.mqtt.client as mqtt', ''];
            if (o.op === 'subscribe') {
                lines.push('', 'def on_message(client, userdata, message):', '    print(message.topic, message.payload.decode())', '', '');
                lines.push('client = mqtt.Client(mqtt.CallbackAPIVersion.VERSION2)');
                lines.push('client.on_message = on_message');
                lines.push('client.connect(' + codeLiteral('py', o.broker.host) + ', ' + o.broker.port + ')');
                lines.push('client.subscribe(' + codeLiteral('py', o.topic) + ', qos=' + o.qos + ')');
                lines.push('client.loop_forever()');
            } else {
                lines.push('client = mqtt.Client(mqtt.CallbackAPIVersion.VERSION2)');
                lines.push('client.connect(' + codeLiteral('py', o.broker.host) + ', ' + o.broker.port + ')');
                lines.push('client.loop_start()');
                lines.push('info = client.publish(' + codeLiteral('py', o.topic) + ', ' + codeLiteral('py', o.payload) + ', qos=' + o.qos + ', retain=' + (o.retain ? 'True' : 'False') + ')');
                lines.push('info.wait_for_publish()');
                lines.push('client.loop_stop()');
                lines.push('client.disconnect()');
            }
            return lines.join('\n');
        }
    });

    registerCodeExportTarget({
        id: 'node-mqtt', label: 'Node (mqtt)', protocols: ['mqtt'], comment: '//', rank: 30,  // i18n-exempt: the name of the library the snippet is generated for
        generate: function (ctx) {
            var o = _mqttOp(ctx);
            var lines = ["import mqtt from 'mqtt';", '',
                'const client = mqtt.connect(' + codeLiteral('js', 'mqtt://' + o.broker.host + ':' + o.broker.port) + ');'];
            if (o.op === 'subscribe') {
                lines.push("client.on('connect', () => client.subscribe(" + codeLiteral('js', o.topic) + ', { qos: ' + o.qos + ' }));');
                lines.push("client.on('message', (topic, payload) => console.log(topic, payload.toString()));");
            } else {
                lines.push("client.on('connect', () => {");
                lines.push('  client.publish(' + codeLiteral('js', o.topic) + ', ' + codeLiteral('js', o.payload) + ', { qos: ' + o.qos + ', retain: ' + o.retain + ' }, (err) => {');
                lines.push('    if (err) console.error(err);', '    client.end();', '  });', '});');
            }
            return lines.join('\n');
        }
    });

    registerCodeExportTarget({
        id: 'csharp-mqttnet', label: 'C# (MQTTnet)', protocols: ['mqtt'], comment: '//', rank: 40,  // i18n-exempt: the name of the library the snippet is generated for
        generate: function (ctx) {
            var o = _mqttOp(ctx);
            var qos = ['AtMostOnce', 'AtLeastOnce', 'ExactlyOnce'][o.qos] || 'AtMostOnce';
            var lines = [
                'using MQTTnet;',
                'using MQTTnet.Protocol;',
                '',
                'using var client = new MqttClientFactory().CreateMqttClient();',
                'await client.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer(' + codeLiteral('cs', o.broker.host) + ', ' + o.broker.port + ').Build());'
            ];
            if (o.op === 'subscribe') {
                lines.push('client.ApplicationMessageReceivedAsync += e =>');
                lines.push('{');
                lines.push('    Console.WriteLine($"{e.ApplicationMessage.Topic} {e.ApplicationMessage.ConvertPayloadToString()}");');
                lines.push('    return Task.CompletedTask;');
                lines.push('};');
                lines.push('await client.SubscribeAsync(' + codeLiteral('cs', o.topic) + ', MqttQualityOfServiceLevel.' + qos + ');');
                lines.push('await Task.Delay(Timeout.Infinite);');
            } else {
                lines.push('await client.PublishAsync(new MqttApplicationMessageBuilder()');
                lines.push('    .WithTopic(' + codeLiteral('cs', o.topic) + ')');
                lines.push('    .WithPayload(' + codeLiteral('cs', o.payload) + ')');
                lines.push('    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.' + qos + ')');
                lines.push('    .WithRetainFlag(' + o.retain + ')');
                lines.push('    .Build());');
                lines.push('await client.DisconnectAsync();');
            }
            return lines.join('\n');
        }
    });

    // ---- NATS ----
    // nats/<subject>/publish|subscribe|request, or a JetStream publish
    // (nats/jetstream/<stream>/publish/<subject>).
    function _natsOp(ctx) {
        var full = ctx.methodFullName || '';
        var name = ctx.method || '';
        var subject = name, op = ctx.serverStreaming ? 'subscribe' : 'publish', jetstream = false;
        var js = /^nats\/jetstream\/[^/]+\/publish\/(.+)$/.exec(full);
        var core = /^nats\/(.+)\/(publish|subscribe|request)$/.exec(full) || /^(.+)\/(publish|subscribe|request)$/.exec(name);
        if (js) { subject = js[1]; op = 'publish'; jetstream = true; }
        else if (core) { subject = core[1]; op = core[2]; }
        var body = parsedBody(ctx) || {};
        if (typeof body.subject === 'string' && body.subject) subject = body.subject;
        var payload = body.payload === undefined ? '' : (typeof body.payload === 'string' ? body.payload : JSON.stringify(body.payload));
        var url = String(ctx.serverUrl || '');
        if (!/^[a-z]+:\/\//i.test(url)) url = 'nats://' + (url || 'localhost:4222');
        return { op: op, subject: subject, payload: payload, jetstream: jetstream, url: url };
    }

    registerCodeExportTarget({
        id: 'nats-cli', label: 'nats CLI', protocols: ['nats'], comment: '#', rank: 10,  // i18n-exempt: the name of the tool the snippet is generated for
        generate: function (ctx) {
            var o = _natsOp(ctx);
            var server = ' --server ' + codeLiteral('sh', o.url);
            if (o.op === 'subscribe') return 'nats sub ' + codeLiteral('sh', o.subject) + server;
            if (o.op === 'request') return 'nats request ' + codeLiteral('sh', o.subject) + ' ' + codeLiteral('sh', o.payload) + server;
            if (o.jetstream) return 'nats pub ' + codeLiteral('sh', o.subject) + ' ' + codeLiteral('sh', o.payload) + ' --jetstream' + server;
            return 'nats pub ' + codeLiteral('sh', o.subject) + ' ' + codeLiteral('sh', o.payload) + server;
        }
    });

    registerCodeExportTarget({
        id: 'python-nats', label: 'Python (nats-py)', protocols: ['nats'], comment: '#', rank: 20,  // i18n-exempt: the name of the library the snippet is generated for
        generate: function (ctx) {
            var o = _natsOp(ctx);
            var lines = ['import asyncio', '', 'import nats', '', '', 'async def main():', '    nc = await nats.connect(' + codeLiteral('py', o.url) + ')'];
            if (o.op === 'subscribe') {
                lines.push('    sub = await nc.subscribe(' + codeLiteral('py', o.subject) + ')');
                lines.push('    async for msg in sub.messages:');
                lines.push('        print(msg.subject, msg.data.decode())');
            } else if (o.op === 'request') {
                lines.push('    msg = await nc.request(' + codeLiteral('py', o.subject) + ', ' + codeLiteral('py', o.payload) + '.encode(), timeout=5)');
                lines.push('    print(msg.data.decode())');
            } else if (o.jetstream) {
                lines.push('    js = nc.jetstream()');
                lines.push('    ack = await js.publish(' + codeLiteral('py', o.subject) + ', ' + codeLiteral('py', o.payload) + '.encode())');
                lines.push('    print(ack.stream, ack.seq)');
            } else {
                lines.push('    await nc.publish(' + codeLiteral('py', o.subject) + ', ' + codeLiteral('py', o.payload) + '.encode())');
            }
            lines.push('    await nc.drain()', '', '', 'asyncio.run(main())');
            return lines.join('\n');
        }
    });

    registerCodeExportTarget({
        id: 'node-nats', label: 'Node (nats)', protocols: ['nats'], comment: '//', rank: 30,  // i18n-exempt: the name of the library the snippet is generated for
        generate: function (ctx) {
            var o = _natsOp(ctx);
            var lines = ["import { connect, StringCodec } from 'nats';", '',
                'const nc = await connect({ servers: ' + codeLiteral('js', o.url) + ' });', 'const sc = StringCodec();'];
            if (o.op === 'subscribe') {
                lines.push('for await (const msg of nc.subscribe(' + codeLiteral('js', o.subject) + ')) {');
                lines.push('  console.log(msg.subject, sc.decode(msg.data));', '}');
            } else if (o.op === 'request') {
                lines.push('const reply = await nc.request(' + codeLiteral('js', o.subject) + ', sc.encode(' + codeLiteral('js', o.payload) + '), { timeout: 5000 });');
                lines.push('console.log(sc.decode(reply.data));');
            } else if (o.jetstream) {
                lines.push('const ack = await nc.jetstream().publish(' + codeLiteral('js', o.subject) + ', sc.encode(' + codeLiteral('js', o.payload) + '));');
                lines.push('console.log(ack.stream, ack.seq);');
            } else {
                lines.push('nc.publish(' + codeLiteral('js', o.subject) + ', sc.encode(' + codeLiteral('js', o.payload) + '));');
            }
            lines.push('await nc.drain();');
            return lines.join('\n');
        }
    });

    registerCodeExportTarget({
        id: 'go-nats', label: 'Go (nats.go)', protocols: ['nats'], comment: '//', rank: 40,  // i18n-exempt: the name of the library the snippet is generated for
        generate: function (ctx) {
            var o = _natsOp(ctx);
            var lines = ['package main', '', 'import ('];
            if (o.op !== 'publish' || o.jetstream) lines.push('\t"fmt"');
            if (o.op === 'request' || o.op === 'subscribe') lines.push('\t"time"');
            lines.push('', '\t"github.com/nats-io/nats.go"', ')', '', 'func main() {');
            lines.push('\tnc, err := nats.Connect(' + codeLiteral('go', o.url) + ')', '\tif err != nil {', '\t\tpanic(err)', '\t}', '\tdefer nc.Drain()');
            if (o.op === 'subscribe') {
                lines.push('\tsub, err := nc.SubscribeSync(' + codeLiteral('go', o.subject) + ')', '\tif err != nil {', '\t\tpanic(err)', '\t}');
                lines.push('\tfor {', '\t\tmsg, err := sub.NextMsg(time.Hour)', '\t\tif err != nil {', '\t\t\tpanic(err)', '\t\t}',
                    '\t\tfmt.Println(msg.Subject, string(msg.Data))', '\t}');
            } else if (o.op === 'request') {
                lines.push('\tmsg, err := nc.Request(' + codeLiteral('go', o.subject) + ', []byte(' + codeLiteral('go', o.payload) + '), 5*time.Second)');
                lines.push('\tif err != nil {', '\t\tpanic(err)', '\t}', '\tfmt.Println(string(msg.Data))');
            } else if (o.jetstream) {
                lines.push('\tjs, err := nc.JetStream()', '\tif err != nil {', '\t\tpanic(err)', '\t}');
                lines.push('\tack, err := js.Publish(' + codeLiteral('go', o.subject) + ', []byte(' + codeLiteral('go', o.payload) + '))');
                lines.push('\tif err != nil {', '\t\tpanic(err)', '\t}', '\tfmt.Println(ack.Stream, ack.Sequence)');
            } else {
                lines.push('\tif err := nc.Publish(' + codeLiteral('go', o.subject) + ', []byte(' + codeLiteral('go', o.payload) + ')); err != nil {', '\t\tpanic(err)', '\t}');
            }
            lines.push('}');
            return lines.join('\n');
        }
    });

    registerCodeExportTarget({
        id: 'csharp-nats', label: 'C# (NATS.Net)', protocols: ['nats'], comment: '//', rank: 50,  // i18n-exempt: the name of the library the snippet is generated for
        generate: function (ctx) {
            var o = _natsOp(ctx);
            var lines = ['using NATS.Client.Core;'];
            if (o.jetstream) lines.push('using NATS.Client.JetStream;');
            lines.push('', 'await using var nats = new NatsConnection(new NatsOpts { Url = ' + codeLiteral('cs', o.url) + ' });');
            if (o.op === 'subscribe') {
                lines.push('await foreach (var msg in nats.SubscribeAsync<string>(' + codeLiteral('cs', o.subject) + '))');
                lines.push('{', '    Console.WriteLine($"{msg.Subject} {msg.Data}");', '}');
            } else if (o.op === 'request') {
                lines.push('var reply = await nats.RequestAsync<string, string>(' + codeLiteral('cs', o.subject) + ', ' + codeLiteral('cs', o.payload) + ');');
                lines.push('Console.WriteLine(reply.Data);');
            } else if (o.jetstream) {
                lines.push('var js = new NatsJSContext(nats);');
                lines.push('var ack = await js.PublishAsync(' + codeLiteral('cs', o.subject) + ', ' + codeLiteral('cs', o.payload) + ');');
                lines.push('Console.WriteLine($"{ack.Stream} {ack.Seq}");');
            } else {
                lines.push('await nats.PublishAsync(' + codeLiteral('cs', o.subject) + ', ' + codeLiteral('cs', o.payload) + ');');
            }
            return lines.join('\n');
        }
    });

    // ---- Socket.IO ----
    // A listen method names its event (socketio/<event>/listen, or
    // socketio/listen with the event in the body); an emit sends
    // {event, data}.
    function _socketIoOp(ctx) {
        var full = ctx.methodFullName || '';
        var body = parsedBody(ctx) || {};
        var listen = ctx.serverStreaming || /\/listen$/.test(full);
        var m = /^socketio\/(.+)\/listen$/.exec(full);
        var event = m ? m[1] : (typeof body.event === 'string' && body.event ? body.event : 'message');
        var data = body.data === undefined ? null : body.data;
        if (typeof data === 'string') {
            try { data = JSON.parse(data); } catch (_) { /* a plain string stays one */ }
        }
        return { listen: listen, event: event, data: data };
    }

    registerCodeExportTarget({
        id: 'node-socketio', label: 'Node (socket.io-client)', protocols: ['socketio'], comment: '//', rank: 10,  // i18n-exempt: the name of the library the snippet is generated for
        generate: function (ctx) {
            var o = _socketIoOp(ctx);
            var opts = Object.keys(ctx.metadata).length > 0 ? ', { extraHeaders: ' + JSON.stringify(ctx.metadata) + ' }' : '';
            var lines = ["import { io } from 'socket.io-client';", '', 'const socket = io(' + codeLiteral('js', ctx.serverUrl) + opts + ');'];
            if (o.listen) {
                lines.push('socket.on(' + codeLiteral('js', o.event) + ', (...args) => console.log(...args));');
            } else {
                lines.push("socket.on('connect', () => {");
                lines.push('  socket.emit(' + codeLiteral('js', o.event) + (o.data === null ? '' : ', ' + JSON.stringify(o.data)) + ');');
                lines.push('});');
            }
            return lines.join('\n');
        }
    });

    registerCodeExportTarget({
        id: 'python-socketio', label: 'Python (python-socketio)', protocols: ['socketio'], comment: '#', rank: 20,  // i18n-exempt: the name of the library the snippet is generated for
        generate: function (ctx) {
            var o = _socketIoOp(ctx);
            var headers = Object.keys(ctx.metadata).map(function (k) { return codeLiteral('py', k) + ': ' + codeLiteral('py', ctx.metadata[k]); });
            var lines = ['import json', '', 'import socketio', '', 'sio = socketio.Client()', '', ''];
            if (o.listen) {
                lines.push('@sio.on(' + codeLiteral('py', o.event) + ')', 'def on_event(*args):', '    print(*args)');
            } else {
                lines.push('@sio.event', 'def connect():');
                lines.push('    sio.emit(' + codeLiteral('py', o.event) + (o.data === null ? '' : ', json.loads(' + codeLiteral('py', JSON.stringify(o.data)) + ')') + ')');
            }
            lines.push('', '', 'sio.connect(' + codeLiteral('py', ctx.serverUrl) + ', headers={' + headers.join(', ') + '})');
            lines.push('sio.wait()');
            return lines.join('\n');
        }
    });

    registerCodeExportTarget({
        id: 'csharp-socketio', label: 'C# (SocketIOClient)', protocols: ['socketio'], comment: '//', rank: 30,  // i18n-exempt: the name of the library the snippet is generated for
        generate: function (ctx) {
            var o = _socketIoOp(ctx);
            var lines = ['using SocketIOClient;', '', 'var client = new SocketIOClient.SocketIO(new Uri(' + codeLiteral('cs', ctx.serverUrl) + '));'];
            var keys = Object.keys(ctx.metadata);
            if (keys.length > 0) {
                lines.push('client.Options.ExtraHeaders = new Dictionary<string, string>');
                lines.push('{');
                keys.forEach(function (k) { lines.push('    [' + codeLiteral('cs', k) + '] = ' + codeLiteral('cs', ctx.metadata[k]) + ','); });
                lines.push('};');
            }
            if (o.listen) {
                lines.push('client.On(' + codeLiteral('cs', o.event) + ', response => Console.WriteLine(response));');
                lines.push('await client.ConnectAsync();');
                lines.push('await Task.Delay(Timeout.Infinite);');
            } else {
                lines.push('await client.ConnectAsync();');
                lines.push('await client.EmitAsync(' + codeLiteral('cs', o.event)
                    + (o.data === null ? '' : ', System.Text.Json.JsonDocument.Parse(' + codeLiteral('cs', JSON.stringify(o.data)) + ').RootElement') + ');');
            }
            return lines.join('\n');
        }
    });

    // ---- Pulsar ----
    // pulsar/topic/<topic>/produce|subscribe, or produce/subscribe with the
    // topic on the service.
    function _pulsarOp(ctx) {
        var full = ctx.methodFullName || '';
        var m = /^pulsar\/topic\/(.+)\/(produce|subscribe)$/.exec(full);
        var op = m ? m[2] : (ctx.serverStreaming ? 'subscribe' : 'produce');
        var topic = m ? m[1] : (ctx.service || '');
        var body = parsedBody(ctx) || {};
        var payload = body.payload === undefined ? '' : (typeof body.payload === 'string' ? body.payload : JSON.stringify(body.payload));
        var url = String(ctx.serverUrl || '');
        if (/^https?:/i.test(url)) {
            ctx.notes.push('Clients connect to the broker service URL (pulsar://host:6650), not the admin URL; adjust it if they differ.');
            url = 'pulsar://' + brokerHostPort(url, 6650).host + ':6650';
        }
        return { op: op, topic: topic, payload: payload, url: url || 'pulsar://localhost:6650' };
    }

    registerCodeExportTarget({
        id: 'pulsar-client', label: 'pulsar-client CLI', protocols: ['pulsar'], comment: '#', rank: 10,  // i18n-exempt: the name of the tool the snippet is generated for
        generate: function (ctx) {
            var o = _pulsarOp(ctx);
            var base = 'pulsar-client --url ' + codeLiteral('sh', o.url);
            if (o.op === 'subscribe') return base + ' consume ' + codeLiteral('sh', o.topic) + ' -s bowire-sub -n 0';
            return base + ' produce ' + codeLiteral('sh', o.topic) + ' -m ' + codeLiteral('sh', o.payload);
        }
    });

    registerCodeExportTarget({
        id: 'python-pulsar', label: 'Python (pulsar-client)', protocols: ['pulsar'], comment: '#', rank: 20,  // i18n-exempt: the name of the library the snippet is generated for
        generate: function (ctx) {
            var o = _pulsarOp(ctx);
            var lines = ['import pulsar', '', 'client = pulsar.Client(' + codeLiteral('py', o.url) + ')'];
            if (o.op === 'subscribe') {
                lines.push('consumer = client.subscribe(' + codeLiteral('py', o.topic) + ', "bowire-sub")');
                lines.push('while True:', '    msg = consumer.receive()', '    print(msg.data().decode())', '    consumer.acknowledge(msg)');
            } else {
                lines.push('producer = client.create_producer(' + codeLiteral('py', o.topic) + ')');
                lines.push('producer.send(' + codeLiteral('py', o.payload) + '.encode())');
                lines.push('client.close()');
            }
            return lines.join('\n');
        }
    });

    registerCodeExportTarget({
        id: 'java-pulsar', label: 'Java (pulsar-client)', protocols: ['pulsar'], comment: '//', rank: 30,  // i18n-exempt: the name of the library the snippet is generated for
        generate: function (ctx) {
            var o = _pulsarOp(ctx);
            var lines = [
                'import org.apache.pulsar.client.api.*;',
                '',
                'public class Main {',
                '    public static void main(String[] args) throws Exception {',
                '        PulsarClient client = PulsarClient.builder().serviceUrl(' + codeLiteral('java', o.url) + ').build();'
            ];
            if (o.op === 'subscribe') {
                lines.push('        Consumer<String> consumer = client.newConsumer(Schema.STRING)');
                lines.push('            .topic(' + codeLiteral('java', o.topic) + ')');
                lines.push('            .subscriptionName("bowire-sub")');
                lines.push('            .subscribe();');
                lines.push('        while (true) {', '            Message<String> msg = consumer.receive();',
                    '            System.out.println(msg.getValue());', '            consumer.acknowledge(msg);', '        }');
            } else {
                lines.push('        Producer<String> producer = client.newProducer(Schema.STRING).topic(' + codeLiteral('java', o.topic) + ').create();');
                lines.push('        producer.send(' + codeLiteral('java', o.payload) + ');');
                lines.push('        client.close();');
            }
            lines.push('    }', '}');
            return lines.join('\n');
        }
    });
