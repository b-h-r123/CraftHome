using System.Security.Cryptography;
using CraftHome.Web.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CraftHome.Web.Services;

public static class OwnerBootstrap
{
    public const string Email = "owner@crafthome.local";

    public static async Task InitializeAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await database.Database.MigrateAsync();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        if (!await roles.RoleExistsAsync("Owner"))
            await roles.CreateAsync(new IdentityRole("Owner"));
        var owner = await users.FindByEmailAsync(Email);
        if (owner is null)
        {
            var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)) + "a!";
            owner = new ApplicationUser { UserName = Email, Email = Email, EmailConfirmed = true };
            var result = await users.CreateAsync(owner, password);
            if (!result.Succeeded) throw new InvalidOperationException("无法初始化服主账户。");
            var paths = scope.ServiceProvider.GetRequiredService<RuntimePaths>();
            await File.WriteAllTextAsync(Path.Combine(paths.DataDirectory, "first-login.txt"),
                $"CraftHome 首次登录\n邮箱：{Email}\n密码：{password}\n请登录后修改密码并删除本文件。\n");
        }
        if (!await users.IsInRoleAsync(owner, "Owner"))
        {
            var result = await users.AddToRoleAsync(owner, "Owner");
            if (!result.Succeeded) throw new InvalidOperationException("无法初始化服主权限。");
        }
    }
}
