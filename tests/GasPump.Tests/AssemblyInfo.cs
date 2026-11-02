using Xunit;

// The simulation tests drive real worker threads against a fake clock. Running
// test classes in parallel starves the thread pool, and the harness can then
// report quiet while work is still queued. The whole suite runs in well under a
// second serially, so running it that way costs nothing.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
