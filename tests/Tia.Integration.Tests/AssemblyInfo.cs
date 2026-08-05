// Every test in this assembly drives MSBuildWorkspace, and two workspace loads running
// concurrently in one process do not survive it - the fixture collections failed wholesale until
// this was added, and pass in isolation. Serialising the assembly is the fix; the suite is
// dominated by restore and workspace-load time either way, so there is little parallelism to lose.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
