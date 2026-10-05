namespace TillPOS.Erp;

public sealed record ErpConnection(Uri BaseUrl, string ApiKey, string ApiSecret);

/// <summary>Arguments of frappe.client.get_list. Filters are [field, operator, value] triples.
/// PageLength 0 returns all rows.</summary>
public sealed record ListQuery(
    string Doctype,
    IReadOnlyList<string> Fields,
    IReadOnlyList<object[]> Filters,
    string OrderBy = "modified asc",
    int Start = 0,
    int PageLength = 500);

public sealed record ServerInfo(string User, DateTimeOffset? ServerTime);
