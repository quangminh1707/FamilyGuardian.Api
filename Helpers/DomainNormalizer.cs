using System.Text.RegularExpressions;

namespace FamilyGuardian.Api.Helpers;

public static class DomainNormalizer
{
    private static readonly Regex DomainRegex = new(@"^(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z0-9][a-z0-9-]{0,61}[a-z0-9]$");

    public static string Normalize(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        var domain = input.Trim().ToLowerInvariant();

        // Remove scheme
        if (domain.StartsWith("https://")) domain = domain[8..];
        else if (domain.StartsWith("http://")) domain = domain[7..];

        // Remove www.
        if (domain.StartsWith("www.")) domain = domain[4..];

        // Remove path and query
        var slashIdx = domain.IndexOfAny(new[] { '/', '?', '#' });
        if (slashIdx != -1) domain = domain[..slashIdx];

        // Remove port
        var colonIdx = domain.IndexOf(':');
        if (colonIdx != -1) domain = domain[..colonIdx];

        return domain;
    }

    public static bool IsValidDomain(string domain)
    {
        if (string.IsNullOrWhiteSpace(domain)) return false;
        var normalized = Normalize(domain);
        return DomainRegex.IsMatch(normalized);
    }
}
