namespace ProjectDashboard.Models;

/// <summary>
/// What <c>git count-objects -v</c> reports. Both sizes are the kibibyte figures git prints,
/// not byte counts, so a reclaim smaller than a kibibyte reads as zero rather than as a
/// fabricated precision.
/// </summary>
public sealed record RepoObjectCounts(int LooseObjects, long LooseKiB, int PackedObjects, long PackKiB)
{
    public long TotalKiB => LooseKiB + PackKiB;

    public int TotalObjects => LooseObjects + PackedObjects;
}
