using System.Reflection;

namespace SyncFactors.Infrastructure.Tests;

public sealed class ActiveDirectoryConnectionFactoryTests
{
    [Theory]
    [InlineData("svc_successfactors@example.local", true)]
    [InlineData("CN=svc_successfactors,OU=Service Accounts,DC=example,DC=local", true)]
    [InlineData("svc_successfactors", false)]
    [InlineData(@"EXAMPLE\svc_successfactors", false)]
    public void LooksLikeSimpleBindPrincipal_RecognizesExpectedFormats(string username, bool expected)
    {
        var method = typeof(SyncFactorsConfigurationLoader).Assembly
            .GetType("SyncFactors.Infrastructure.ActiveDirectoryConnectionFactory")
            ?.GetMethod(
                "LooksLikeSimpleBindPrincipal",
                BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var actual = Assert.IsType<bool>(method!.Invoke(null, [username]));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ConfigureSessionOptions_DoesNotSetSigningOrSealing_WhenSigningIsNotRequired()
    {
        var protocolVersion = 0;
        var signingCalls = 0;
        var sealingCalls = 0;

        InvokeConfigureSessionOptions(
            value => protocolVersion = value,
            _ => signingCalls++,
            _ => sealingCalls++,
            requireSigning: false);

        Assert.Equal(3, protocolVersion);
        Assert.Equal(0, signingCalls);
        Assert.Equal(0, sealingCalls);
    }

    [Fact]
    public void ConfigureSessionOptions_SetsSigningAndSealing_WhenSigningIsRequired()
    {
        var protocolVersion = 0;
        var signingValue = false;
        var sealingValue = false;
        var signingCalls = 0;
        var sealingCalls = 0;

        InvokeConfigureSessionOptions(
            value => protocolVersion = value,
            value =>
            {
                signingCalls++;
                signingValue = value;
            },
            value =>
            {
                sealingCalls++;
                sealingValue = value;
            },
            requireSigning: true);

        Assert.Equal(3, protocolVersion);
        Assert.Equal(1, signingCalls);
        Assert.Equal(1, sealingCalls);
        Assert.True(signingValue);
        Assert.True(sealingValue);
    }

    private static void InvokeConfigureSessionOptions(
        Action<int> setProtocolVersion,
        Action<bool> setSigning,
        Action<bool> setSealing,
        bool requireSigning)
    {
        var method = typeof(SyncFactorsConfigurationLoader).Assembly
            .GetType("SyncFactors.Infrastructure.ActiveDirectoryConnectionFactory")
            ?.GetMethod(
                "ConfigureSessionOptions",
                BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        method!.Invoke(null, [setProtocolVersion, setSigning, setSealing, requireSigning]);
    }
}
