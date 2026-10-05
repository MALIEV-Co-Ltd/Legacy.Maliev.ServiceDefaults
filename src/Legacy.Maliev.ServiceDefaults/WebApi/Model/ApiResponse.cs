namespace Maliev.Service.WebApi.Model;

/// <summary>Retains the parsed item together with its caller-owned HTTP response.</summary>
/// <typeparam name="T">The response item type.</typeparam>
public class ApiResponse<T>
{
    /// <summary>Gets or sets the parsed item, or the type's default value when no item was read.</summary>
    public T Item { get; set; } = default!;

    /// <summary>Gets or sets the original HTTP response, whose disposal remains the caller's responsibility.</summary>
    public HttpResponseMessage Response { get; set; } = null!;
}
