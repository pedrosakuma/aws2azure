using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Aws2Azure.IntegrationTests.Fixtures;

namespace Aws2Azure.IntegrationTests.OperationalQualification;

internal static class SecretsManagerCredentialRotationQualification
{
    private static readonly TimeSpan SetupPropagationTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RevocationPropagationTimeout = TimeSpan.FromMinutes(10);

    public static async Task<SecretsManagerCredentialRotationResult> VerifyAsync(
        SecretsManagerRealAzureProxyFixture fixture,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        var identityAClientId = RequiredEnvironment("AWS2AZURE_ROTATION_CLIENT_ID_A");
        var identityAObjectId = RequiredEnvironment("AWS2AZURE_ROTATION_OBJECT_ID_A");
        var identityBClientId = RequiredEnvironment("AWS2AZURE_ROTATION_CLIENT_ID_B");
        var identityBObjectId = RequiredEnvironment("AWS2AZURE_ROTATION_OBJECT_ID_B");
        var roleAssignmentA = RequiredEnvironment("AWS2AZURE_ROTATION_ROLE_ASSIGNMENT_ID_A");
        var roleAssignmentB = RequiredEnvironment("AWS2AZURE_ROTATION_ROLE_ASSIGNMENT_ID_B");
        var tokenFileA = RequiredEnvironment("AWS2AZURE_ROTATION_TOKEN_FILE_A");
        var tokenFileB = RequiredEnvironment("AWS2AZURE_ROTATION_TOKEN_FILE_B");
        var artifactDigest = RequiredEnvironment("AWS2AZURE_LOAD_ARTIFACT_DIGEST");
        var candidateConfigDigest = RequiredEnvironment("AWS2AZURE_LOAD_CONFIG_DIGEST");
        var vaultUrl = RequiredEnvironment("AZURE_KEYVAULT_URL");
        var vaultId = RequiredEnvironment("AZURE_KEYVAULT_ID");

        if (string.Equals(identityAClientId, identityBClientId, StringComparison.Ordinal)
            || string.Equals(identityAObjectId, identityBObjectId, StringComparison.Ordinal)
            || string.Equals(roleAssignmentA, roleAssignmentB, StringComparison.Ordinal)
            || string.Equals(tokenFileA, tokenFileB, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Credential rotation requires distinct identities, role assignments, and token files.");
        }

        var startedAt = DateTimeOffset.UtcNow;
        var sentinelName = $"a2a-rotation-{Guid.NewGuid():N}"[..48];
        var sentinelValue = "identity-rotation-sentinel";
        var oldInstance = fixture.DefaultInstance;
        SecretsManagerRealAzureProxyFixture.ProxyInstance? newInstance = null;
        var sentinelCreated = false;
        var greenReadCompletions = 0L;
        var setupRetries = 0L;
        var revocationPolls = 0L;
        var stopwatch = Stopwatch.StartNew();

        using var oldClient = fixture.CreateSecretsManagerClient(maxErrorRetry: 0);
        AmazonSecretsManagerClient? newClient = null;
        try
        {
            await RefreshGitHubOidcTokenAsync(tokenFileA, cancellationToken).ConfigureAwait(false);
            setupRetries += await RetryExpectedSetupPropagationAsync(async () =>
            {
                await oldClient.CreateSecretAsync(new CreateSecretRequest
                {
                    Name = sentinelName,
                    SecretString = sentinelValue,
                    Description = "aws2azure backend identity rotation qualification",
                }, cancellationToken).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
            sentinelCreated = true;
            await AssertValueAsync(oldClient, sentinelName, sentinelValue, cancellationToken)
                .ConfigureAwait(false);

            await RefreshGitHubOidcTokenAsync(tokenFileB, cancellationToken).ConfigureAwait(false);
            newInstance = await fixture.StartProxyInstanceAsync(identityBClientId, tokenFileB)
                .ConfigureAwait(false);
            newClient = fixture.CreateSecretsManagerClient(newInstance.ServiceUrl, maxErrorRetry: 0);
            setupRetries += await RetryExpectedSetupPropagationAsync(async () =>
            {
                await AssertValueAsync(newClient, sentinelName, sentinelValue, cancellationToken)
                    .ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
            greenReadCompletions++;

            var revocationRequestedAt = DateTimeOffset.UtcNow;
            await DeleteExactRoleAssignmentAsync(roleAssignmentA, cancellationToken)
                .ConfigureAwait(false);

            var deadline = DateTimeOffset.UtcNow + RevocationPropagationTimeout;
            DateTimeOffset oldAccessDeniedAt;
            while (true)
            {
                await AssertValueAsync(newClient, sentinelName, sentinelValue, cancellationToken)
                    .ConfigureAwait(false);
                greenReadCompletions++;
                revocationPolls++;

                try
                {
                    await AssertValueAsync(oldClient, sentinelName, sentinelValue, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (AmazonSecretsManagerException exception)
                    when (exception.StatusCode == HttpStatusCode.Forbidden
                          && string.Equals(
                              exception.ErrorCode,
                              "AccessDeniedException",
                              StringComparison.Ordinal))
                {
                    oldAccessDeniedAt = DateTimeOffset.UtcNow;
                    break;
                }

                if (DateTimeOffset.UtcNow >= deadline)
                {
                    throw new TimeoutException(
                        "The revoked runtime identity retained Key Vault access beyond the bounded propagation window.");
                }
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }

            await AssertValueAsync(newClient, sentinelName, sentinelValue, cancellationToken)
                .ConfigureAwait(false);
            greenReadCompletions++;

            await fixture.StopProxyInstanceAsync(oldInstance).ConfigureAwait(false);
            fixture.PromoteToDefault(newInstance);
            await newClient.DeleteSecretAsync(new DeleteSecretRequest
            {
                SecretId = sentinelName,
                ForceDeleteWithoutRecovery = true,
            }, cancellationToken).ConfigureAwait(false);
            sentinelCreated = false;
            var completedAt = DateTimeOffset.UtcNow;

            var bindingDigest = Digest(
                SecretsManagerRealAzureProxyFixture.AwsAccessKey
                + "\n"
                + SecretsManagerRealAzureProxyFixture.AwsSecret);
            var backendDigest = Digest(vaultUrl);
            return new SecretsManagerCredentialRotationResult(
                stopwatch.Elapsed.TotalSeconds,
                completedAt,
                new RealAzureCredentialRotationProof
                {
                    ScenarioId = "credential-rotation",
                    Service = "secretsmanager",
                    Operation = "GetSecretValue",
                    RotationKind = "azure_backend_identity",
                    AuthenticationMode = "workload_identity",
                    BackendKind = "key_vault",
                    IdentityAClientId = identityAClientId,
                    IdentityAObjectId = identityAObjectId,
                    IdentityBClientId = identityBClientId,
                    IdentityBObjectId = identityBObjectId,
                    RoleAssignmentAId = roleAssignmentA,
                    RoleAssignmentBId = roleAssignmentB,
                    RoleDefinitionId = "b86a8fe4-44ce-4948-aee5-eccb2c155cd7",
                    RoleScopeDigestA = Digest(vaultId),
                    RoleScopeDigestB = Digest(vaultId),
                    FederatedIssuerDigest = RequiredEnvironment(
                        "AWS2AZURE_ROTATION_FEDERATED_ISSUER_DIGEST"),
                    FederatedSubjectDigest = RequiredEnvironment(
                        "AWS2AZURE_ROTATION_FEDERATED_SUBJECT_DIGEST"),
                    FederatedAudienceDigest = RequiredEnvironment(
                        "AWS2AZURE_ROTATION_FEDERATED_AUDIENCE_DIGEST"),
                    RuntimeArtifactDigestA = artifactDigest,
                    RuntimeArtifactDigestB = artifactDigest,
                    CandidateConfigDigestA = candidateConfigDigest,
                    CandidateConfigDigestB = candidateConfigDigest,
                    ProxyConfigDigestA = fixture.ProxyConfigDigest,
                    ProxyConfigDigestB = fixture.ProxyConfigDigest,
                    AwsBindingDigestA = bindingDigest,
                    AwsBindingDigestB = bindingDigest,
                    BackendTargetDigestA = backendDigest,
                    BackendTargetDigestB = backendDigest,
                    SetupPropagationRetries = setupRetries,
                    FederatedCredentialCompletions = 2,
                    RevocationPolls = revocationPolls,
                    GreenReadCompletions = greenReadCompletions,
                    OldAccessDeniedCompletions = 1,
                    OldAccessDeniedErrorCode = "AccessDeniedException",
                    OldAccessDeniedHttpStatus = 403,
                    StartedAtUtc = startedAt,
                    RevocationRequestedAtUtc = revocationRequestedAt,
                    OldAccessDeniedAtUtc = oldAccessDeniedAt,
                    CompletedAtUtc = completedAt,
                });
        }
        finally
        {
            if (sentinelCreated)
            {
                try
                {
                    var cleanupClient = newClient ?? oldClient;
                    await cleanupClient.DeleteSecretAsync(new DeleteSecretRequest
                    {
                        SecretId = sentinelName,
                        ForceDeleteWithoutRecovery = true,
                    }, CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                }
            }
            if (newInstance is not null
                && !fixture.IsDefault(newInstance))
            {
                await fixture.StopProxyInstanceAsync(newInstance).ConfigureAwait(false);
            }
            newClient?.Dispose();
        }
    }

    public static async Task RefreshGitHubOidcTokenAsync(
        string tokenFile,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenFile);
        var requestUrl = RequiredEnvironment("ACTIONS_ID_TOKEN_REQUEST_URL");
        var requestToken = RequiredEnvironment("ACTIONS_ID_TOKEN_REQUEST_TOKEN");
        var separator = requestUrl.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            requestUrl + separator + "audience=api%3A%2F%2FAzureADTokenExchange");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", requestToken);
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"GitHub OIDC token request failed with HTTP {(int)response.StatusCode}.",
                null,
                response.StatusCode);
        }
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("value", out var valueElement)
            || valueElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(valueElement.GetString()))
        {
            throw new InvalidDataException("GitHub OIDC response did not contain a token assertion.");
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(tokenFile))
            ?? throw new InvalidDataException("The projected token file has no parent directory.");
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                directory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        await File.WriteAllTextAsync(tokenFile, valueElement.GetString()!, cancellationToken)
            .ConfigureAwait(false);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                tokenFile,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static async Task<long> RetryExpectedSetupPropagationAsync(
        Func<Task> operation,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + SetupPropagationTimeout;
        var retries = 0L;
        while (true)
        {
            try
            {
                await operation().ConfigureAwait(false);
                return retries;
            }
            catch (AmazonSecretsManagerException exception)
                when (IsExpectedSetupAccessPropagation(exception))
            {
                if (DateTimeOffset.UtcNow >= deadline)
                {
                    throw new TimeoutException(
                        "Workload Identity federation or Key Vault RBAC did not propagate within five minutes.",
                        exception);
                }
                retries++;
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsExpectedSetupAccessPropagation(
        AmazonSecretsManagerException exception)
    {
        return exception.StatusCode == HttpStatusCode.Forbidden
               && string.Equals(
                   exception.ErrorCode,
                   "AccessDeniedException",
                   StringComparison.Ordinal);
    }

    private static async Task AssertValueAsync(
        IAmazonSecretsManager client,
        string secretName,
        string expectedValue,
        CancellationToken cancellationToken)
    {
        var response = await client.GetSecretValueAsync(
            new GetSecretValueRequest { SecretId = secretName },
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(response.SecretString, expectedValue, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Credential rotation read returned the wrong sentinel value.");
        }
    }

    private static async Task DeleteExactRoleAssignmentAsync(
        string roleAssignmentId,
        CancellationToken cancellationToken)
    {
        if (!roleAssignmentId.StartsWith("/subscriptions/", StringComparison.OrdinalIgnoreCase)
            || !roleAssignmentId.Contains(
                "/providers/Microsoft.Authorization/roleAssignments/",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The old runtime role assignment id is not an exact Azure resource id.");
        }

        var startInfo = new ProcessStartInfo("az")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("role");
        startInfo.ArgumentList.Add("assignment");
        startInfo.ArgumentList.Add("delete");
        startInfo.ArgumentList.Add("--ids");
        startInfo.ArgumentList.Add(roleAssignmentId);
        startInfo.ArgumentList.Add("--only-show-errors");
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start Azure CLI for role revocation.");
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Azure CLI failed to revoke the exact old role assignment (exit {process.ExitCode}): {stderr}");
        }
    }

    private static string Digest(string value)
    {
        return "sha256:" + Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private static string RequiredEnvironment(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidDataException($"{name} is required for credential rotation.")
            : value;
    }
}

internal sealed record SecretsManagerCredentialRotationResult(
    double DurationSeconds,
    DateTimeOffset CapturedAtUtc,
    RealAzureCredentialRotationProof Proof);
