using SyncFactors.Infrastructure;

namespace SyncFactors.Infrastructure.Tests;

public sealed class LocalAuthOptionsTests
{
    [Fact]
    public void GetAbsoluteSessionLifetime_AllowsExtendedConfiguredValues()
    {
        var options = new LocalAuthOptions
        {
            AbsoluteSessionHours = 168
        };

        Assert.Equal(TimeSpan.FromHours(168), options.GetAbsoluteSessionLifetime());
    }

    [Fact]
    public void GetIdleTimeout_AllowsExtendedConfiguredValues()
    {
        var options = new LocalAuthOptions
        {
            IdleTimeoutMinutes = 480
        };

        Assert.Equal(TimeSpan.FromHours(8), options.GetIdleTimeout());
    }

    [Fact]
    public void GetRememberMeSessionLifetime_AllowsExtendedConfiguredValues()
    {
        var options = new LocalAuthOptions
        {
            RememberMeSessionHours = 720
        };

        Assert.Equal(TimeSpan.FromDays(30), options.GetRememberMeSessionLifetime());
    }
}
