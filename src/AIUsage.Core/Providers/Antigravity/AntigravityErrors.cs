namespace AIUsage.Core.Providers.Antigravity;

public enum AntigravityErrorKind
{
    NotSignedIn,
    CredentialStoreUnreadable,
    InvalidCredentialData,
    AuthExpired,
    Unavailable
}

public sealed class AntigravityError : Exception, Models.ICategorizedError
{
    public AntigravityErrorKind Kind { get; }

    public AntigravityError(AntigravityErrorKind kind) : base(Describe(kind))
    {
        Kind = kind;
    }

    private static string Describe(AntigravityErrorKind kind) => kind switch
    {
        AntigravityErrorKind.NotSignedIn => "Start Antigravity or run `agy` and try again.",
        AntigravityErrorKind.CredentialStoreUnreadable => "Couldn't read Antigravity credentials from Windows Credential Manager. Sign in to Antigravity again.",
        AntigravityErrorKind.InvalidCredentialData => "Antigravity credentials are invalid. Open Antigravity or run `agy` to sign in again.",
        AntigravityErrorKind.AuthExpired => "Antigravity sign-in expired. Open Antigravity or run `agy` to refresh.",
        AntigravityErrorKind.Unavailable => "Antigravity usage is temporarily unavailable. Try again shortly.",
        _ => "Antigravity error."
    };

    public Models.ErrorCategory ErrorCategory => Kind switch
    {
        AntigravityErrorKind.NotSignedIn => Models.ErrorCategory.NotLoggedIn,
        AntigravityErrorKind.CredentialStoreUnreadable => Models.ErrorCategory.CredentialAccess,
        AntigravityErrorKind.InvalidCredentialData => Models.ErrorCategory.AuthInvalid,
        AntigravityErrorKind.AuthExpired => Models.ErrorCategory.AuthExpired,
        AntigravityErrorKind.Unavailable => Models.ErrorCategory.Network,
        _ => Models.ErrorCategory.Other
    };
}
