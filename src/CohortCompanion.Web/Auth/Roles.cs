namespace CohortCompanion.Web.Auth;

public static class Roles
{
    public const string Admin = "Admin";
    public const string Instructor = "Instructor";

    public static readonly string[] All = [Admin, Instructor];
}
