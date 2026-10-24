namespace Mantenio.Client;

/// <summary>Per-host defaults. Android keeps people signed in by default; the web asks every time.</summary>
public sealed record ClientPreferences(bool KeepSignedInByDefault);
