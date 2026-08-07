using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Maliev.Aspire.ServiceDefaults.Caching;

namespace Legacy.Maliev.ServiceDefaults.Tests.Caching;

public sealed class CacheLoggingContractTests
{
    [Fact]
    public void Cache_log_identifier_is_stable_but_does_not_include_the_input()
    {
        var input = "customer:email=customer@example.test:session=opaque";
        var expectedDigest = SHA256.HashData(Encoding.UTF8.GetBytes(input));

        var helper = typeof(InMemoryCacheService).Assembly
            .GetType("Maliev.Aspire.ServiceDefaults.Caching.CacheLogValue");
        Assert.NotNull(helper);

        var method = helper!.GetMethod("Hash", BindingFlags.Static | BindingFlags.Public);
        Assert.NotNull(method);

        var result = Assert.IsType<string>(method!.Invoke(null, [input]));

        Assert.DoesNotContain(input, result, StringComparison.Ordinal);
        Assert.Equal($"{input.Length}:{Convert.ToHexString(expectedDigest.AsSpan(0, 8)).ToLowerInvariant()}", result);
    }

}
