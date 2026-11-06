namespace KiComLibBackend.Models
{
    public class CommitRequest
    {
        public required string Message { get; set; }
        public string? Author { get; set; }
        public string? Client { get; set; }
        public int? BaseCommitId { get; set; }
        public List<CommitChange> Changes { get; set; } = new();
    }

    public class CommitChange
    {
        public required string Entity { get; set; }
        public required string Op { get; set; }
        public Guid? Uuid { get; set; }
        public string? ExpectedHash { get; set; }
        public string? BlobHash { get; set; }
        public string? PreviewBlobHash { get; set; }
        public int? LibraryFileId { get; set; }
        public string? Name { get; set; }
        public string? Reference { get; set; }
        public bool? InBom { get; set; }
        public bool? OnBoard { get; set; }
        public bool? PinNumbersHidden { get; set; }
        public bool? PinNamesHidden { get; set; }
        public float? PinNamesOffset { get; set; }
        public string? Description { get; set; }
        public int? Type { get; set; }
        public bool? BoardOnly { get; set; }
        public bool? ExcludeFromPosFiles { get; set; }
        public bool? ExcludeFromBom { get; set; }
        public float? Height { get; set; }
        public string? Layer { get; set; }
        public string? Tags { get; set; }
        public string? Property { get; set; }
        public string? Directory { get; set; }
        public string? Filename { get; set; }
        public string? Comment { get; set; }
        public List<FootprintModelChange>? Models { get; set; }
    }

    public class FootprintModelChange
    {
        public short Position { get; set; }
        public Guid? Model3dUuid { get; set; }
        public float[]? Offset { get; set; }
        public float[]? Scale { get; set; }
        public float[]? Rotate { get; set; }
        public float? Opacity { get; set; }
        public bool Hide { get; set; }
    }

    public class CommitResultItem
    {
        public string Entity { get; set; } = "";
        public Guid Uuid { get; set; }
        public int Id { get; set; }
        public int RevisionId { get; set; }
        public int RevisionNo { get; set; }
        public string? Hash { get; set; }
    }

    public class CommitResponse
    {
        public int CommitId { get; set; }
        public List<CommitResultItem> Results { get; set; } = new();
    }

    public class RevertRequest
    {
        public string? Author { get; set; }
        public string? Message { get; set; }

        /// <summary>
        /// Copy this revision forward so it becomes the new head.
        /// 0 undoes a create when revision 1 is still the head.
        /// </summary>
        public int? RevisionNo { get; set; }

        /// <summary>When reverting a commit, limit the undo to this entity.</summary>
        public string? Entity { get; set; }

        /// <summary>When reverting a commit, limit the undo to this entity id.</summary>
        public int? Id { get; set; }
    }

    public class ConflictItem
    {
        public string Entity { get; set; } = "";
        public Guid? Uuid { get; set; }
        public string? ExpectedHash { get; set; }
        public string? ActualHash { get; set; }
        public int? HeadRevisionId { get; set; }
        public string? Reason { get; set; }
    }

    public class InUseItem
    {
        public string Entity { get; set; } = "";
        public Guid Uuid { get; set; }
        public string Name { get; set; } = "";
        public List<InUseReference> UsedBy { get; set; } = new();
    }

    public class InUseReference
    {
        public string Entity { get; set; } = "";
        public int? Id { get; set; }
        public string Name { get; set; } = "";
    }

    public class FootprintModelDto
    {
        public short Position { get; set; }
        public int Model3dId { get; set; }
        public Guid? Model3dUuid { get; set; }
        public string? Directory { get; set; }
        public string? Filename { get; set; }
        public float[] Offset { get; set; } = new float[] { 0, 0, 0 };
        public float[] Scale { get; set; } = new float[] { 1, 1, 1 };
        public float[] Rotate { get; set; } = new float[] { 0, 0, 0 };
        public float? Opacity { get; set; }
        public bool Hide { get; set; }
    }

    public class SymbolDetailDto
    {
        public bool Verified { get; set; }
        public string VerificationState { get; set; } = "unverified";
        public int? Id { get; set; }
        public Guid? Uuid { get; set; }
        public int LibraryFileId { get; set; }
        public string Name { get; set; } = "";
        public string? Reference { get; set; }
        public bool InBom { get; set; }
        public bool OnBoard { get; set; }
        public bool PinNumbersHidden { get; set; }
        public bool PinNamesHidden { get; set; }
        public float? PinNamesOffset { get; set; }
        public string? Description { get; set; }
        public string? Data { get; set; }
        public string? Hash { get; set; }
        public string? FileHash { get; set; }
        public int? RevisionNo { get; set; }
        public int? HeadRevisionId { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public DateTime? CreatedAt { get; set; }
    }

    public class FootprintDetailDto
    {
        public bool Verified { get; set; }
        public string VerificationState { get; set; } = "unverified";
        public int? Id { get; set; }
        public Guid? Uuid { get; set; }
        public int LibraryFileId { get; set; }
        public string Name { get; set; } = "";
        public int Type { get; set; }
        public bool BoardOnly { get; set; }
        public bool ExcludeFromPosFiles { get; set; }
        public bool ExcludeFromBom { get; set; }
        public float Height { get; set; }
        public string Layer { get; set; } = "";
        public string? Description { get; set; }
        public string? Tags { get; set; }
        public string? Property { get; set; }
        public string? Data { get; set; }
        public string? Hash { get; set; }
        public string? FileHash { get; set; }
        public int? RevisionNo { get; set; }
        public int? HeadRevisionId { get; set; }
        public int? Model3dId { get; set; }
        public List<FootprintModelDto> Models { get; set; } = new();
        public DateTime? UpdatedAt { get; set; }
        public DateTime? CreatedAt { get; set; }
    }
}
