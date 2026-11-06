namespace KiComLibBackend.Models
{
    public class FootprintDTO
    {
        public int? Id { get; set; }
        public bool Verified { get; set; }
        public string VerificationState { get; set; } = "unverified";
        public Guid? Uuid { get; set; }
        public required int LibraryFileId { get; set; }
        public required string Name { get; set; }
        public required int Type { get; set; }
        public bool BoardOnly { get; set; }
        public bool ExcludeFromPosFiles { get; set; }
        public bool ExcludeFromBom { get; set; }
        public float Height { get; set; }
        public required string Layer { get; set; }
        public string? Description { get; set; }
        public string? Tags { get; set; }
        public string? Property { get; set; }
        public int? Model3dId { get; set; }
        public List<FootprintModelDto>? Models { get; set; }
        public string? Hash { get; set; }
        public string? FileHash { get; set; }
        public int? RevisionNo { get; set; }
        public int? HeadRevisionId { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public DateTime? CreatedAt { get; set; }
    }
}
