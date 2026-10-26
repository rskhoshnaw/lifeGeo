namespace LifeDrop.Mobile.Visits;

// Which home buttons the signed-in user gets (plan step 5.3): the warehouse and the delivery buttons only for people whose role has those permissions, so a visitor
// does not see them. The permission list of the user is kept on the phone as comma-separated keys; a list that was never fetched shows everything (the server decides anyway).
public static class ButtonAccess
{
    public static bool Allows(string? storedPermissions, string permission)
    {
        if (string.IsNullOrWhiteSpace(storedPermissions)) return true;
        return storedPermissions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains(permission, StringComparer.Ordinal);
    }

    public static string Store(IEnumerable<string> permissions) => string.Join(",", permissions.Where(p => !string.IsNullOrWhiteSpace(p) && !p.Contains(',')).Distinct(StringComparer.Ordinal));
}
