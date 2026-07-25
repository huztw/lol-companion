using System.Net.Http.Headers;
using System.Text;

namespace LoLCompanion.Core.Lcu;

public sealed class LcuCredential
{
    private readonly string _password;

    public LcuCredential(int processId, string host, int port, string protocol, string password)
    {
        if (processId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(processId));
        }

        if (string.IsNullOrWhiteSpace(host))
        {
            throw new ArgumentException("Host is required.", nameof(host));
        }

        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        if (!string.Equals(protocol, "https", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Only https is supported.", nameof(protocol));
        }

        if (string.IsNullOrWhiteSpace(password))
        {
            throw new ArgumentException("Password is required.", nameof(password));
        }

        ProcessId = processId;
        Host = host;
        Port = port;
        Protocol = "https";
        _password = password;
    }

    public int ProcessId { get; }

    public string Host { get; }

    public int Port { get; }

    public string Protocol { get; }

    public AuthenticationHeaderValue CreateAuthorizationHeader()
    {
        var raw = Convert.ToBase64String(Encoding.ASCII.GetBytes($"riot:{_password}"));
        return new AuthenticationHeaderValue("Basic", raw);
    }

    public override string ToString() =>
        $"LcuCredential {{ ProcessId = {ProcessId}, Host = {Host}, Port = {Port}, Protocol = {Protocol}, Password = [redacted] }}";
}
