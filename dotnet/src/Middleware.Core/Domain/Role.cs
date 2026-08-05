namespace Middleware.Core.Domain;

/// <summary>
/// Application roles. Persisted by name and exposed to authorization as <c>ROLE_&lt;name&gt;</c>
/// authorities. Only a single role exists today; the enum keeps role handling type-safe.
/// </summary>
public enum Role
{
    User
}

/// <summary>Helpers for <see cref="Role"/>.</summary>
public static class RoleExtensions
{
    /// <summary>Authority form of this role (e.g. <c>ROLE_USER</c>), matching the Java service.</summary>
    public static string Authority(this Role role) => "ROLE_" + role.ToString().ToUpperInvariant();
}
