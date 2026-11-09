using CohortCompanion.Web.Data;
using Microsoft.AspNetCore.Identity;

namespace CohortCompanion.Web.Auth;

public enum AccessChange
{
    Done,
    NotFound,

    /// <summary>Admins can't change their own access, so nobody locks themselves out by mistake.</summary>
    Self,

    /// <summary>The school always keeps at least one admin.</summary>
    LastAdmin,

    /// <summary>Admins always teach too; take away admin first.</summary>
    StillAdmin,
}

/// <summary>
/// The role changes an admin makes on the Instructors page. Every admin is also an instructor, so the pages for
/// authoring and running sessions always work for them, and there's always at least one admin left.
/// </summary>
public sealed class InstructorAccessService(UserManager<ApplicationUser> users, AuditLog audit)
{
    public Task<AccessChange> MakeAdminAsync(string actorId, string userId) =>
        ChangeAsync(actorId, userId, "MakeAdmin", async user =>
        {
            await EnsureRoleAsync(user, Roles.Admin);
            await EnsureRoleAsync(user, Roles.Instructor);
            return AccessChange.Done;
        });

    public Task<AccessChange> RemoveAdminAsync(string actorId, string userId) =>
        ChangeAsync(actorId, userId, "RemoveAdmin", async user =>
        {
            if (!await users.IsInRoleAsync(user, Roles.Admin))
            {
                return AccessChange.Done;
            }

            if ((await users.GetUsersInRoleAsync(Roles.Admin)).Count <= 1)
            {
                return AccessChange.LastAdmin;
            }

            await users.RemoveFromRoleAsync(user, Roles.Admin);
            return AccessChange.Done;
        });

    /// <summary>Gives back, or takes away, someone's access to the instructor pages.</summary>
    public Task<AccessChange> SetInstructorAsync(string actorId, string userId, bool allowed) =>
        ChangeAsync(actorId, userId, allowed ? "RestoreInstructor" : "RemoveInstructor", async user =>
        {
            if (allowed)
            {
                await EnsureRoleAsync(user, Roles.Instructor);
                return AccessChange.Done;
            }

            if (await users.IsInRoleAsync(user, Roles.Admin))
            {
                return AccessChange.StillAdmin;
            }

            if (await users.IsInRoleAsync(user, Roles.Instructor))
            {
                await users.RemoveFromRoleAsync(user, Roles.Instructor);
            }

            return AccessChange.Done;
        });

    /// <summary>Gives the Instructor role to any admin without it, from before admins always taught.</summary>
    public async Task EnsureAdminsTeachAsync()
    {
        foreach (var admin in await users.GetUsersInRoleAsync(Roles.Admin))
        {
            if (!await users.IsInRoleAsync(admin, Roles.Instructor))
            {
                await users.AddToRoleAsync(admin, Roles.Instructor);
            }
        }
    }

    public static string Describe(AccessChange result) => result switch
    {
        AccessChange.NotFound => "That account no longer exists.",
        AccessChange.Self => "You can't change your own access. Ask another admin.",
        AccessChange.LastAdmin => "There has to be at least one admin. Make someone else an admin first.",
        AccessChange.StillAdmin => "Admins always have instructor access. Remove their admin access first.",
        _ => "",
    };

    private async Task<AccessChange> ChangeAsync(string actorId, string userId, string action, Func<ApplicationUser, Task<AccessChange>> change)
    {
        if (actorId == userId)
        {
            return AccessChange.Self;
        }

        if (await users.FindByIdAsync(userId) is not { } user)
        {
            return AccessChange.NotFound;
        }

        var result = await change(user);
        if (result == AccessChange.Done)
        {
            audit.Record(actorId, action, $"user:{userId}");

            // Signs them out of existing sessions at the next revalidation, so the change takes effect.
            await users.UpdateSecurityStampAsync(user);
        }

        return result;
    }

    private async Task EnsureRoleAsync(ApplicationUser user, string role)
    {
        if (!await users.IsInRoleAsync(user, role))
        {
            await users.AddToRoleAsync(user, role);
        }
    }
}
