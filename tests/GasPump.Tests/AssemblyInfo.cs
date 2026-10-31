using Xunit;

// The simulation tests drive real worker threads against a fake clock. Running
// test classes in parallel starves the thread pool and makes "quiet" lie.
// The whole suite runs in well under a second serially, so nothing is lost.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
