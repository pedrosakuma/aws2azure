using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Aws2Azure.Core.Azure;

/// <summary>
/// Augments an outbound Azure REST request with the appropriate
/// authorization material (headers and/or query string).
/// </summary>
public interface IAzureAuthenticator
{
    ValueTask AuthenticateAsync(HttpRequestMessage request, CancellationToken cancellationToken = default);
}
