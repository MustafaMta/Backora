using Microsoft.AspNetCore.Identity;

namespace Backora.Infrastructure.Identity;

public sealed class ApplicationUser : IdentityUser<long>
{
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public bool MustChangePassword { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Display code from the legacy system (users.user_id, e.g. KH-2025-07-00001).</summary>
    public string? LegacyUserCode { get; set; }

    /// <summary>Primary key of the user in the legacy MariaDB database.</summary>
    public long? LegacyId { get; set; }

    public uint Version { get; set; }
}

public sealed class ApplicationRole : IdentityRole<long>
{
    public ApplicationRole() { }
    public ApplicationRole(string name) : base(name) => NormalizedName = name.ToUpperInvariant();
}
