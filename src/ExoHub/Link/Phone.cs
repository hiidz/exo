namespace ExoHub.Link;

// A phone as its app names itself in HELLO: the identity the app made once,
// kept until its data is cleared, in hex, and the name its user gave the
// phone. Keyed by the identity alone. Logged as the identity's first eight
// digits, one token as the adb serial was, since the tools find log fields
// by position; the accept line gives the name.
sealed record Phone(string Id, string Name)
{
    public override string ToString() => Id[..8];
}
