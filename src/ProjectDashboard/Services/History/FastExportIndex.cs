namespace ProjectDashboard.Services.History;

public sealed class BlobIndexEntry
{
    public required long Mark { get; init; }

    /// <summary>Payload location on the spool. Invalid once <see cref="BlobRecord.Data"/>.InlineBytes replaces it.</summary>
    public required SpoolSlice Payload { get; init; }

    public required BlobRecord Record { get; init; }
}

public sealed class CommitIndexEntry
{
    public required long Mark { get; init; }

    public required string RefName { get; init; }

    public required string? OriginalOid { get; init; }

    /// <summary>Spool offset where the message payload starts.</summary>
    public required long MessageOffset { get; init; }

    /// <summary>Parent datarefs (`:N` or oid) in from-then-merge order.</summary>
    public required IReadOnlyList<string> Parents { get; init; }

    /// <summary>Live references into <see cref="Record"/> — repointing one rewrites the commit's M line.</summary>
    public required IReadOnlyList<FileModify> FileModifies { get; init; }

    public required CommitRecord Record { get; init; }
}

public sealed class TagIndexEntry
{
    public required long Mark { get; init; }

    public required string TagName { get; init; }

    public required string? OriginalOid { get; init; }

    public required TagRecord Record { get; init; }
}

/// <summary>
/// Pass-A index over a parsed stream: mark-addressable views of every blob, commit, and
/// tag, holding live references to the records so a rewrite pass can mutate content and
/// then re-emit. <see cref="MaxMark"/> is the allocation floor for marks a rewrite mints.
/// </summary>
public sealed class FastExportIndex
{
    private readonly Dictionary<long, BlobIndexEntry> _blobs = [];
    private readonly Dictionary<long, CommitIndexEntry> _commits = [];
    private readonly Dictionary<long, TagIndexEntry> _tags = [];
    private readonly List<CommitIndexEntry> _commitsInOrder = [];

    public IReadOnlyDictionary<long, BlobIndexEntry> Blobs => _blobs;
    public IReadOnlyDictionary<long, CommitIndexEntry> Commits => _commits;
    public IReadOnlyDictionary<long, TagIndexEntry> Tags => _tags;

    /// <summary>Commits in stream order — parents precede children, so a rewrite pass can walk forward.</summary>
    public IReadOnlyList<CommitIndexEntry> CommitsInOrder => _commitsInOrder;

    public long MaxMark { get; private set; }

    public void Add(FastExportRecord record)
    {
        switch (record)
        {
            case BlobRecord blob:
                if (blob.Mark is { } blobMark)
                {
                    Observe(blobMark);
                    _blobs[blobMark] = new BlobIndexEntry
                    {
                        Mark = blobMark,
                        Payload = blob.Data.SourceSlice,
                        Record = blob
                    };
                }
                break;

            case CommitRecord commit:
                if (commit.Mark is { } commitMark)
                {
                    Observe(commitMark);
                    var entry = new CommitIndexEntry
                    {
                        Mark = commitMark,
                        RefName = commit.RefName,
                        OriginalOid = commit.OriginalOid,
                        MessageOffset = commit.Message.SourceSlice.Offset,
                        Parents = commit.Parents.Select(p => p.DataRefText).ToList(),
                        FileModifies = commit.FileCommands.OfType<FileModify>().ToList(),
                        Record = commit
                    };
                    _commits[commitMark] = entry;
                    _commitsInOrder.Add(entry);
                }
                break;

            case TagRecord tag:
                if (tag.Mark is { } tagMark)
                {
                    Observe(tagMark);
                    _tags[tagMark] = new TagIndexEntry
                    {
                        Mark = tagMark,
                        TagName = tag.TagName,
                        OriginalOid = tag.OriginalOid,
                        Record = tag
                    };
                }
                break;

            case AliasRecord alias:
                Observe(alias.Mark);
                break;
        }
    }

    private void Observe(long mark)
    {
        if (mark > MaxMark) MaxMark = mark;
    }
}
