using Microsoft.Extensions.DependencyInjection;

namespace Maliev.Aspire.ServiceDefaults.IAM;

internal static class IamClientRegistrationGuard
{
    private enum AuthenticationMode
    {
        LegacyLocalSigning,
        AuthServiceTokenExchange
    }

    private sealed record IamClientRegistrationMarker(AuthenticationMode Mode);

    private sealed class AuthServiceTokenExchangeRegistrationMarker;

    internal static void MarkAuthServiceTokenExchangeRegistered(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (!services.Any(descriptor =>
                descriptor.ServiceType == typeof(AuthServiceTokenExchangeRegistrationMarker)))
        {
            services.AddSingleton(new AuthServiceTokenExchangeRegistrationMarker());
        }
    }

    internal static bool IsAuthServiceTokenExchangeRegistered(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.Any(descriptor =>
            descriptor.ServiceType == typeof(AuthServiceTokenExchangeRegistrationMarker));
    }

    internal static bool TryReserveLegacyClient(IServiceCollection services) =>
        TryReserveClient(services, AuthenticationMode.LegacyLocalSigning);

    internal static bool TryReserveAuthServiceClient(IServiceCollection services) =>
        TryReserveClient(services, AuthenticationMode.AuthServiceTokenExchange);

    internal static void EnsureLegacyClientCanRegister(IServiceCollection services) =>
        EnsureCompatible(services, AuthenticationMode.LegacyLocalSigning);

    internal static void EnsureAuthServiceClientCanRegister(IServiceCollection services) =>
        EnsureCompatible(services, AuthenticationMode.AuthServiceTokenExchange);

    private static bool TryReserveClient(IServiceCollection services, AuthenticationMode requestedMode)
    {
        ArgumentNullException.ThrowIfNull(services);
        var existingMarker = GetRegistrationMarker(services);
        if (existingMarker is null)
        {
            services.AddSingleton(new IamClientRegistrationMarker(requestedMode));
            return true;
        }

        EnsureCompatible(existingMarker, requestedMode);
        return false;
    }

    private static void EnsureCompatible(IServiceCollection services, AuthenticationMode requestedMode)
    {
        ArgumentNullException.ThrowIfNull(services);
        var existingMarker = GetRegistrationMarker(services);
        if (existingMarker is not null)
        {
            EnsureCompatible(existingMarker, requestedMode);
        }
    }

    private static void EnsureCompatible(
        IamClientRegistrationMarker existingMarker,
        AuthenticationMode requestedMode)
    {
        if (existingMarker.Mode != requestedMode)
        {
            throw new InvalidOperationException(
                "Legacy and AuthService-backed IAM client registrations cannot be combined in one process.");
        }
    }

    private static IamClientRegistrationMarker? GetRegistrationMarker(IServiceCollection services) =>
        services.FirstOrDefault(descriptor =>
                descriptor.ServiceType == typeof(IamClientRegistrationMarker))?
            .ImplementationInstance as IamClientRegistrationMarker;
}
