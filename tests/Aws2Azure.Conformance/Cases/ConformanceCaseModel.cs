namespace Aws2Azure.Conformance.Cases;

/// <summary>
/// Common conformance-scenario shape shared by the per-service matrices. Tier 1
/// replay, Tier 2 backend differential, and the planned Tier 3 real-AWS/real-
/// Azure capture all need the same three pillars:
/// <list type="number">
///   <item>a stable identity (<see cref="Name"/> + <see cref="Operation"/>),</item>
///   <item>a plan that can build one or more signed HTTP requests, including
///   later requests that depend on earlier responses, and</item>
///   <item>a machine-readable expectation surface that says whether the case is
///   an error or a success path and what each step must prove.</item>
/// </list>
/// The existing error matrices now implement this contract directly, and the new
/// happy-path seed cases use the same shape so future capture/diff code does not
/// need a second parallel model.
/// </summary>
public interface IConformanceCase
{
    /// <summary>Stable case identifier used by tests, goldens, and allow-lists.</summary>
    string Name { get; }

    /// <summary>
    /// AWS-facing operation identity. For a single-request error case this is the
    /// failing dispatch surface; for a multi-step success case it may summarize a
    /// round-trip such as <c>PutObject/GetObject/DeleteObject</c>.
    /// </summary>
    string Operation { get; }

    /// <summary>
    /// Expected contract-level outcome. Errors remain the current
    /// <c>status + Code</c> oracle; happy paths describe successful statuses plus
    /// the headers/body fields and semantic properties later tiers must compare.
    /// </summary>
    ConformanceCaseExpectation Expected { get; }

    /// <summary>
    /// Builds the executable request plan for this case. A plan may contain
    /// multiple steps and may also carry a skip reason when the current tier lacks
    /// the backend required to make the success path meaningful (the exact Tier-3
    /// situation tracked by issue #708).
    /// </summary>
    ValueTask<ConformanceExecutionPlan> CreatePlanAsync(
        ConformanceCaseContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Runtime inputs shared by every conformance tier. The immediate need is the
/// access key/secret/base URI used to sign and target requests, but the property
/// bag also lets future fixtures flow service-specific prerequisites (for
/// example, a pre-provisioned Kinesis stream name) without reshaping the case
/// abstraction again.
/// </summary>
public sealed record ConformanceCaseContext(
    string AccessKeyId,
    string SecretAccessKey,
    Uri? BaseAddress = null,
    string Region = "us-east-1",
    IReadOnlyDictionary<string, string>? Properties = null)
{
    /// <summary>Best-effort lookup for a fixture-supplied property.</summary>
    public string? GetProperty(string name)
        => Properties is not null && Properties.TryGetValue(name, out var value)
            ? value
            : null;

    /// <summary>
    /// Required lookup for a fixture-supplied property. Case builders use this
    /// when a later tier must provide a resource identifier instead of letting the
    /// case guess one.
    /// </summary>
    public string GetRequiredProperty(string name)
        => GetProperty(name)
        ?? throw new InvalidOperationException(
            $"Conformance context did not provide required property '{name}'.");
}

/// <summary>
/// Whether a case expects an AWS error contract or a successful response
/// contract. Future differential tiers can branch on this without down-casting
/// the per-service case type.
/// </summary>
public enum ConformanceOutcomeKind
{
    /// <summary>Proxy/backend should fail with an AWS-native error envelope.</summary>
    Error,

    /// <summary>Proxy/backend should succeed and expose service-specific data.</summary>
    Success,
}

/// <summary>
/// Case-level expectation surface. The step expectations describe the per-request
/// contract, while <see cref="SemanticAssertion"/> captures the higher-level
/// property a happy-path round-trip must preserve (body equality, pagination
/// completeness, condition evaluation, batch success semantics, and so on).
/// </summary>
public sealed record ConformanceCaseExpectation(
    ConformanceOutcomeKind OutcomeKind,
    IReadOnlyList<ConformanceStepExpectation> Steps,
    string? SemanticAssertion = null)
{
    /// <summary>Creates the current single-step error oracle.</summary>
    public static ConformanceCaseExpectation Error(
        int expectedStatus,
        string expectedCode,
        string? notes = null)
        => new(
            ConformanceOutcomeKind.Error,
            [new(expectedStatus, ExpectedErrorCode: expectedCode, Notes: notes)]);

    /// <summary>Creates a success-path oracle spanning one or more requests.</summary>
    public static ConformanceCaseExpectation Success(
        IReadOnlyList<ConformanceStepExpectation> steps,
        string? semanticAssertion = null)
        => new(ConformanceOutcomeKind.Success, steps, semanticAssertion);
}

/// <summary>
/// One request/response contract within a case. Error cases currently have one
/// step; happy paths commonly need several.
/// </summary>
public sealed record ConformanceStepExpectation(
    int ExpectedStatus,
    string? ExpectedErrorCode = null,
    IReadOnlyList<ConformanceHeaderExpectation>? RequiredHeaders = null,
    IReadOnlyList<ConformanceBodyAssertion>? RequiredBodyAssertions = null,
    string? Notes = null);

/// <summary>
/// A required response-header fact, expressed as <c>name + rule description</c>
/// rather than a single hard-coded value so the matrix can encode things like
/// "ETag must be present" and "x-amz-target media type stays 1.1".
/// </summary>
public sealed record ConformanceHeaderExpectation(string Name, string Requirement);

/// <summary>
/// A required response-body fact. <see cref="Path"/> is intentionally logical
/// rather than tied to XML or JSON syntax so the same model works for REST-XML,
/// AWS-JSON, and Query services.
/// </summary>
public sealed record ConformanceBodyAssertion(string Path, string Requirement);

/// <summary>
/// Executable request plan produced from a case. A plan can be marked skippable
/// while still retaining the exact request sequence to be used once a stronger
/// backend-backed tier is available.
/// </summary>
public sealed record ConformanceExecutionPlan(
    IReadOnlyList<ConformanceRequestStep> Steps,
    string? SkipReason = null)
{
    /// <summary>Whether the current tier should skip execution of this plan.</summary>
    public bool ShouldSkip => !string.IsNullOrWhiteSpace(SkipReason);
}

/// <summary>
/// One request-building step. Later steps receive the responses produced by
/// earlier ones, which is what lets a case say "list page 2 uses the
/// NextContinuationToken returned by page 1" or "DeleteMessage uses the receipt
/// handle returned by ReceiveMessage".
/// </summary>
public sealed class ConformanceRequestStep
{
    private readonly Func<ConformanceExecutionState, CancellationToken, ValueTask<HttpRequestMessage>> _buildRequestAsync;

    public ConformanceRequestStep(
        string name,
        Func<ConformanceExecutionState, CancellationToken, ValueTask<HttpRequestMessage>> buildRequestAsync)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(buildRequestAsync);

        Name = name;
        _buildRequestAsync = buildRequestAsync;
    }

    public ConformanceRequestStep(
        string name,
        Func<ConformanceExecutionState, HttpRequestMessage> buildRequest)
        : this(name, (state, _) => new ValueTask<HttpRequestMessage>(buildRequest(state)))
    {
    }

    public ConformanceRequestStep(string name, Func<HttpRequestMessage> buildRequest)
        : this(name, _ => buildRequest())
    {
    }

    /// <summary>Stable step identifier within the case.</summary>
    public string Name { get; }

    /// <summary>Builds the signed HTTP request for this step.</summary>
    public ValueTask<HttpRequestMessage> BuildRequestAsync(
        ConformanceExecutionState state,
        CancellationToken cancellationToken = default)
        => _buildRequestAsync(state, cancellationToken);
}

/// <summary>
/// Execution-time state shared across plan steps. The first request runs with no
/// prior exchanges; later steps can inspect <see cref="Exchanges"/> to pick up a
/// token, ARN, queue URL, receipt handle, or ETag emitted by an earlier step.
/// </summary>
public sealed class ConformanceExecutionState
{
    public ConformanceExecutionState(
        ConformanceCaseContext context,
        IReadOnlyList<ConformanceObservedExchange>? exchanges = null)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
        Exchanges = exchanges ?? [];
    }

    /// <summary>Shared credentials/base URI/fixture properties.</summary>
    public ConformanceCaseContext Context { get; }

    /// <summary>Responses already observed for earlier steps.</summary>
    public IReadOnlyList<ConformanceObservedExchange> Exchanges { get; }

    /// <summary>Finds a previously recorded exchange by step name.</summary>
    public bool TryGetExchange(string stepName, out ConformanceObservedExchange exchange)
    {
        foreach (var candidate in Exchanges)
        {
            if (string.Equals(candidate.StepName, stepName, StringComparison.Ordinal))
            {
                exchange = candidate;
                return true;
            }
        }

        exchange = default!;
        return false;
    }

    /// <summary>Gets a previously recorded exchange or throws.</summary>
    public ConformanceObservedExchange GetRequiredExchange(string stepName)
    {
        if (TryGetExchange(stepName, out var exchange))
        {
            return exchange;
        }

        throw new InvalidOperationException(
            $"Conformance execution state did not contain a prior exchange named '{stepName}'.");
    }
}

/// <summary>
/// Minimal prior-response snapshot passed between steps. The full HTTP objects
/// are intentionally not retained here so a future runner can serialize this
/// state cheaply for golden capture or diff replay.
/// </summary>
public sealed record ConformanceObservedExchange(
    string StepName,
    int StatusCode,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    string Body);

/// <summary>
/// Generic case implementation used by the new happy-path seed matrices. The
/// existing error matrices already have service-specific records because their
/// request-building inputs differ slightly; this shared wrapper keeps the new
/// cases concise while still satisfying <see cref="IConformanceCase"/>.
/// </summary>
public sealed class PlannedConformanceCase : IConformanceCase
{
    private readonly Func<ConformanceCaseContext, CancellationToken, ValueTask<ConformanceExecutionPlan>> _createPlanAsync;

    public PlannedConformanceCase(
        string name,
        string operation,
        ConformanceCaseExpectation expected,
        Func<ConformanceCaseContext, CancellationToken, ValueTask<ConformanceExecutionPlan>> createPlanAsync)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(createPlanAsync);

        Name = name;
        Operation = operation;
        Expected = expected;
        _createPlanAsync = createPlanAsync;
    }

    public string Name { get; }

    public string Operation { get; }

    public ConformanceCaseExpectation Expected { get; }

    public ValueTask<ConformanceExecutionPlan> CreatePlanAsync(
        ConformanceCaseContext context,
        CancellationToken cancellationToken = default)
        => _createPlanAsync(context, cancellationToken);
}
