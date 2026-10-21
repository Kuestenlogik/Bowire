// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Protocol.Rest;

namespace Kuestenlogik.Bowire.Protocol.Rest.Tests;

/// <summary>
/// The canonical query string AWS Signature Version 4 signs. AWS computes
/// the same string on its side; any difference in order is a different
/// signature and a 403.
/// </summary>
/// <remarks>
/// Vectors from the AWS SigV4 test suite (<c>aws-sig-v4-test-suite</c>):
/// <c>get-vanilla-query-order-key</c> and
/// <c>get-vanilla-query-order-value</c>. The rule they pin, from
/// https://docs.aws.amazon.com/IAM/latest/UserGuide/create-signed-request.html:
/// "Sort the parameters by parameter name ... Parameters with duplicate
/// names should be sorted by value."
/// </remarks>
public sealed class AwsSigV4CanonicalQueryTests
{
    [Theory]
    // get-vanilla-query-order-key
    [InlineData("?Param2=value2&Param1=value1", "Param1=value1&Param2=value2")]
    // get-vanilla-query-order-value: code-point order, so "V" before "v"
    [InlineData("?Param1=value2&Param1=Value1", "Param1=Value1&Param1=value2")]
    [InlineData("?a=2&a=1", "a=1&a=2")]
    [InlineData("?b=1&a=2&a=1", "a=1&a=2&b=1")]
    public void Parameters_Are_Sorted_By_Name_Then_By_Value(string query, string expected)
        => Assert.Equal(expected, AwsSigV4Signer.BuildCanonicalQueryString(query));
}
