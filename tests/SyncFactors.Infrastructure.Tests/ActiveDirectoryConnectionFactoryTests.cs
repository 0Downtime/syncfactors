using System.Reflection;
using System.DirectoryServices.Protocols;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using SyncFactors.Domain;

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
    public void ResolveAuthType_UsesBasic_WhenUsernameIsConfigured()
    {
        var actual = InvokeResolveAuthType("svc-syncfactors@example.local");

        Assert.Equal(AuthType.Basic, actual);
    }

    [Fact]
    public void ResolveAuthType_UsesCurrentWindowsIdentity_WhenUsernameIsBlankOnWindows()
    {
        var actual = InvokeResolveAuthType("");

        var expected = OperatingSystem.IsWindows()
            ? AuthType.Negotiate
            : AuthType.Anonymous;
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

    [Fact]
    public void CertificateMatchesServerName_AcceptsDnsSubjectAlternativeName()
    {
        using var certificate = CreateServerCertificate(
            commonName: "unused.example.test",
            dnsSubjectAlternativeName: "dc01.example.test");

        Assert.True(InvokeCertificateMatchesServerName(certificate, "dc01.example.test"));
    }

    [Fact]
    public void CertificateMatchesServerName_AcceptsCommonName_WhenSubjectAlternativeNameIsAbsent()
    {
        using var certificate = CreateServerCertificate(commonName: "dc01.example.test");

        Assert.True(InvokeCertificateMatchesServerName(certificate, "dc01.example.test"));
    }

    [Fact]
    public void CertificateMatchesServerName_RejectsWrongSubjectAlternativeName_EvenWhenCommonNameMatches()
    {
        using var certificate = CreateServerCertificate(
            commonName: "dc01.example.test",
            dnsSubjectAlternativeName: "dc02.example.test");

        Assert.False(InvokeCertificateMatchesServerName(certificate, "dc01.example.test"));
    }

    [Fact]
    public void ValidateServerCertificateCore_RequiresPinInAdditionToHostnameAndValidChain()
    {
        using var certificate = CreateServerCertificate(
            commonName: "dc01.example.test",
            dnsSubjectAlternativeName: "dc01.example.test");
        var matchingPinTransport = CreateTransport([certificate.Thumbprint]);
        var wrongPinTransport = CreateTransport(["0000000000000000000000000000000000000000"]);

        Assert.True(InvokeValidateServerCertificateCore(
            certificate,
            "dc01.example.test",
            matchingPinTransport,
            _ => true));
        Assert.False(InvokeValidateServerCertificateCore(
            certificate,
            "dc01.example.test",
            wrongPinTransport,
            _ => true));
        Assert.False(InvokeValidateServerCertificateCore(
            certificate,
            "dc01.example.test",
            matchingPinTransport,
            _ => false));
    }

    [Fact]
    public void ValidateServerCertificate_RejectsUntrustedCertificate_EvenWhenPinned()
    {
        using var certificate = CreateServerCertificate(
            commonName: "dc01.example.test",
            dnsSubjectAlternativeName: "dc01.example.test");
        var transport = CreateTransport([certificate.Thumbprint]);

        Assert.False(InvokeValidateServerCertificate(certificate, "dc01.example.test", transport));
    }

    [Fact]
    public void ValidateServerCertificate_RejectsExpiredCertificate_EvenWhenPinned()
    {
        using var certificate = CreateServerCertificate(
            commonName: "dc01.example.test",
            dnsSubjectAlternativeName: "dc01.example.test",
            notBefore: DateTimeOffset.UtcNow.AddDays(-10),
            notAfter: DateTimeOffset.UtcNow.AddDays(-1));
        var transport = CreateTransport([certificate.Thumbprint]);

        Assert.False(InvokeValidateServerCertificate(certificate, "dc01.example.test", transport));
    }

    [Fact]
    public void ValidateServerCertificate_RetainsExplicitValidationOptOut()
    {
        using var certificate = CreateServerCertificate(
            commonName: "wrong-host.example.test",
            dnsSubjectAlternativeName: "wrong-host.example.test");
        var transport = CreateTransport([], requireCertificateValidation: false);

        Assert.True(InvokeValidateServerCertificate(certificate, "dc01.example.test", transport));
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

    private static AuthType InvokeResolveAuthType(string? username)
    {
        var method = typeof(SyncFactorsConfigurationLoader).Assembly
            .GetType("SyncFactors.Infrastructure.ActiveDirectoryConnectionFactory")
            ?.GetMethod(
                "ResolveAuthType",
                BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var config = new ActiveDirectoryConfig(
            Server: "dc01.example.test",
            Port: 636,
            Username: username,
            BindPassword: null,
            IdentityAttribute: "employeeID",
            DefaultActiveOu: "OU=Active,DC=example,DC=test",
            PrehireOu: "OU=Prehire,DC=example,DC=test",
            GraveyardOu: "OU=Graveyard,DC=example,DC=test",
            Transport: new ActiveDirectoryTransportConfig("ldaps", false, true, true, []),
            IdentityPolicy: new ActiveDirectoryIdentityPolicyConfig(true),
            LeaveOu: null,
            UpnSuffix: "example.test",
            LicensingGroups: [],
            IdentityCorrelation: null);

        return Assert.IsType<AuthType>(method!.Invoke(null, [config]));
    }

    private static bool InvokeCertificateMatchesServerName(X509Certificate2 certificate, string server)
    {
        var method = GetFactoryMethod(
            "CertificateMatchesServerName",
            [typeof(X509Certificate2), typeof(string)]);

        return Assert.IsType<bool>(method.Invoke(null, [certificate, server]));
    }

    private static bool InvokeValidateServerCertificate(
        X509Certificate2 certificate,
        string server,
        ActiveDirectoryTransportConfig transport)
    {
        var method = GetFactoryMethod(
            "ValidateServerCertificate",
            [typeof(X509Certificate), typeof(string), typeof(ActiveDirectoryTransportConfig)]);

        return Assert.IsType<bool>(method.Invoke(null, [certificate, server, transport]));
    }

    private static bool InvokeValidateServerCertificateCore(
        X509Certificate2 certificate,
        string server,
        ActiveDirectoryTransportConfig transport,
        Func<X509Certificate2, bool> validateChain)
    {
        var method = GetFactoryMethod(
            "ValidateServerCertificateCore",
            [
                typeof(X509Certificate2),
                typeof(string),
                typeof(ActiveDirectoryTransportConfig),
                typeof(Func<X509Certificate2, bool>)
            ]);

        return Assert.IsType<bool>(method.Invoke(null, [certificate, server, transport, validateChain]));
    }

    private static MethodInfo GetFactoryMethod(string name, Type[] parameterTypes)
    {
        var type = typeof(SyncFactorsConfigurationLoader).Assembly
            .GetType("SyncFactors.Infrastructure.ActiveDirectoryConnectionFactory");
        Assert.NotNull(type);

        var method = type!.GetMethod(
            name,
            BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            types: parameterTypes,
            modifiers: null);
        Assert.NotNull(method);
        return method!;
    }

    private static ActiveDirectoryTransportConfig CreateTransport(
        IReadOnlyList<string> trustedThumbprints,
        bool requireCertificateValidation = true) =>
        new(
            Mode: "ldaps",
            AllowLdapFallback: false,
            RequireCertificateValidation: requireCertificateValidation,
            RequireSigning: true,
            TrustedCertificateThumbprints: trustedThumbprints);

    private static X509Certificate2 CreateServerCertificate(
        string commonName,
        string? dnsSubjectAlternativeName = null,
        DateTimeOffset? notBefore = null,
        DateTimeOffset? notAfter = null)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={commonName}",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        if (!string.IsNullOrWhiteSpace(dnsSubjectAlternativeName))
        {
            var subjectAlternativeNames = new SubjectAlternativeNameBuilder();
            subjectAlternativeNames.AddDnsName(dnsSubjectAlternativeName);
            request.CertificateExtensions.Add(subjectAlternativeNames.Build());
        }

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1")],
            true));

        return request.CreateSelfSigned(
            notBefore ?? DateTimeOffset.UtcNow.AddMinutes(-5),
            notAfter ?? DateTimeOffset.UtcNow.AddDays(1));
    }
}
