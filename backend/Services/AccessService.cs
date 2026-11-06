using System.Text.Json;
using KiComLibBackend.Data;
using KiComLibBackend.Models;
using Microsoft.EntityFrameworkCore;

namespace KiComLibBackend.Services
{
    public sealed class GroupGrant
    {
        public int Id { get; init; }

        public string Name { get; init; } = "";

        public bool CanRead { get; init; }

        public bool CanWrite { get; init; }

        public bool CanAdmin { get; init; }

        public bool CanReadWarehouse { get; init; }

        public bool CanWriteWarehouse { get; init; }
    }

    public sealed class AccessRights
    {
        public bool CanRead { get; init; }

        public bool CanWrite { get; init; }

        public bool CanAdmin { get; init; }

        public bool CanReadWarehouse { get; init; }

        public bool CanWriteWarehouse { get; init; }
    }

    public sealed class CallerContext
    {
        public const string ItemKey = "KiComLib.Caller";

        public bool IsAuthenticated { get; init; }

        public bool TokenRejected { get; init; }

        public int? UserId { get; init; }

        public string? Username { get; init; }

        public bool IsAdmin { get; init; }

        public bool CanRead { get; init; }

        public bool CanWrite { get; init; }

        public bool CanReadWarehouse { get; init; }

        public bool CanWriteWarehouse { get; init; }

        public bool MustChangePassword { get; init; }

        public string AuthType { get; init; } = "anonymous";

        public bool FromCookie { get; init; }

        public string? Source { get; init; }

        public string? TokenName { get; init; }

        public int? SessionId { get; init; }

        public List<GroupGrant> Groups { get; init; } = new();

        public static CallerContext Anonymous(bool tokenRejected = false) => new()
        {
            TokenRejected = tokenRejected,
            CanRead = false,
            CanWrite = false
        };

        public static CallerContext Current(HttpContext http)
        {
            if (http.Items.TryGetValue(ItemKey, out object? value) && value is CallerContext caller)
            {
                return caller;
            }

            return Anonymous();
        }

        public object ToProfile()
        {
            return new
            {
                authenticated = IsAuthenticated,
                username = Username,
                isAdmin = IsAdmin,
                canRead = CanRead,
                canWrite = CanWrite,
                canReadWarehouse = CanReadWarehouse,
                canWriteWarehouse = CanWriteWarehouse,
                mustChangePassword = MustChangePassword,
                authType = AuthType,
                source = Source,
                tokenName = TokenName,
                groups = Groups.Select(g => new
                {
                    id = g.Id,
                    name = g.Name,
                    canRead = g.CanRead,
                    canWrite = g.CanWrite,
                    canAdmin = g.CanAdmin,
                    canReadWarehouse = g.CanReadWarehouse,
                    canWriteWarehouse = g.CanWriteWarehouse
                })
            };
        }
    }

    public class AccessService
    {
        private static readonly TimeSpan TouchInterval = TimeSpan.FromMinutes(5);

        private readonly ApplicationDbContext _db;
        private readonly AppSettingsService _settings;

        public AccessService(ApplicationDbContext db, AppSettingsService settings)
        {
            _db = db;
            _settings = settings;
        }

        public static string? ExtractToken(HttpRequest request)
        {
            if (!request.Headers.TryGetValue("Authorization", out Microsoft.Extensions.Primitives.StringValues values))
            {
                return null;
            }

            string raw = values.ToString().Trim();
            raw = StripAuthScheme(raw, "Bearer");
            raw = StripAuthScheme(raw, "Token");
            if (string.IsNullOrEmpty(raw)
                || raw.Equals("Bearer", StringComparison.OrdinalIgnoreCase)
                || raw.Equals("Token", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return raw;
        }

        /// <summary>
        /// KiCad's HTTP library sends <c>Authorization: Token &lt;access token&gt;</c>.
        /// API clients send <c>Bearer</c> or the raw token.
        /// </summary>
        private static string StripAuthScheme(string raw, string scheme)
        {
            string prefix = scheme + " ";
            if (!raw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return raw;
            }

            return raw[prefix.Length..].Trim();
        }

        public async Task<CallerContext> ResolveAsync(HttpContext http, CancellationToken cancellationToken)
        {
            string? headerToken = ExtractToken(http.Request);
            string? cookieToken = null;
            if (headerToken == null
                && http.Request.Cookies.TryGetValue(SessionCookies.Name, out string? fromCookie)
                && !string.IsNullOrWhiteSpace(fromCookie))
            {
                cookieToken = fromCookie;
            }

            string? token = headerToken ?? cookieToken;
            if (token == null)
            {
                return CallerContext.Anonymous();
            }

            bool sessionFromCookie = headerToken == null;
            string hash = SecretToken.Hash(token);
            DateTime now = DateTime.UtcNow;

            UserSession? session = await _db.UserSessions
                .Include(s => s.User)
                .ThenInclude(u => u.Groups)
                .FirstOrDefaultAsync(s => s.TokenHash == hash, cancellationToken);

            if (session != null)
            {
                if (session.ExpiresAt <= now || !session.User.IsActive)
                {
                    _db.UserSessions.Remove(session);
                    await _db.SaveChangesAsync(cancellationToken);
                    return CallerContext.Anonymous(tokenRejected: true);
                }

                await TouchAsync(session.LastUsedAt, value => session.LastUsedAt = value, now, cancellationToken);
                return FromUser(session.User, "session", sessionId: session.Id, fromCookie: sessionFromCookie);
            }

            ApiToken? apiToken = await _db.ApiTokens
                .Include(t => t.User)
                .ThenInclude(u => u.Groups)
                .FirstOrDefaultAsync(t => t.TokenHash == hash && t.RevokedAt == null, cancellationToken);

            if (apiToken == null || !apiToken.User.IsActive || apiToken.ExpiresAt <= now)
            {
                return CallerContext.Anonymous(tokenRejected: true);
            }

            await TouchAsync(apiToken.LastUsedAt, value => apiToken.LastUsedAt = value, now, cancellationToken);
            return FromUser(apiToken.User, "api-token", tokenName: apiToken.Name, tokenCanWrite: apiToken.CanWrite);
        }

        public async Task<IssuedSession> CreateSessionAsync(AppUser user, CancellationToken cancellationToken)
        {
            string token = SecretToken.Create("kps_");
            DateTime now = DateTime.UtcNow;
            int days = await _settings.SessionLifetimeDaysAsync(cancellationToken);
            DateTime expiresAt = now.AddDays(days);
            _db.UserSessions.Add(new UserSession
            {
                UserId = user.Id,
                TokenHash = SecretToken.Hash(token),
                CreatedAt = now,
                ExpiresAt = expiresAt
            });
            await _db.SaveChangesAsync(cancellationToken);
            return new IssuedSession(token, expiresAt);
        }

        public async Task RevokeCurrentSessionAsync(CallerContext caller, CancellationToken cancellationToken)
        {
            if (caller.SessionId == null)
            {
                return;
            }

            UserSession? session = await _db.UserSessions.FirstOrDefaultAsync(s => s.Id == caller.SessionId, cancellationToken);
            if (session != null)
            {
                _db.UserSessions.Remove(session);
                await _db.SaveChangesAsync(cancellationToken);
            }
        }

        public static CallerContext FromUser(AppUser user, string authType, int? sessionId = null, string? tokenName = null, bool? tokenCanWrite = null, bool fromCookie = false)
        {
            AccessRights rights = RightsOf(user.Groups);
            bool canWrite = rights.CanWrite;
            bool canWriteWarehouse = rights.CanWriteWarehouse;
            if (tokenCanWrite == false)
            {
                canWrite = false;
                canWriteWarehouse = false;
            }

            return new CallerContext
            {
                IsAuthenticated = true,
                UserId = user.Id,
                Username = user.Username,
                IsAdmin = rights.CanAdmin,
                CanRead = rights.CanRead,
                CanWrite = canWrite,
                CanReadWarehouse = rights.CanReadWarehouse,
                CanWriteWarehouse = canWriteWarehouse,
                MustChangePassword = PasswordPolicy.MustChange(user.Source, user.PasswordHash),
                AuthType = authType,
                FromCookie = fromCookie,
                Source = user.Source,
                TokenName = tokenName,
                SessionId = sessionId,
                Groups = user.Groups
                    .OrderBy(g => g.Name)
                    .Select(g => new GroupGrant
                    {
                        Id = g.Id,
                        Name = g.Name,
                        CanRead = g.CanRead,
                        CanWrite = g.CanWrite,
                        CanAdmin = g.CanAdmin,
                        CanReadWarehouse = g.CanReadWarehouse,
                        CanWriteWarehouse = g.CanWriteWarehouse
                    })
                    .ToList()
            };
        }

        public static AccessRights RightsOf(IEnumerable<AppGroup> groups)
        {
            bool canRead = false;
            bool canWrite = false;
            bool canAdmin = false;
            bool canReadWarehouse = false;
            bool canWriteWarehouse = false;
            foreach (AppGroup group in groups)
            {
                if (group.CanRead)
                {
                    canRead = true;
                }

                if (group.CanWrite)
                {
                    canWrite = true;
                }

                if (group.CanAdmin)
                {
                    canAdmin = true;
                }

                if (group.CanReadWarehouse)
                {
                    canReadWarehouse = true;
                }

                if (group.CanWriteWarehouse)
                {
                    canWriteWarehouse = true;
                }
            }

            if (canWrite)
            {
                canRead = true;
            }

            if (canWriteWarehouse)
            {
                canReadWarehouse = true;
            }

            return new AccessRights
            {
                CanRead = canRead,
                CanWrite = canWrite,
                CanAdmin = canAdmin,
                CanReadWarehouse = canReadWarehouse,
                CanWriteWarehouse = canWriteWarehouse
            };
        }

        private async Task TouchAsync(DateTime? lastUsed, Action<DateTime> assign, DateTime now, CancellationToken cancellationToken)
        {
            if (lastUsed != null && lastUsed > now.Subtract(TouchInterval))
            {
                return;
            }

            assign(now);
            await _db.SaveChangesAsync(cancellationToken);
        }
    }

    public readonly record struct IssuedSession(string Token, DateTime ExpiresAt);

    public class CatalogAccessMiddleware
    {
        private readonly RequestDelegate _next;

        public CatalogAccessMiddleware(RequestDelegate next)
        {
            _next = next;
        }

        public async Task InvokeAsync(HttpContext context, AccessService access)
        {
            CallerContext caller = await access.ResolveAsync(context, context.RequestAborted);
            context.Items[CallerContext.ItemKey] = caller;

            string path = (context.Request.Path.Value ?? "").TrimEnd('/').ToLowerInvariant();
            string method = context.Request.Method;
            bool login = method == HttpMethods.Post && path == "/api/auth/login";
            // A session cookie from a previous installation is unknown after a fresh setup.
            // Reject it on API calls, but still serve the login page and drop the cookie.
            bool apiRequest = path == "/api" || path.StartsWith("/api/", StringComparison.Ordinal);
            if (caller.TokenRejected && !login)
            {
                if (AccessService.ExtractToken(context.Request) == null)
                {
                    SessionCookies.Clear(context.Response, context.Request);
                }

                if (apiRequest)
                {
                    await WriteErrorAsync(context, StatusCodes.Status401Unauthorized, "Invalid API token");
                    return;
                }
            }

            bool cookieMutation = method == HttpMethods.Post
                || method == HttpMethods.Put
                || method == HttpMethods.Patch
                || method == HttpMethods.Delete;
            if (caller.FromCookie && cookieMutation && !login && !SessionCookies.HasCsrfHeader(context.Request))
            {
                await WriteErrorAsync(context, StatusCodes.Status403Forbidden, "Missing request header");
                return;
            }

            if (caller.MustChangePassword && !PasswordChangeAllowed(method, path))
            {
                await WriteErrorAsync(context, StatusCodes.Status403Forbidden, "Change the installed default password before using KiComLib");
                return;
            }

            if (BackupService.IsRestoring && !path.StartsWith("/api/backup", StringComparison.Ordinal))
            {
                await WriteErrorAsync(context, StatusCodes.Status503ServiceUnavailable, "A database restore is in progress");
                return;
            }
            bool accountPath = path.StartsWith("/api/auth", StringComparison.Ordinal)
                || path.StartsWith("/api/user", StringComparison.Ordinal)
                || path.StartsWith("/api/group", StringComparison.Ordinal)
                || path.StartsWith("/api/token", StringComparison.Ordinal)
                || path.StartsWith("/api/backup", StringComparison.Ordinal)
                || path.StartsWith("/api/settings", StringComparison.Ordinal)
                || path.StartsWith("/api/directory", StringComparison.Ordinal);
            bool projectApi = path.StartsWith("/api/project", StringComparison.Ordinal)
                || path.StartsWith("/api/git-host", StringComparison.Ordinal)
                || path.StartsWith("/api/git-credential", StringComparison.Ordinal)
                || path.StartsWith("/api/git-repository", StringComparison.Ordinal)
                || path.StartsWith("/api/hooks/git", StringComparison.Ordinal);
            bool webhook = path.StartsWith("/api/hooks/git", StringComparison.Ordinal);
            bool warehouseApi = path.StartsWith("/api/warehouse", StringComparison.Ordinal);
            bool catalogApi = path.StartsWith("/api/", StringComparison.Ordinal) && !accountPath && !projectApi && !warehouseApi;
            if (warehouseApi)
            {
                if (!caller.IsAuthenticated)
                {
                    await WriteErrorAsync(context, StatusCodes.Status401Unauthorized,
                        caller.TokenRejected ? "Invalid API token" : "Authentication required");
                    return;
                }

                bool warehouseMutation = method == HttpMethods.Post
                    || method == HttpMethods.Put
                    || method == HttpMethods.Patch
                    || method == HttpMethods.Delete;
                if (warehouseMutation && !caller.CanWriteWarehouse)
                {
                    await WriteErrorAsync(context, StatusCodes.Status403Forbidden, "Warehouse write permission required");
                    return;
                }

                if (!warehouseMutation
                    && (method == HttpMethods.Get || method == HttpMethods.Head)
                    && !caller.CanReadWarehouse)
                {
                    await WriteErrorAsync(context, StatusCodes.Status403Forbidden, "Warehouse read permission required");
                    return;
                }
            }
            if (projectApi && !webhook && !caller.IsAuthenticated)
            {
                await WriteErrorAsync(context, StatusCodes.Status401Unauthorized,
                    caller.TokenRejected ? "Invalid API token" : "Authentication required");
                return;
            }

            bool existsProbe = method == HttpMethods.Post && (path == "/api/blob/exists" || path == "/api/reviews/resolve");
            bool catalogRead = catalogApi
                && (existsProbe || method == HttpMethods.Get || method == HttpMethods.Head);
            if (catalogRead)
            {
                if (!caller.IsAuthenticated)
                {
                    await WriteErrorAsync(context, StatusCodes.Status401Unauthorized, "Authentication required");
                    return;
                }

                if (!caller.CanRead)
                {
                    await WriteErrorAsync(context, StatusCodes.Status403Forbidden, "Read permission required");
                    return;
                }
            }

            bool mutation = catalogApi
                && !existsProbe
                && (method == HttpMethods.Post || method == HttpMethods.Put || method == HttpMethods.Patch || method == HttpMethods.Delete);

            if (mutation)
            {
                if (!caller.IsAuthenticated)
                {
                    await WriteErrorAsync(context, StatusCodes.Status401Unauthorized,
                        caller.TokenRejected ? "Invalid API token" : "Authentication required");
                    return;
                }

                if (!caller.CanWrite)
                {
                    await WriteErrorAsync(context, StatusCodes.Status403Forbidden, "Write permission required");
                    return;
                }
            }
            await _next(context);
        }

        private static bool PasswordChangeAllowed(string method, string path)
        {
            if (path == "/api/auth/login" && method == HttpMethods.Post)
            {
                return true;
            }

            if (path == "/api/auth/logout" && method == HttpMethods.Post)
            {
                return true;
            }

            if (path == "/api/auth/password" && method == HttpMethods.Post)
            {
                return true;
            }

            if (path == "/api/auth/me" && (method == HttpMethods.Get || method == HttpMethods.Head))
            {
                return true;
            }

            return false;
        }

        private static async Task WriteErrorAsync(HttpContext context, int statusCode, string error)
        {
            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(new { error }));
        }
    }
}
