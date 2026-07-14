using Microsoft.AspNetCore.Identity;

namespace WorkPlanStudio.Persistence;

public sealed class ApplicationUser : IdentityUser
{
    public DateTime CreatedUtc { get; set; }
    public bool IsActive { get; set; } = true;
}
