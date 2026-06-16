// Integration tests are categorized at the assembly level so local runs can
// exclude the whole integration surface with `--filter "Category!=Integration"`.
// Real-Azure tests add their own `Category=RealAzure` trait on the class.
[assembly: Xunit.AssemblyTrait("Category", "Integration")]

// Do not disable parallelization for the whole assembly: only the in-process
// WebApplicationFactory collections that mutate the process-global
// AWS2AZURE_CONFIG_FILE are marked DisableParallelization on their collection
// definitions. Those collections must stay serialized until the proxy host can
// receive fixture-scoped configuration without using that environment variable.
