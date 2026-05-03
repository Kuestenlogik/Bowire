// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

global using Xunit;
global using Kuestenlogik.Bowire.Mock;
global using Kuestenlogik.Bowire.Mock.Loading;
global using Kuestenlogik.Bowire.Mock.Matchers;
global using Kuestenlogik.Bowire.Mocking;
global using Kuestenlogik.Bowire.PluginLoading;
// Schema-only + transport-host adapters moved out of
// Kuestenlogik.Bowire.Mock.* into the matching protocol plugins during the
// mock plugin-isation refactor.
global using Kuestenlogik.Bowire.Protocol.Mqtt.Mock;
global using Kuestenlogik.Bowire.Protocol.Rest.Mock;
global using Kuestenlogik.Bowire.Protocol.Grpc.Mock;
global using Kuestenlogik.Bowire.Protocol.GraphQL.Mock;
