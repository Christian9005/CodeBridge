using System.Security.Cryptography;
using System.Text;

namespace CodeBridge.WebEditor;

/// <summary>Who may talk to the editor: this computer only, and only with the key printed at start-up.</summary>
internal static class RequestGuard
{
    /// <summary>The status code to answer with, or null when the request may go on.</summary>
    public static int? Reject(string host, PathString path, string? givenKey, string key)
    {
        // DNS rebinding: a page on another name that resolves to 127.0.0.1 must not reach the editor
        if (host is not ("127.0.0.1" or "localhost" or "[::1]" or "::1"))
            return StatusCodes.Status421MisdirectedRequest;

        if (path.StartsWithSegments("/api") &&
            (givenKey is null || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(givenKey), Encoding.UTF8.GetBytes(key))))
            return StatusCodes.Status401Unauthorized;

        return null;
    }
}
