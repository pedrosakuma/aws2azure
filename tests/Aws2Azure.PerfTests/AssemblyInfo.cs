// Isolate each measured scenario from other collections' emulator startup and
// load. This does not change the worker concurrency inside PerfRunner.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
