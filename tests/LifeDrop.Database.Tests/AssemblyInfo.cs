using Xunit;

// Tests set process-wide environment variables (for example RoleAssignmentDatabaseTests sets
// ConnectionStrings__LifeDrop and Jwt__*), which the API host reads at startup. Running test
// classes in parallel lets one class's values leak into another class's host.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
