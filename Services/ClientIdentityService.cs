namespace DecoSOP.Services;

/// <summary>
/// Scoped service that decides whose preferences (favorites, pins, folder colors, lists) this
/// browser sees. When someone has picked their name in the header, it is that user — so their
/// setup follows them to any PC. Otherwise it falls back to the PC's address, as before users
/// existed. Read once from the initial request; changing user reloads the page.
/// </summary>
public class ClientIdentityService
{
    /// <summary>Cookie holding the chosen user's id. Set by the header's user menu.</summary>
    public const string UserCookie = "decosop-user";

    public string ClientId { get; }

    /// <summary>The PC's own key: used when no user is chosen, and to seed a brand-new user.</summary>
    public string MachineId { get; }

    /// <summary>The chosen user's id, or null when browsing as "this PC".</summary>
    public int? UserId { get; }

    public ClientIdentityService(IHttpContextAccessor accessor)
    {
        var http = accessor.HttpContext;
        MachineId = http?.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (int.TryParse(http?.Request.Cookies[UserCookie], out var id) && id > 0) UserId = id;
        ClientId = UserId is int uid ? UserKey(uid) : MachineId;
    }

    public static string UserKey(int userId) => $"user:{userId}";
}
