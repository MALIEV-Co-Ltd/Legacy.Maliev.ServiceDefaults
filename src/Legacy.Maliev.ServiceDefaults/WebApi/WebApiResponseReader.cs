extern alias LegacyHttpFormatter;

using Maliev.Service.WebApi.Model;

namespace Maliev.Service.WebApi;

/// <summary>Retains the legacy typed-success formatter boundary without owning HTTP transport or credentials.</summary>
public static class WebApiResponseReader
{
    /// <summary>Parses successful content and preserves non-success responses without reading their bodies.</summary>
    /// <typeparam name="T">The response item type.</typeparam>
    /// <param name="response">The response retained and disposed by the caller.</param>
    /// <param name="cancellationToken">The caller's cancellation token, also forwarded to the original formatter.</param>
    /// <returns>The parsed item, when successful, together with the unchanged HTTP response.</returns>
    /// <exception cref="ArgumentNullException">The response is null.</exception>
    /// <exception cref="OperationCanceledException">The caller cancels the read.</exception>
    public static async Task<ApiResponse<T>> ReadAsAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        cancellationToken.ThrowIfCancellationRequested();
        var result = new ApiResponse<T> { Response = response };
        if (response.IsSuccessStatusCode)
        {
            result.Item = await LegacyHttpFormatter::System.Net.Http.HttpContentExtensions
                .ReadAsAsync<T>(response.Content, cancellationToken).ConfigureAwait(false);
        }
        return result;
    }
}
