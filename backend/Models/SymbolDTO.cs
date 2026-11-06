namespace KiComLibBackend.Models
{
    public class SymbolDTO
    {
        public int? Id { get; set; }
        public bool Verified { get; set; }
        public string VerificationState { get; set; } = "unverified";
        public Guid? Uuid { get; set; }
        public required int LibraryFileId { get; set; }
        public required string Name { get; set; }
        public string? Reference { get; set; }
        public bool InBom { get; set; } = true;
        public bool OnBoard { get; set; } = true;
        public bool PinNumbersHidden { get; set; }
        public bool PinNamesHidden { get; set; }
        public float? PinNamesOffset { get; set; }
        public string? Description { get; set; }
        public string? Hash { get; set; }
        public string? FileHash { get; set; }
        public int? RevisionNo { get; set; }
        public int? HeadRevisionId { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public DateTime? CreatedAt { get; set; }
    }
}
