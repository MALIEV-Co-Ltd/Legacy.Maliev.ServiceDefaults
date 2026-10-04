namespace Maliev.Aspire.ServiceDefaults.Diagnostics;

/// <summary>Maps request metadata to code-owned operation labels without exporting transport details.</summary>
internal static class PrivateDependencyOperationMapper
{
    internal static string GetOperation(HttpRequestMessage request)
    {
        // HttpClient resolves relative targets before invoking factory-owned handlers.
        // An unresolved or missing target is not enough evidence to classify an operation.
        if (request.RequestUri is not { IsAbsoluteUri: true } target)
        {
            return "UnknownOperation";
        }

        var path = target.AbsolutePath.AsSpan().TrimStart('/');
        var separator = path.IndexOf('/');
        var firstSegment = separator < 0 ? path : path[..separator];
        // The longest retained resource is quotationrequests (17 characters).
        // Do not allocate/case-fold arbitrarily large untrusted path segments.
        if (firstSegment.Length is < 1 or > 17)
        {
            return "UnknownOperation";
        }
        var resource = firstSegment.ToString().ToLowerInvariant() switch
        {
            "auth" => "Auth",
            "countries" => "Countries",
            "currencies" => "Currencies",
            "customers" => "Customers",
            "emails" => "Emails",
            "employees" => "Employees",
            "invoices" => "Invoices",
            "jobs" => "Jobs",
            "materials" => "Materials",
            "messages" => "Messages",
            "orders" => "Orders",
            "orderstatuses" => "OrderStatuses",
            "payments" => "Payments",
            "pdfs" => "Pdfs",
            "purchaseorders" => "PurchaseOrders",
            "quotationrequests" => "QuotationRequests",
            "quotations" => "Quotations",
            "receipts" => "Receipts",
            "suppliers" => "Suppliers",
            "uploads" => "Uploads",
            _ => null,
        };
        var verb = request.Method.Method switch
        {
            "GET" => "Get",
            "POST" => "Post",
            "PUT" => "Put",
            "PATCH" => "Patch",
            "DELETE" => "Delete",
            "HEAD" => "Head",
            "OPTIONS" => "Options",
            _ => null,
        };

        return resource is null || verb is null ? "UnknownOperation" : resource + "." + verb;
    }
}
