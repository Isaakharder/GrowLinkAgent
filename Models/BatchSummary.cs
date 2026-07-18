namespace FlowMasterAgent.Models;

public sealed class BatchSummary
{
    public int Found { get; set; }
    public int Succeeded { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public decimal KgImported { get; set; }
    public int VarietiesImported { get; set; }
    public DateTime? LastSync { get; set; }

    // Last actual CropLink upload attempt (POST /api/v1/climate/imports).
    // A connection test (GET /api/v1/climate/ping) must never touch these.
    public string? LastCropLinkFilename { get; set; }
    public int? LastCropLinkReadingsParsed { get; set; }
    public string? LastCropLinkResult { get; set; } // "Created" | "Duplicate" | "Failed"
    public int? LastCropLinkHttpStatus { get; set; }
    public string? LastCropLinkImportId { get; set; }
    public DateTime? LastCropLinkTime { get; set; }
}
