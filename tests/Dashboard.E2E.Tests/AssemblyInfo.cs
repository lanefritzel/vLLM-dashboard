using Xunit;

// xUnit runs different [Collection]s in parallel by default. This project now has
// two ("E2E" and "E2EWithSupervisor", P14), each spinning up its own full
// Dashboard.Web process + Postgres container (+ a real supervisor container for
// the latter) + browser -- running both at once contends hard for CPU/IO on a
// single dev machine and produces exactly the kind of flaky Playwright timeout
// this attribute exists to avoid. The whole suite already finishes in a couple
// of minutes serialized; there's nothing to gain from racing two heavyweight
// stacks against each other.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
