using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using WorkPlanStudio.Persistence;

namespace WorkPlanStudio.Api.Data;

public static class DatabaseStartup
{
    public static async Task ApplyAsync(
        IServiceProvider services,
        IConfiguration configuration,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductionDbContext>();
        if (configuration.GetValue("Database:ApplyMigrationsOnStartup", false))
        {
            var directory = Path.GetDirectoryName(db.Database.GetConnectionString()?.Replace("Data Source=", "", StringComparison.OrdinalIgnoreCase));
            if (db.Database.IsSqlite() && !string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            logger.LogInformation("Applying database migrations using {Provider}", db.Database.ProviderName);
            await db.Database.MigrateAsync(cancellationToken);
        }

        var email = configuration["Identity:BootstrapAdminEmail"];
        var password = configuration["Identity:BootstrapAdminPassword"];
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            return;
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        if (!await roles.RoleExistsAsync("Administrator"))
            IdentityResultGuard(await roles.CreateAsync(new IdentityRole("Administrator")), "create Administrator role");
        var user = await users.FindByEmailAsync(email);
        if (user is null)
        {
            user = new ApplicationUser
            {
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                CreatedUtc = DateTime.UtcNow
            };
            IdentityResultGuard(await users.CreateAsync(user, password), "create bootstrap administrator");
        }
        if (!await users.IsInRoleAsync(user, "Administrator"))
            IdentityResultGuard(await users.AddToRoleAsync(user, "Administrator"), "assign Administrator role");
        logger.LogInformation("Bootstrap administrator {Email} is present", email);
    }

    private static void IdentityResultGuard(IdentityResult result, string action)
    {
        if (!result.Succeeded)
            throw new InvalidOperationException($"Unable to {action}: {string.Join(", ", result.Errors.Select(error => error.Code))}");
    }
}
